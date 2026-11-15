using System.Globalization;
using System.Net.Sockets;
using System.Text;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>One file or folder in an FTP listing.</summary>
public sealed record FtpEntry(string Name, bool IsDir, long Size, DateTime? ModifiedUtc);

/// <summary>An FTP reply the console's server refused (4xx/5xx).</summary>
public sealed class FtpException(int code, string message) : IOException(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// A small READ-ONLY FTP client (passive mode) for the FTP server on a Switch / 3DS SD card: Checkpoint's built-in server
/// (mtheall's ftpd, port 50000) or the standalone ftpd app (port 5000). It only lists folders and downloads files - it never
/// sends a command that changes anything on the console (no STOR / DELE / RNFR / MKD / RMD).
/// </summary>
public sealed class ConsoleFtp : IDisposable
{
    private readonly TcpClient _ctl;
    private readonly NetworkStream _s;
    private readonly StreamReader _r;
    private readonly string _host;
    private readonly int _timeoutMs;

    private ConsoleFtp(TcpClient ctl, string host, int timeoutMs)
    {
        _ctl = ctl;
        _host = host;
        _timeoutMs = timeoutMs;
        _s = ctl.GetStream();
        _r = new StreamReader(_s, new UTF8Encoding(false));
    }

    /// <summary>Connects and logs in anonymously (ftpd accepts any user when none is configured).</summary>
    public static ConsoleFtp Connect(string host, int port, int timeoutMs = 15000)
    {
        var c = new TcpClient();
        if (!c.ConnectAsync(host, port).Wait(Math.Min(timeoutMs, 4000)) || !c.Connected)
        {
            c.Dispose();
            throw new IOException($"Nothing answers at {host}:{port}.");
        }
        c.ReceiveTimeout = c.SendTimeout = timeoutMs;
        var f = new ConsoleFtp(c, host, timeoutMs);
        try
        {
            Expect(f.Read(), 220);
            var (code, text) = f.Cmd("USER anonymous");
            if (code == 331) (code, text) = f.Cmd("PASS shinyforge@");
            if (code != 230) throw new FtpException(code, "The console's FTP server refused the login: " + text);
            f.Cmd("OPTS UTF8 ON"); // file names with é; harmless if unsupported
            Expect(f.Cmd("TYPE I"), 200);
            return f;
        }
        catch { f.Dispose(); throw; }
    }

    private (int Code, string Text) Read()
    {
        var line = _r.ReadLine() ?? throw new IOException("The console closed the FTP connection.");
        if (line.Length < 3 || !int.TryParse(line[..3], out var code)) throw new IOException("Unexpected FTP reply: " + line);
        var text = new StringBuilder(line);
        if (line.Length > 3 && line[3] == '-')
        {
            // multi-line reply: ends with "<code> "
            string? l;
            while ((l = _r.ReadLine()) != null)
            {
                text.Append('\n').Append(l);
                if (l.StartsWith(line[..3] + " ")) break;
            }
        }
        return (code, text.ToString());
    }

    private (int Code, string Text) Cmd(string command)
    {
        var bytes = Encoding.UTF8.GetBytes(command + "\r\n");
        _s.Write(bytes, 0, bytes.Length);
        return Read();
    }

    private static void Expect((int Code, string Text) reply, params int[] ok)
    {
        if (!ok.Contains(reply.Code)) throw new FtpException(reply.Code, reply.Text);
    }

    /// <summary>PASV: the server's data port; the connection goes to the control host (the address in the reply can be 0.0.0.0).</summary>
    private TcpClient OpenData()
    {
        var (code, text) = Cmd("PASV");
        if (code != 227) throw new FtpException(code, text);
        var a = text.IndexOf('('); var b = text.IndexOf(')', a + 1);
        var parts = (a >= 0 && b > a ? text[(a + 1)..b] : text[4..]).Split(',').Select(p => int.Parse(p.Trim(), CultureInfo.InvariantCulture)).ToArray();
        var port = parts[4] * 256 + parts[5];
        var d = new TcpClient();
        if (!d.ConnectAsync(_host, port).Wait(_timeoutMs) || !d.Connected) { d.Dispose(); throw new IOException("Couldn't open the FTP data connection."); }
        d.ReceiveTimeout = d.SendTimeout = _timeoutMs;
        return d;
    }

    /// <summary>The folder's entries (MLSD; LIST as a fallback). Throws <see cref="FtpException"/> (5xx) if it doesn't exist.</summary>
    public List<FtpEntry> List(string path)
    {
        try { return ListWith("MLSD", path, ParseMlsd); }
        catch (FtpException e) when (e.Code is 500 or 501 or 502 or 504) { return ListWith("LIST", path, ParseList); }
    }

    private List<FtpEntry> ListWith(string verb, string path, Func<string, FtpEntry?> parse)
    {
        using var data = OpenData();
        Expect(Cmd($"{verb} {path}"), 125, 150);
        string all;
        using (var sr = new StreamReader(data.GetStream(), new UTF8Encoding(false))) all = sr.ReadToEnd();
        Expect(Read(), 226, 250);
        return all.Split('\n').Select(l => parse(l.TrimEnd('\r'))).OfType<FtpEntry>()
            .Where(e => e.Name is not ("." or "..")).ToList();
    }

    /// <summary>"type=dir;size=0;modify=20261101120000; name"</summary>
    internal static FtpEntry? ParseMlsd(string line)
    {
        var sp = line.IndexOf(' ');
        if (sp <= 0) return null;
        var facts = line[..sp].Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Split('=', 2)).Where(f => f.Length == 2).ToDictionary(f => f[0].ToLowerInvariant(), f => f[1]);
        var type = facts.GetValueOrDefault("type", "").ToLowerInvariant();
        if (type is "cdir" or "pdir") return null;
        long.TryParse(facts.GetValueOrDefault("size", "0"), out var size);
        DateTime? mod = DateTime.TryParseExact(facts.GetValueOrDefault("modify", "").Split('.')[0], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var m) ? m : null;
        return new FtpEntry(line[(sp + 1)..], type == "dir", size, mod);
    }

    /// <summary>Unix-style "drwxr-xr-x 1 user group 0 Nov 01 12:00 name with spaces".</summary>
    internal static FtpEntry? ParseList(string line)
    {
        var p = line.Split(' ', 9, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 9 || p[0].Length < 10) return null;
        long.TryParse(p[4], out var size);
        return new FtpEntry(p[8], p[0][0] == 'd', size, null);
    }

    /// <summary>Downloads one file to <paramref name="localFile"/> (written to a .part file first).</summary>
    public void Download(string path, string localFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);
        var part = localFile + ".part";
        using (var data = OpenData())
        {
            Expect(Cmd($"RETR {path}"), 125, 150);
            using (var fs = File.Create(part)) data.GetStream().CopyTo(fs);
        }
        Expect(Read(), 226, 250);
        File.Move(part, localFile, true);
    }

    public void Dispose()
    {
        try { if (_ctl.Connected) { var q = Encoding.ASCII.GetBytes("QUIT\r\n"); _s.Write(q, 0, q.Length); } } catch { }
        _r.Dispose();
        _ctl.Dispose();
    }
}

/// <summary>
/// Reads Checkpoint backups straight from the SD card INSIDE the Switch / 3DS over WiFi - no need to take the card out.
/// The console runs an FTP server: Checkpoint's own (Settings → FTP server, port 50000) or the ftpd app (port 5000).
/// Only this game's Checkpoint folders are downloaded, into a local mirror (<see cref="MirrorRoot"/>) laid out exactly like
/// the SD card, so <see cref="CheckpointService.FindBackups"/> reads them like a card in the PC. Nothing on the console is changed.
/// </summary>
public static class ConsoleSdService
{
    /// <summary>Checkpoint's built-in FTP server, then the standalone ftpd app (Switch and 3DS).</summary>
    public static readonly int[] FtpPorts = { 50000, 5000 };

    private const long MaxFile = 64L * 1024 * 1024; // Pokémon saves are at most a few MB

    /// <summary>The Checkpoint saves folder on the console's SD card.</summary>
    public static string RemoteSaves(bool switchGame) => switchGame ? "/switch/Checkpoint/saves" : "/3ds/Checkpoint/saves";

    /// <summary>The local mirror of the console's saves folder (a cache; the chosen backup is still copied to SavesDir).</summary>
    public static string MirrorRoot(bool switchGame) => Path.Combine(AppPaths.DataDir, "Console SD", switchGame ? "Switch" : "3DS");

    /// <summary>The first FTP port that answers on the console, or null.</summary>
    public static int? FindPort(string host, IEnumerable<int>? ports = null)
    {
        foreach (var p in ports ?? FtpPorts)
            if (HealthMonitor.CanReach(host, p, 2500)) return p;
        return null;
    }

    public sealed record MirrorResult(string Root, int TitleFolders, int Files, int Downloaded);

    /// <summary>
    /// Downloads this game's Checkpoint title folders from the console into <paramref name="localRoot"/> (default
    /// <see cref="MirrorRoot"/>). Unchanged files (same size and time) aren't downloaded again; backups deleted on the
    /// console are removed from the mirror.
    /// </summary>
    public static MirrorResult Mirror(Game game, string host, int port, Action<string>? progress = null, string? localRoot = null,
        CancellationToken ct = default)
    {
        localRoot ??= MirrorRoot(game.IsSwitch);
        using var ftp = ConsoleFtp.Connect(host, port);
        var remote = RemoteSaves(game.IsSwitch);
        List<FtpEntry> titles;
        try { titles = ftp.List(remote); }
        catch (FtpException e) when (e.Code >= 500)
        {
            throw new IOException($"There's no {remote} folder on the console's SD card - is Checkpoint installed, and has it backed up a game yet?");
        }
        var mine = titles.Where(t => t.IsDir && CheckpointService.Matches(game, t.Name)).ToList();
        Directory.CreateDirectory(localRoot);
        // title folders of this game that are gone from the console
        foreach (var d in Directory.EnumerateDirectories(localRoot))
            if (CheckpointService.Matches(game, Path.GetFileName(d)) && !mine.Any(t => t.Name == Path.GetFileName(d)))
                Directory.Delete(d, true);
        int files = 0, downloaded = 0;
        foreach (var t in mine)
            MirrorDir(ftp, remote + "/" + t.Name, Path.Combine(localRoot, t.Name), 3, progress, ref files, ref downloaded, ct);
        AppPaths.Log($"Console SD ({host}:{port}): {mine.Count} {game.Title} folder(s), {files} files, {downloaded} downloaded");
        return new MirrorResult(localRoot, mine.Count, files, downloaded);
    }

    private static void MirrorDir(ConsoleFtp ftp, string remote, string local, int depth, Action<string>? progress,
        ref int files, ref int downloaded, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entries = ftp.List(remote);
        Directory.CreateDirectory(local);
        // drop what isn't on the console any more (only inside this mirror)
        foreach (var f in Directory.EnumerateFiles(local))
            if (!entries.Any(e => !e.IsDir && e.Name == Path.GetFileName(f))) File.Delete(f);
        foreach (var d in Directory.EnumerateDirectories(local))
            if (!entries.Any(e => e.IsDir && e.Name == Path.GetFileName(d))) Directory.Delete(d, true);

        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(local, e.Name);
            if (e.IsDir)
            {
                if (depth > 0) MirrorDir(ftp, remote + "/" + e.Name, target, depth - 1, progress, ref files, ref downloaded, ct);
                continue;
            }
            if (e.Size > MaxFile) continue;
            files++;
            var when = e.ModifiedUtc?.ToLocalTime();
            var fi = new FileInfo(target);
            if (fi.Exists && fi.Length == e.Size && (when == null || fi.LastWriteTime == when)) continue;
            progress?.Invoke($"Downloading {remote}/{e.Name} ({e.Size / 1024:N0} KB)...");
            ftp.Download(remote + "/" + e.Name, target);
            if (when != null) File.SetLastWriteTime(target, when.Value);
            downloaded++;
        }
    }

    /// <summary>What to tell the user when no FTP server answers.</summary>
    public static string HowToTurnOn(bool switchGame, string host) =>
        $"No FTP server answers on the {(switchGame ? "Switch" : "3DS")} at {host} (ports {string.Join(" / ", FtpPorts)}).\n\n" +
        "To let ShinyForge read the SD card inside the console:\n" +
        "  1. Open Checkpoint on the console.\n" +
        "  2. Turn on its FTP server in Checkpoint's settings (Checkpoint then shows \"FTP server running on <IP>:50000\").\n" +
        "     Or start the ftpd app instead (port 5000).\n" +
        "  3. Leave Checkpoint (or ftpd) open while ShinyForge reads the saves.\n\n" +
        $"The {(switchGame ? "Switch" : "3DS")} must be on the same WiFi network as this computer.";
}
