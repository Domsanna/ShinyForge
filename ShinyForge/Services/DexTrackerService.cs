using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PKHeX.Core;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// DexTracker (TheNinjask/DexTracker, MIT) is ShinyForge's Living Dex and Pokédex. It is a web app (Assets\DexTracker,
/// built by tools\build-dextracker.ps1) that ShinyForge serves on this PC only (127.0.0.1) and opens in a browser window.
/// <para>
/// DexTracker keeps its savefile in the browser. ShinyForge adds a small script (Assets\shinyforge-dextracker.js) that
/// keeps the savefile in <see cref="SavePath"/> instead: loaded from the file when the page opens, written back on every
/// change. So ShinyForge can mark the Pokémon it finds in your saves (<see cref="MarkCollection"/>), and the page reloads
/// when it does.
/// </para>
/// </summary>
public static class DexTrackerService
{
    public static string AppDir => Path.Combine(AppPaths.AssetsDir, "DexTracker");
    private static string BridgeScript => Path.Combine(AppPaths.AssetsDir, "shinyforge-dextracker.js");
    public static string Folder => Path.Combine(AppPaths.DataDir, "DexTracker");
    public static string SavePath => Path.Combine(Folder, "savefile.json");
    public static string BackupDir => Path.Combine(Folder, "Backups");
    public static bool Installed => File.Exists(Path.Combine(AppDir, "index.html"));
    public static string Version => File.Exists(Path.Combine(AppDir, "version.txt")) ? File.ReadAllText(Path.Combine(AppDir, "version.txt")).Trim() : "";

    private static readonly object FileLock = new();
    private static TcpListener? _listener;
    public static int Port { get; private set; }
    public static string Url => $"http://127.0.0.1:{Port}/";

    // ------------------------------------------------------------------ the savefile on disk

    /// <summary>The savefile's revision (its last write time) - the page compares it to notice ShinyForge's changes.</summary>
    public static string Revision()
    {
        lock (FileLock) return File.Exists(SavePath) ? File.GetLastWriteTimeUtc(SavePath).Ticks.ToString() : "0";
    }

    /// <summary>The savefile, or null if there is none yet.</summary>
    public static JsonObject? Load()
    {
        lock (FileLock)
        {
            if (!File.Exists(SavePath)) return null;
            return JsonNode.Parse(File.ReadAllText(SavePath)) as JsonObject;
        }
    }

    /// <summary>Writes the savefile (keeps the previous one as savefile.json.bak and one dated copy per day). Returns the new revision.</summary>
    public static string Save(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject) throw new InvalidDataException("not a DexTracker savefile");
        lock (FileLock)
        {
            Directory.CreateDirectory(Folder);
            if (File.Exists(SavePath))
            {
                File.Copy(SavePath, SavePath + ".bak", true);
                Directory.CreateDirectory(BackupDir);
                var daily = Path.Combine(BackupDir, $"savefile {DateTime.Now:yyyy-MM-dd}.json");
                if (!File.Exists(daily)) File.Copy(SavePath, daily);
                foreach (var old in Directory.GetFiles(BackupDir, "savefile *.json").OrderByDescending(f => f).Skip(30)) File.Delete(old);
            }
            var tmp = SavePath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, SavePath, true);
            // two writes inside the file system's time resolution would get the same revision - make sure it moves on
            File.SetLastWriteTimeUtc(SavePath, DateTime.UtcNow);
            return File.GetLastWriteTimeUtc(SavePath).Ticks.ToString();
        }
    }

    /// <summary>An empty savefile in DexTracker's format (schema 4, DexTracker 1.5.x).</summary>
    public static JsonObject Empty() => new()
    {
        ["meta"] = new JsonObject { ["schema_version"] = 4 },
        ["species_ownership"] = new JsonArray(), ["form_ownership"] = new JsonArray(), ["per_game_ownership"] = new JsonObject(),
        ["ot_registry"] = new JsonArray(), ["hall_of_fame"] = new JsonArray(), ["switch_profiles"] = new JsonArray(),
        ["cooking_recipes"] = new JsonArray(), ["challenges_completed"] = new JsonArray(), ["research_mons_completed"] = new JsonArray(),
        ["pla_research_counts"] = new JsonObject(),
    };

    // ------------------------------------------------------------------ the local web server

    /// <summary>Starts the server (once) on 127.0.0.1 - port 8766 or the next free one. Only this PC can open it.</summary>
    public static void EnsureRunning()
    {
        if (_listener != null) return;
        for (var port = 8766; port < 8786; port++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                _listener = l;
                Port = port;
                _ = Task.Run(AcceptLoop);
                AppPaths.Log($"DexTracker server on 127.0.0.1:{port}");
                return;
            }
            catch (SocketException) { }
        }
        throw new IOException("No free port for DexTracker (tried 8766-8785).");
    }

    /// <summary>Opens DexTracker - on a game's own Pokédex when there is one (<paramref name="dexId"/>, e.g. "FRLG Dex").</summary>
    public static void Open(string? dexId = null)
    {
        if (!Installed) throw new FileNotFoundException("DexTracker isn't installed with this copy of ShinyForge (Assets\\DexTracker).");
        EnsureRunning();
        var url = Url + (dexId != null ? "?dex=" + Uri.EscapeDataString(dexId) : "");
        // An app window (no tabs / address bar) when Edge or Chrome is there, else the default browser.
        if (OsInfo.IsWindows && AppWindowBrowser() is { } exe)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"--app=\"{url}\"") { UseShellExecute = false }); return; }
            catch (Exception e) { AppPaths.Log("DexTracker app window: " + e.Message); }
        }
        Browser.Open(url);
    }

    private static string? AppWindowBrowser()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"), Path.Combine(pf64, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf64, @"Google\Chrome\Application\chrome.exe"), Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
        };
        // the user's default browser first, if it is one of them
        var def = Browser.Split(Browser.DefaultBrowserCommand())?.Exe;
        if (def != null && candidates.Contains(def, StringComparer.OrdinalIgnoreCase)) return def;
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task AcceptLoop()
    {
        while (_listener != null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(client));
        }
    }

    private static async Task Handle(TcpClient client)
    {
        using var _ = client;
        try
        {
            client.ReceiveTimeout = 10000;
            var stream = client.GetStream();
            var (method, target, headers, body) = await ReadRequest(stream);
            if (method == null) return;
            // Only this server's own page may use it: the Host must be this address (no DNS rebinding) and the savefile
            // calls need a custom header, which another web site can't send without the browser asking first (CORS).
            var host = headers.GetValueOrDefault("host", "");
            if (host != $"127.0.0.1:{Port}" && host != $"localhost:{Port}") { await Send(stream, 403, "text/plain", "forbidden"u8.ToArray()); return; }
            var path = Uri.UnescapeDataString(target.Split('?')[0]);

            if (path.StartsWith("/shinyforge/"))
            {
                if (headers.GetValueOrDefault("x-shinyforge") != "1" ||
                    (headers.TryGetValue("origin", out var origin) && origin != $"http://{host}"))
                { await Send(stream, 403, "text/plain", "forbidden"u8.ToArray()); return; }
                await HandleApi(stream, method, path, headers, body);
                return;
            }
            if (method != "GET" && method != "HEAD") { await Send(stream, 405, "text/plain", "method not allowed"u8.ToArray()); return; }

            if (path == "/" || path == "/index.html") { await Send(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(IndexHtml())); return; }
            if (path == "/shinyforge-dextracker.js") { await Send(stream, 200, "text/javascript; charset=utf-8", await File.ReadAllBytesAsync(BridgeScript)); return; }
            var file = Path.GetFullPath(Path.Combine(AppDir, path.TrimStart('/')));
            if (!file.StartsWith(Path.GetFullPath(AppDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            {
                // DexTracker is a single page: unknown paths get the page itself
                if (!Path.HasExtension(path)) await Send(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(IndexHtml()));
                else await Send(stream, 404, "text/plain", "not found"u8.ToArray());
                return;
            }
            await Send(stream, 200, ContentType(file), await File.ReadAllBytesAsync(file));
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { /* browser went away */ }
        catch (Exception e) { AppPaths.Log("DexTracker server: " + e.Message); }
    }

    private static async Task HandleApi(NetworkStream stream, string method, string path, Dictionary<string, string> headers, byte[] body)
    {
        switch (method, path)
        {
            case ("GET", "/shinyforge/rev"):
                await SendJson(stream, 200, new JsonObject { ["rev"] = Revision() });
                break;
            case ("GET", "/shinyforge/savefile"):
                JsonObject? data;
                string rev;
                lock (FileLock) { rev = Revision(); data = Load(); }
                await SendJson(stream, 200, new JsonObject { ["rev"] = rev, ["data"] = data });
                break;
            case ("PUT", "/shinyforge/savefile"):
                // the page's change is based on the revision it loaded: if ShinyForge wrote in between, the page reloads instead
                var baseRev = headers.GetValueOrDefault("x-base-rev", "");
                string? newRev = null;
                try
                {
                    lock (FileLock)
                    {
                        if (baseRev == Revision()) newRev = Save(Encoding.UTF8.GetString(body));
                    }
                }
                catch (Exception e) when (e is JsonException or InvalidDataException)
                {
                    await Send(stream, 400, "text/plain", Encoding.UTF8.GetBytes("not a DexTracker savefile: " + e.Message));
                    break;
                }
                if (newRev == null) await SendJson(stream, 409, new JsonObject { ["rev"] = Revision() });
                else await SendJson(stream, 200, new JsonObject { ["rev"] = newRev });
                break;
            default:
                await Send(stream, 404, "text/plain", "not found"u8.ToArray());
                break;
        }
    }

    /// <summary>DexTracker's page with ShinyForge's script added first, and without the offline service worker.</summary>
    private static string IndexHtml()
    {
        var html = File.ReadAllText(Path.Combine(AppDir, "index.html"));
        html = System.Text.RegularExpressions.Regex.Replace(html, "<script id=\"vite-plugin-pwa:register-sw\"[^>]*></script>", "");
        html = System.Text.RegularExpressions.Regex.Replace(html, "<link rel=\"manifest\"[^>]*>", "");
        var i = html.IndexOf("<script", StringComparison.OrdinalIgnoreCase);
        const string tag = "<script src=\"/shinyforge-dextracker.js\"></script>\n  ";
        return i < 0 ? html.Replace("</head>", tag + "</head>") : html.Insert(i, tag);
    }

    private static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8", ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp",
        ".html" => "text/html; charset=utf-8", ".webmanifest" => "application/manifest+json", ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    private static async Task<(string? Method, string Target, Dictionary<string, string> Headers, byte[] Body)> ReadRequest(NetworkStream stream)
    {
        // headers byte by byte up to the blank line (the body follows and must not be read as text)
        var head = new List<byte>();
        var one = new byte[1];
        while (head.Count < 65536)
        {
            if (await stream.ReadAsync(one) == 0) return (null, "", new(), Array.Empty<byte>());
            head.Add(one[0]);
            if (head.Count >= 4 && head[^1] == '\n' && head[^2] == '\r' && head[^3] == '\n' && head[^4] == '\r') break;
        }
        var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return (null, "", new(), Array.Empty<byte>());
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1))
        {
            var c = l.IndexOf(':');
            if (c > 0) headers[l[..c].Trim().ToLowerInvariant()] = l[(c + 1)..].Trim();
        }
        var length = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var n) ? n : 0;
        if (length > 64 * 1024 * 1024) throw new InvalidDataException("request too large");
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var r = await stream.ReadAsync(body.AsMemory(read));
            if (r == 0) break;
            read += r;
        }
        return (first[0], first[1], headers, body);
    }

    private static Task SendJson(NetworkStream s, int code, JsonObject o) => Send(s, code, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(o.ToJsonString()));

    private static async Task Send(NetworkStream s, int code, string type, byte[] body)
    {
        var reason = code switch { 200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 409 => "Conflict", _ => "Error" };
        var head = $"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-cache\r\n" +
                   "X-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await s.WriteAsync(body);
    }

    // ------------------------------------------------------------------ games -> DexTracker's dexes

    /// <summary>DexTracker's own Pokédex for a game (opened from its page), or null = the National Dex.</summary>
    public static string? DexFor(Game game) => GameDexes(game).FirstOrDefault();

    /// <summary>The game Pokédexes a game's catches count for (DexTracker has them for the Switch games and FireRed / LeafGreen).</summary>
    public static string[] GameDexes(Game game) => DexGroup(game.Versions.FirstOrDefault()) switch
    {
        "FRLG" => new[] { "FRLG Dex" },
        "LGPE" => new[] { "LGPE Dex" },
        "SWSH" => new[] { "SwSh Dex", "SwSh-IoA Dex", "SwSh-TCT Dex", "SwSh-Not In Dex" },
        "BDSP" => new[] { "BDSP Dex" },
        "PLA" => new[] { "PLA Dex" },
        "SV" => new[] { "SV Dex", "SV-TTM Dex", "SV-TID Dex", "SV-Not In Dex" },
        "ZA" => new[] { "PLZA Dex", "PLZA-MD Dex" },
        _ => Array.Empty<string>(),
    };

    /// <summary>Versions that share one Pokédex in DexTracker.</summary>
    private static string DexGroup(GameVersion v) => v switch
    {
        GameVersion.FR or GameVersion.LG => "FRLG",
        GameVersion.GP or GameVersion.GE => "LGPE",
        GameVersion.SW or GameVersion.SH => "SWSH",
        GameVersion.BD or GameVersion.SP => "BDSP",
        GameVersion.PLA => "PLA",
        GameVersion.SL or GameVersion.VL => "SV",
        GameVersion.ZA => "ZA",
        _ => v.ToString(),
    };
    /// <summary>DexTracker's game names (its "Game" list) for PKHeX's versions - the origin of an OT Registry entry.</summary>
    public static string GameName(GameVersion v) => v switch
    {
        GameVersion.RD or GameVersion.GN or GameVersion.BU or GameVersion.YW => "Yellow",
        GameVersion.GD or GameVersion.SI or GameVersion.C => "Silver",
        GameVersion.R => "Ruby", GameVersion.S => "Sapphire", GameVersion.E => "Emerald", GameVersion.FR => "FireRed", GameVersion.LG => "LeafGreen",
        GameVersion.CXD => "Colosseum", GameVersion.D => "Diamond", GameVersion.P => "Pearl", GameVersion.Pt => "Platinum",
        GameVersion.HG => "Heart Gold", GameVersion.SS => "Soul Silver", GameVersion.B => "Black", GameVersion.W => "White",
        GameVersion.B2 => "Black 2", GameVersion.W2 => "White 2", GameVersion.X => "X", GameVersion.Y => "Y",
        GameVersion.OR => "Omega Ruby", GameVersion.AS => "Alpha Sapphire", GameVersion.SN => "Sun", GameVersion.MN => "Moon",
        GameVersion.US => "Ultra Sun", GameVersion.UM => "Ultra Moon", GameVersion.GP => "Let's Go Pikachu", GameVersion.GE => "Let's Go Eevee",
        GameVersion.SW => "Sword", GameVersion.SH => "Shield", GameVersion.BD => "Brilliant Diamond", GameVersion.SP => "Shining Pearl",
        GameVersion.PLA => "Legends: Arceus", GameVersion.SL => "Scarlet", GameVersion.VL => "Violet", GameVersion.ZA => "Legends: ZA",
        GameVersion.GO => "Go",
        _ => "",
    };

    // ------------------------------------------------------------------ marking what you own

    public sealed record MarkResult(int Species, int Shiny, int Forms, int GameDex, int NewTrainers);

    private sealed class Reference
    {
        public HashSet<string> Species = new();
        public List<(string Nat, string Form, string FormCode, string? FormCodeBase, string BoxGroup)> Forms = new();
        public Dictionary<string, Dictionary<string, string?>> DexMappings = new(); // dex -> national -> regional ("Not In" dexes: none)
    }

    private static Reference? _ref;

    private static Reference Ref()
    {
        if (_ref != null) return _ref;
        var j = JsonNode.Parse(File.ReadAllText(Path.Combine(AppDir, "data", "reference_data.json")))!;
        var r = new Reference();
        foreach (var s in j["species"]!.AsArray()) r.Species.Add(s!["national_no"]!.GetValue<string>());
        foreach (var f in j["forms"]!.AsArray())
            r.Forms.Add((f!["national_no"]!.GetValue<string>(), f["form"]?.GetValue<string>() ?? "", f["form_code"]?.GetValue<string>() ?? "",
                         f["form_code_base"]?.GetValue<string>(), f["box_group"]?.GetValue<string>() ?? ""));
        foreach (var (dex, rows) in j["dex_mappings"]!.AsObject())
            r.DexMappings[dex] = rows!.AsArray().GroupBy(m => m!["national_no"]!.GetValue<string>())
                                      .ToDictionary(g => g.Key, g => g.First()!["regional_no"]?.GetValue<string>());
        return _ref = r;
    }

    /// <summary>DexTracker writes National Dex numbers with 4 digits ("0025").</summary>
    public static string Nat(int species) => species.ToString("0000");

    /// <summary>
    /// Marks every Pokémon of a save (or live game) in DexTracker: the National / Shiny National Dex, the Form Dexes where the
    /// form is one DexTracker lists, and the game's own Pokédex. Only empty slots are filled - nothing you entered is changed.
    /// Each Pokémon's trainer (OT + ID) is found in DexTracker's OT Registry, or added to it.
    /// </summary>
    public static MarkResult MarkCollection(Collection owned, Game game, Action<string>? log = null)
    {
        if (!Installed) throw new FileNotFoundException("DexTracker isn't installed (Assets\\DexTracker).");
        var reference = Ref();
        lock (FileLock)
        {
            var save = Load() ?? Empty();
            var species = Arr(save, "species_ownership");
            var forms = Arr(save, "form_ownership");
            var registry = Arr(save, "ot_registry");
            var perGame = save["per_game_ownership"] as JsonObject ?? (JsonObject)(save["per_game_ownership"] = new JsonObject());
            int sp = 0, sh = 0, fo = 0, gd = 0, newOt = 0;
            var dexes = GameDexes(game);

            foreach (var m in owned.Mons)
            {
                var pk = m.Pk;
                var nat = Nat(pk.Species);
                if (!reference.Species.Contains(nat)) continue;
                var otId = OtEntry(registry, pk, m.OwnOT, ref newOt);
                var slot = pk.IsShiny ? "shiny" : "normal";

                var row = species.FirstOrDefault(r => r?["national_no"]?.GetValue<string>() == nat) as JsonObject;
                if (row == null) species.Add(row = new JsonObject { ["national_no"] = nat });
                if (row[slot] == null) { row[slot] = new JsonObject { ["ot_id"] = otId }; if (pk.IsShiny) sh++; else sp++; }

                foreach (var f in FormsOf(pk, reference))
                {
                    var frow = forms.FirstOrDefault(r => r?["national_no"]?.GetValue<string>() == nat && (r["form_code"]?.GetValue<string>() ?? "") == f.FormCode &&
                                                          (r["form"]?.GetValue<string>() ?? "") == f.Form && r["form_code_base"]?.GetValue<string>() == f.FormCodeBase) as JsonObject;
                    if (frow == null)
                    {
                        frow = new JsonObject { ["national_no"] = nat, ["form"] = f.Form, ["form_code"] = f.FormCode };
                        if (f.FormCodeBase != null) frow["form_code_base"] = f.FormCodeBase;
                        forms.Add(frow);
                    }
                    if (frow[slot] == null) { frow[slot] = new JsonObject { ["ot_id"] = otId }; fo++; }
                }

                // the game's own Pokédex: only Pokémon that came from this game
                if (DexTrackerOrigin(pk, game))
                    foreach (var dex in dexes)
                    {
                        if (!reference.DexMappings.TryGetValue(dex, out var map) || !map.TryGetValue(nat, out var regional)) continue;
                        var rows = perGame[dex] as JsonArray ?? (JsonArray)(perGame[dex] = new JsonArray());
                        if (rows.Any(r => r?["national_no"]?.GetValue<string>() == nat)) break;
                        rows.Add(new JsonObject { ["national_no"] = nat, ["regional_no"] = regional, ["ot_id"] = otId, ["is_mine"] = m.OwnOT });
                        gd++;
                        break;
                    }
            }
            if (sp + sh + fo + gd + newOt > 0)
            {
                Save(save.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                log?.Invoke($"DexTracker: {sp} new Pokémon, {sh} new shiny, {fo} new form(s), {gd} new in the game's Pokédex" +
                            (newOt > 0 ? $", {newOt} trainer(s) added to the OT Registry" : "") + ".");
            }
            else log?.Invoke("DexTracker: already up to date.");
            return new MarkResult(sp, sh, fo, gd, newOt);
        }
    }

    /// <summary>
    /// Before DexTracker opens from a game: adds the Pokémon of that game's save (only if the save really is from that game -
    /// a save of another game is skipped, so nothing lands in the wrong game's Pokédex). Never throws.
    /// </summary>
    public static void AddGameSave(FoundSave? save, Game game, Action<string> log)
    {
        if (save == null) { log($"DexTracker: no {game.Title} save found - opening without adding Pokémon (set the save in the Collection Tracker card)."); return; }
        if (!game.Versions.Contains(save.Save.Version))
        {
            log($"DexTracker: the save {Path.GetFileName(save.Path)} is from Pokémon {save.Save.Version}, not {game.Title} - not added.");
            return;
        }
        try
        {
            log($"DexTracker: adding the Pokémon of {Path.GetFileName(save.Path)} ({game.Title}, {save.Save.OT})...");
            MarkCollection(Collection.FromSave(save.Save, save.Path), game, log);
        }
        catch (Exception e) { log("DexTracker: couldn't add the save: " + e.Message); }
    }

    private static JsonArray Arr(JsonObject o, string key) => o[key] as JsonArray ?? (JsonArray)(o[key] = new JsonArray());

    /// <summary>Did this Pokémon come from the game (not traded in / transferred from HOME)? FireRed and LeafGreen share a Pokédex.</summary>
    private static bool DexTrackerOrigin(PKM pk, Game game) => game.Versions.Any(v => DexGroup(v) == DexGroup(pk.Version));
    /// <summary>DexTracker's Form Dex entries this Pokémon fills: a regional / other form by name, the female look, Gigantamax.</summary>
    private static IEnumerable<(string Form, string FormCode, string? FormCodeBase)> FormsOf(PKM pk, Reference r)
    {
        var nat = Nat(pk.Species);
        var all = r.Forms.Where(f => f.Nat == nat).ToList();
        if (all.Count == 0) yield break;
        var names = FormConverter.GetFormList(pk.Species, GameInfo.Strings.types, GameInfo.Strings.forms, GameInfo.GenderSymbolASCII, pk.Context);
        var formName = pk.Form < names.Length ? names[pk.Form] : "";
        if (pk.Form > 0 && formName.Length > 0)
        {
            var hit = all.FirstOrDefault(f => f.BoxGroup == "Forms" && (string.Equals(f.Form, formName, StringComparison.OrdinalIgnoreCase) ||
                                                                         formName.StartsWith(f.Form, StringComparison.OrdinalIgnoreCase) && f.Form.Length >= 4));
            if (hit.Nat != null) yield return (hit.Form, hit.FormCode, hit.FormCodeBase);
        }
        if (pk.Gender == 1 && all.FirstOrDefault(f => f.BoxGroup == "Female") is { Nat: not null } fem)
            yield return (fem.Form, fem.FormCode, fem.FormCodeBase);
        if (pk is IGigantamaxReadOnly { CanGigantamax: true } && all.FirstOrDefault(f => f.BoxGroup == "Gigantamax") is { Nat: not null } gmax)
            yield return (gmax.Form, gmax.FormCode, gmax.FormCodeBase);
    }

    /// <summary>The OT Registry entry for this Pokémon's trainer (OT name + ID as the game shows it), added if missing.</summary>
    private static string OtEntry(JsonArray registry, PKM pk, bool mine, ref int added)
    {
        var ot = pk.OriginalTrainerName.Trim();
        var tid = pk.Format >= 7 ? pk.DisplayTID.ToString("000000") : pk.DisplayTID.ToString("00000");
        var game = GameName(pk.Version);
        static bool SameId(string? a, string b) => a != null && (a.Trim() == b || int.TryParse(a, out var x) && int.TryParse(b, out var y) && x == y);
        var hit = registry.FirstOrDefault(r => r?["ot"]?.GetValue<string>()?.Trim() == ot && SameId(r?["tid"]?.GetValue<string>(), tid) &&
                                               (string.IsNullOrEmpty(game) || (r?["game"]?.GetValue<string>() ?? "") is var g && (g == game || g == "")))
                  ?? registry.FirstOrDefault(r => r?["ot"]?.GetValue<string>()?.Trim() == ot && SameId(r?["tid"]?.GetValue<string>(), tid));
        if (hit?["id"]?.GetValue<string>() is { } id) return id;
        var newId = Guid.NewGuid().ToString();
        registry.Add(new JsonObject
        {
            ["id"] = newId, ["ot"] = ot, ["tid"] = tid, ["is_mine"] = mine, ["is_go"] = pk.Version == GameVersion.GO,
            ["profile"] = "N/A", ["game"] = game, ["description"] = "Added by ShinyForge",
        });
        added++;
        return newId;
    }
}
