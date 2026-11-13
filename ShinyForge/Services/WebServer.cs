using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// Small built-in web server (no admin rights needed):
///  - Stream overlay for Streamlabs/OBS: http://localhost:PORT/overlay  (Browser Source)  + text files in Data\Overlay
///  - Phone remote on your home network: http://PC-IP:PORT/remote?pin=XXXX  - hunt status, a live Switch screenshot,
///    Start / Pause / Resume / Stop. Protected by a PIN; only reachable from your own network.
/// </summary>
public static class WebServer
{
    private static TcpListener? _listener;
    private static CancellationTokenSource? _cts;
    public static bool Running => _listener != null;
    public static string OverlayDir => Path.Combine(AppPaths.DataDir, "Overlay");
    public static bool ShinyCharm { get; set; }
    private static HuntStatsResult? _stats;
    private static DateTime _statsAt;

    /// <summary>Remote actions the open game page/hunt registers ("start", "pause", "resume", "stop" + a status line).</summary>
    public static Func<string, string>? RemoteAction { get; set; }
    public static Func<string>? RemoteStatus { get; set; }

    public static string Pin
    {
        get
        {
            var s = AppSettings.Current;
            if (string.IsNullOrWhiteSpace(s.RemotePin))
            {
                s.RemotePin = Random.Shared.Next(100000, 999999).ToString();
                s.Save();
            }
            return s.RemotePin;
        }
    }

    public static IEnumerable<string> LanAddresses() =>
        Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).Select(a => a.ToString());

    public static string OverlayUrl => $"http://localhost:{AppSettings.Current.WebPort}/overlay";
    public static string RemoteUrl => $"http://{LanAddresses().FirstOrDefault() ?? "localhost"}:{AppSettings.Current.WebPort}/remote?pin={Pin}";

    /// <summary>Starts/stops the server to match the settings (call after changing them).</summary>
    public static void Apply()
    {
        Stop();
        var s = AppSettings.Current;
        if (!s.OverlayEnabled && !s.RemoteEnabled) return;
        try
        {
            _listener = new TcpListener(s.RemoteEnabled ? IPAddress.Any : IPAddress.Loopback, s.WebPort);
            _listener.Start();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(() => AcceptLoop(token));
            _ = Task.Run(() => OverlayFilesLoop(token));
            AppPaths.Log($"Web server on port {s.WebPort} (overlay={s.OverlayEnabled}, remote={s.RemoteEnabled})");
        }
        catch (Exception e)
        {
            _listener = null;
            HealthMonitor.Report("Overlay / remote", HealthState.Error, $"Couldn't start the web server on port {s.WebPort}: {e.Message}");
        }
    }

    public static void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    private static HuntStatsResult? Stats()
    {
        if (_stats != null && (DateTime.Now - _statsAt).TotalSeconds < 3) return _stats;
        var src = HuntStats.Active();
        _stats = src == null ? null : HuntStats.Compute(src, ShinyCharm);
        _statsAt = DateTime.Now;
        return _stats;
    }

    /// <summary>Text files for Streamlabs "Text (GDI+) → Read from file" sources, refreshed every 5 s.</summary>
    private static async Task OverlayFilesLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (AppSettings.Current.OverlayEnabled && Stats() is { } st)
                {
                    Directory.CreateDirectory(OverlayDir);
                    foreach (var (k, v) in HuntStats.OverlayTexts(st))
                        await File.WriteAllTextAsync(Path.Combine(OverlayDir, k + ".txt"), v, token);
                }
            }
            catch { }
            try { await Task.Delay(5000, token); } catch { }
        }
    }

    private static async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener != null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(token); }
            catch { break; }
            _ = Task.Run(() => Handle(client));
        }
    }

    private static async Task Handle(TcpClient client)
    {
        using var _ = client;
        try
        {
            client.ReceiveTimeout = 5000;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync() ?? "";
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { } // headers
            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return;
            var url = new Uri("http://x" + parts[1]);
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            var path = url.AbsolutePath.TrimEnd('/');
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            var isLocal = remote != null && IPAddress.IsLoopback(remote);
            var s = AppSettings.Current;
            bool PinOk() => q["pin"] == Pin;

            switch (path)
            {
                case "/overlay" when s.OverlayEnabled:
                    await Send(stream, "text/html", OverlayHtml); break;
                case "/api/stats":
                    if (!isLocal && !(s.RemoteEnabled && PinOk())) { await Send(stream, "text/plain", "forbidden", 403); break; }
                    await Send(stream, "application/json", StatsJson()); break;
                case var p when p.StartsWith("/sprite/"):
                    var file = Path.Combine(AppPaths.AssetsDir, "Dex", Path.GetFileName(p[8..]));
                    if (File.Exists(file)) await Send(stream, "image/png", await File.ReadAllBytesAsync(file));
                    else await Send(stream, "text/plain", "not found", 404);
                    break;
                case "/remote" when s.RemoteEnabled:
                    if (!PinOk()) { await Send(stream, "text/html", "<h2>ShinyForge remote</h2><p>Wrong or missing PIN.</p>", 403); break; }
                    await Send(stream, "text/html", RemoteHtml.Replace("__PIN__", Pin)); break;
                case "/api/screen" when s.RemoteEnabled && PinOk():
                    var jpg = await Task.Run(SwitchScreenshot);
                    if (jpg != null) await Send(stream, "image/jpeg", jpg);
                    else await Send(stream, "text/plain", "no screenshot (Switch not reachable)", 503);
                    break;
                case "/api/action" when s.RemoteEnabled && PinOk():
                    var what = q["do"] ?? "";
                    var result = RemoteAction?.Invoke(what) ?? HuntService.RemoteAction(what);
                    AppPaths.Log($"Remote action '{what}' from {remote}: {result}");
                    await Send(stream, "application/json", JsonSerializer.Serialize(new { result })); break;
                default:
                    await Send(stream, "text/plain", "ShinyForge", 404); break;
            }
        }
        catch { /* client went away */ }
    }

    private static string StatsJson()
    {
        var st = Stats();
        var status = RemoteStatus?.Invoke() ?? HuntService.StatusLine();
        if (st == null) return JsonSerializer.Serialize(new { source = "", status });
        var sprite = st.Last == null ? "" : SpriteFor(st.Last.Species);
        return JsonSerializer.Serialize(new
        {
            source = st.Source, status, counter = st.Phase, total = st.Total, shinies = st.ShiniesFound,
            odds = $"1/{Math.Round(1 / st.Odds)}", chance = Math.Round(st.ChanceByNow * 100, 1), rate = Math.Round(st.PerHour),
            milestone = st.Milestone, eta90 = st.Eta90 is { } e ? $"{(int)e.TotalHours}h {e.Minutes:00}m" : "",
            last = st.Last == null ? null : new { st.Last.Species, st.Last.Nature, iv = st.Last.IvTotal, ivs = st.Last.Ivs, time = st.Last.Time.ToString("HH:mm:ss"), st.Last.Shiny },
            sprite,
            phaseTime = st.PhaseStart is { } ps ? $"{(int)(DateTime.Now - ps).TotalHours}h {(DateTime.Now - ps).Minutes:00}m" : "",
        });
    }

    private static string SpriteFor(string species)
    {
        var id = Enumerable.Range(1, 1025).FirstOrDefault(i =>
            string.Equals(PKHeX.Core.SpeciesName.GetSpeciesName((ushort)i, 2), species, StringComparison.OrdinalIgnoreCase));
        return id == 0 ? "" : $"/sprite/{id}.png";
    }

    /// <summary>A screenshot straight from the Switch (sys-botbase pixelPeek) - no capture card needed.</summary>
    public static byte[]? SwitchScreenshot()
    {
        var s = AppSettings.Current;
        try
        {
            using var c = new TcpClient();
            if (!c.ConnectAsync(s.SwitchIP, s.SwitchPort).Wait(2500)) return null;
            c.ReceiveTimeout = 15000;
            var st = c.GetStream();
            var cmd = Encoding.ASCII.GetBytes("configure echoCommands 0\r\npixelPeek\r\n");
            st.Write(cmd, 0, cmd.Length);
            using var r = new StreamReader(st, Encoding.ASCII);
            var hex = r.ReadLine()?.Trim();
            return string.IsNullOrEmpty(hex) || hex.Length % 2 != 0 ? null : Convert.FromHexString(hex);
        }
        catch { return null; }
    }

    private static async Task Send(NetworkStream s, string type, string body, int code = 200) => await Send(s, type + "; charset=utf-8", Encoding.UTF8.GetBytes(body), code);

    private static async Task Send(NetworkStream s, string type, byte[] body, int code = 200)
    {
        var head = $"HTTP/1.1 {code} {(code == 200 ? "OK" : "Error")}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n" +
                   "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await s.WriteAsync(body);
    }

    // ------------------------------------------------------------------ pages
    private const string OverlayHtml = """
<!doctype html><html><head><meta charset="utf-8"><title>ShinyForge overlay</title>
<style>
 body{margin:0;background:transparent;font-family:'Segoe UI',sans-serif;color:#fff}
 .box{display:flex;align-items:center;gap:14px;background:rgba(20,18,34,.78);border:2px solid #7c3aed;border-radius:16px;padding:10px 18px;width:max-content}
 img{width:80px;height:80px;image-rendering:pixelated}
 .n{font-size:44px;font-weight:800;line-height:1}
 .s{font-size:16px;color:#cfc8f5}.g{color:#ffd666}
</style></head><body><div class="box"><img id="sp" src=""><div>
<div class="n" id="c">-</div><div class="s" id="l1"></div><div class="s g" id="l2"></div></div></div>
<script>
async function tick(){try{const r=await fetch('/api/stats');const d=await r.json();
 document.getElementById('c').textContent=(d.counter??0).toLocaleString()+' encounters';
 document.getElementById('l1').textContent=(d.last?d.last.Species+' • ':'')+'odds '+(d.odds||'')+' • '+(d.chance??0)+'% • '+(d.rate??0)+'/h';
 document.getElementById('l2').textContent=(d.shinies?('★ '+d.shinies+'  '):'')+(d.phaseTime||'');
 if(d.sprite)document.getElementById('sp').src=d.sprite;}catch(e){}}
tick();setInterval(tick,2000);
</script></body></html>
""";

    private const string RemoteHtml = """
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>ShinyForge remote</title><style>
 body{margin:0;padding:16px;background:#0e0d16;color:#f0eefa;font-family:system-ui,'Segoe UI',sans-serif}
 h1{font-size:22px;margin:0 0 4px}.m{color:#a09cbe;font-size:14px}
 .card{background:#1a1828;border:1px solid #3a3658;border-radius:14px;padding:14px;margin:12px 0}
 .big{font-size:34px;font-weight:800}.g{color:#34d399}.y{color:#ffd666}
 button{font-size:17px;padding:12px 0;border:0;border-radius:10px;color:#fff;margin:4px 0;width:48%}
 .go{background:#7c3aed}.pa{background:#3a3658}.st{background:#963c50}
 img{width:100%;border-radius:10px;background:#000;min-height:60px}
</style></head><body>
<h1>✨ ShinyForge</h1><div class="m" id="src"></div>
<div class="card"><div class="big" id="c">-</div><div id="l1" class="m"></div><div id="l2" class="y"></div><div id="st" class="g"></div></div>
<div class="card"><button class="go" onclick="act('start')">Start</button> <button class="pa" onclick="act('pause')">Pause</button>
<button class="pa" onclick="act('resume')">Resume</button> <button class="st" onclick="act('stop')">Stop</button><div id="res" class="m"></div></div>
<div class="card"><img id="scr" alt="Switch screen"><button class="pa" style="width:100%" onclick="shot()">Refresh screenshot</button></div>
<script>
const pin='__PIN__';
async function tick(){try{const d=await (await fetch('/api/stats?pin='+pin)).json();
 document.getElementById('src').textContent=d.source||'No hunt log yet';
 document.getElementById('c').textContent=(d.counter??0).toLocaleString()+' encounters';
 document.getElementById('l1').textContent=d.last?('Last: '+d.last.Species+' '+d.last.Nature+' IVs '+d.last.iv+'/186 at '+d.last.time):'';
 document.getElementById('l2').textContent=(d.chance??0)+'% chance by now • '+(d.rate??0)+'/h • '+(d.milestone||'');
 document.getElementById('st').textContent=d.status||'';}catch(e){}}
async function act(w){if(w=='stop'&&!confirm('Stop the hunt?'))return;
 const r=await (await fetch('/api/action?pin='+pin+'&do='+w)).json();document.getElementById('res').textContent=r.result;tick();}
function shot(){document.getElementById('scr').src='/api/screen?pin='+pin+'&t='+Date.now();}
tick();setInterval(tick,4000);shot();
</script></body></html>
""";
}
