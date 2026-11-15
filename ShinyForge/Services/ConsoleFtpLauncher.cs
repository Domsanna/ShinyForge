using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>A decoded picture: 3 bytes (R, G, B) per pixel, row by row.</summary>
public sealed record RgbImage(int Width, int Height, byte[] Pixels);

/// <summary>
/// Starts the FTP app on the Switch so <see cref="ConsoleSdService"/> can read the SD card, using sys-botbase only:
///   1. the game must be closed (checked with getTitleID - the user closes it, ShinyForge never closes a game),
///   2. HOME menu → first tile, launch it while HOLDING R → Atmosphère opens the Homebrew Menu instead of the game,
///   3. step through the Homebrew Menu (D-Pad right), taking a screenshot of each entry and comparing the big icon of the
///      selected app with Checkpoint's and ftpd's real icons (Assets/Homebrew), then press A on the FTP app,
///   4. wait until its FTP server answers.
/// The Homebrew Menu's network loader is NOT used: it writes the app to the SD card first.
/// </summary>
public static class ConsoleFtpLauncher
{
    /// <summary>JPEG → RGB. Set by each program at start (System.Drawing on Windows, Avalonia on macOS / Linux).</summary>
    public static Func<byte[], RgbImage?>? Decode { get; set; }

    /// <summary>When set, every screenshot taken while looking through the Homebrew Menu is saved here (troubleshooting / SelfTest).</summary>
    public static string? ScreenshotDir { get; set; }

    /// <summary>The FTP apps ShinyForge can start, in order of preference, with the port each one listens on.</summary>
    public static readonly (string Name, string Icon, int Port)[] Apps =
    {
        ("ftpd", "ftpd.jpg", 5000),             // always serves FTP
        ("Checkpoint", "checkpoint.jpg", 50000), // serves FTP when it's turned on in Checkpoint's settings
    };

    // Homebrew Menu's default theme (nx-hbmenu common/theme.c): the selected entry's 256×256 icon and its name.
    private static readonly (int X, int Y, int W, int H) IconBox = (117, 110, 256, 256);
    private static readonly (int X, int Y, int W, int H) NameBox = (490, 150, 670, 60);
    private const double MatchLimit = 14;   // mean colour difference (0-255) for "same icon"
    private const int MaxEntries = 150;

    // ------------------------------------------------------------------ sys-botbase
    private sealed class Bot : IDisposable
    {
        private readonly TcpClient _c = new();
        private readonly NetworkStream _s;
        private readonly StreamReader _r;

        public Bot(string host, int port)
        {
            if (!_c.ConnectAsync(host, port).Wait(3000) || !_c.Connected)
                throw new IOException($"sys-botbase doesn't answer at {host}:{port}.");
            _c.ReceiveTimeout = 15000;
            _s = _c.GetStream();
            Send("configure echoCommands 0");
            // sys-botbase's virtual controller can be left stale (e.g. after a homebrew app ran): every press then fails with
            // "hiddbgSetHdlsState". Same reset as the Python hunter: release everything, detach, attach a fresh one.
            ReleaseAll();
            Send("detachController");
            Thread.Sleep(500);
            Send("setStick LEFT 0 0");   // attaches the new controller (the first input after attaching is lost)
            Thread.Sleep(1000);
            // throw away whatever error lines that produced, so replies line up
            var junk = new byte[4096];
            while (_c.Available > 0) _s.Read(junk, 0, junk.Length);
            _r = new StreamReader(_s, Encoding.ASCII);
        }

        private void ReleaseAll()
        {
            foreach (var b in new[] { "A", "B", "X", "Y", "L", "R", "ZL", "ZR", "PLUS", "MINUS", "HOME", "DUP", "DDOWN", "DLEFT", "DRIGHT" })
                Send("release " + b);
        }

        public void Send(string cmd)
        {
            var b = Encoding.ASCII.GetBytes(cmd + "\r\n");
            _s.Write(b, 0, b.Length);
        }

        /// <summary>Held long enough not to be dropped over WiFi (shorter presses get lost).</summary>
        public void Tap(string button, int waitMs = 350)
        {
            Send("press " + button); Thread.Sleep(160); Send("release " + button); Thread.Sleep(waitMs);
        }

        /// <summary>The running game's title ID, or null when no game is running (sys-botbase answers with error lines + a blank line).</summary>
        public string? TitleId()
        {
            Send("getTitleID");
            for (var i = 0; i < 8; i++)
            {
                var line = _r.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(line)) return null;
                if (Regex.IsMatch(line, "^[0-9A-Fa-f]{16}$")) return line.TrimStart('0').Length == 0 ? null : line.ToUpperInvariant();
            }
            return null;
        }

        public byte[]? Screenshot()
        {
            Send("pixelPeek");
            for (var i = 0; i < 20; i++)
            {
                var line = _r.ReadLine()?.Trim();
                if (line == null) return null;
                if (line.Length < 1000) continue; // an error line from an earlier command, not the picture
                return line.Length % 2 != 0 || !line.All(Uri.IsHexDigit) ? null : Convert.FromHexString(line);
            }
            return null;
        }

        /// <summary>Leaves no button held (R especially) and detaches the virtual controller cleanly.</summary>
        public void Dispose()
        {
            try { ReleaseAll(); Send("detachController"); Thread.Sleep(200); } catch { }
            _c.Dispose();
        }
    }

    /// <summary>The title ID of the game running on the Switch, or null if none is open.</summary>
    public static string? RunningGame(string host, int port)
    {
        using var bot = new Bot(host, port);
        return bot.TitleId();
    }

    // ------------------------------------------------------------------ image comparison
    /// <summary>A small colour fingerprint of a box: the box split into 16×16 cells, each cell's average R, G, B.</summary>
    internal static double[] Fingerprint(RgbImage img, (int X, int Y, int W, int H) box, int cells = 16)
    {
        var f = new double[cells * cells * 3];
        for (var cy = 0; cy < cells; cy++)
        for (var cx = 0; cx < cells; cx++)
        {
            long r = 0, g = 0, b = 0, n = 0;
            int x0 = box.X + cx * box.W / cells, x1 = box.X + (cx + 1) * box.W / cells;
            int y0 = box.Y + cy * box.H / cells, y1 = box.Y + (cy + 1) * box.H / cells;
            for (var y = Math.Max(0, y0); y < Math.Min(img.Height, y1); y++)
            for (var x = Math.Max(0, x0); x < Math.Min(img.Width, x1); x++)
            {
                var i = (y * img.Width + x) * 3;
                r += img.Pixels[i]; g += img.Pixels[i + 1]; b += img.Pixels[i + 2]; n++;
            }
            var k = (cy * cells + cx) * 3;
            if (n > 0) { f[k] = r / (double)n; f[k + 1] = g / (double)n; f[k + 2] = b / (double)n; }
        }
        return f;
    }

    internal static double Difference(double[] a, double[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Average();

    /// <summary>Is a box one flat grey colour? (the user picker's panel: every pixel the same grey)</summary>
    private static bool FlatGrey(RgbImage img, (int X, int Y, int W, int H) box)
    {
        double sum = 0, sq = 0; long n = 0; var maxTint = 0;
        for (var y = box.Y; y < box.Y + box.H; y += 2)
        for (var x = box.X; x < box.X + box.W; x += 2)
        {
            var i = (y * img.Width + x) * 3;
            int r = img.Pixels[i], g = img.Pixels[i + 1], b = img.Pixels[i + 2];
            maxTint = Math.Max(maxTint, Math.Max(Math.Abs(r - g), Math.Abs(g - b)));
            sum += g; sq += g * g; n++;
        }
        var mean = sum / n;
        return maxTint <= 6 && Math.Sqrt(sq / n - mean * mean) < 4;
    }

    /// <summary>The Switch's "Who is using the software?" screen: its panel is flat grey left of the user icons and under the title.</summary>
    internal static bool IsUserPicker(RgbImage img) =>
        img.Width >= 1280 && img.Height >= 720 && FlatGrey(img, (30, 420, 370, 200)) && FlatGrey(img, (30, 330, 1220, 15));

    private static List<(string Name, int Port, double[] Print)> References()
    {
        var list = new List<(string, int, double[])>();
        foreach (var (name, icon, port) in Apps)
        {
            var path = Path.Combine(AppPaths.AssetsDir, "Homebrew", icon);
            if (!File.Exists(path) || Decode?.Invoke(File.ReadAllBytes(path)) is not { } img) continue;
            list.Add((name, port, Fingerprint(img, (0, 0, img.Width, img.Height))));
        }
        return list;
    }

    /// <summary>Which FTP app's icon is the selected Homebrew Menu entry (or null), and how close the best match was.</summary>
    internal static (string? Name, int Port, double Best) Identify(RgbImage screen, List<(string Name, int Port, double[] Print)> refs)
    {
        var icon = Fingerprint(screen, IconBox);
        var best = refs.Select(r => (r.Name, r.Port, D: Difference(icon, r.Print))).OrderBy(x => x.D).FirstOrDefault();
        return best.Name != null && best.D <= MatchLimit ? (best.Name, best.Port, best.D) : (null, 0, best.Name == null ? 999 : best.D);
    }

    // ------------------------------------------------------------------ the whole sequence
    /// <summary>
    /// Opens the Homebrew Menu (game launched while holding R), finds the FTP app by its icon and starts it, then waits for
    /// its FTP server. The game must already be closed. Returns the FTP port.
    /// </summary>
    public static int StartFtpApp(string host, int botPort, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (Decode == null) throw new InvalidOperationException("No image decoder is set up.");
        var refs = References();
        if (refs.Count == 0) throw new InvalidOperationException("The FTP app icons (Assets/Homebrew) are missing.");
        using var bot = new Bot(host, botPort);
        if (bot.TitleId() is { } running)
            throw new InvalidOperationException($"A game is still open on the Switch ({running}). Close it first.");

        progress?.Invoke("Going to the HOME menu...");
        bot.Tap("B", 400);               // the first input only attaches sys-botbase's controller (B is harmless everywhere)
        bot.Tap("HOME", 1500);
        // Up, up = the user icons along the top (from any row); down once = the game row.
        bot.Tap("DUP", 250); bot.Tap("DUP", 250); bot.Tap("DDOWN", 300);
        for (var i = 0; i < 12; i++) bot.Tap("DLEFT", 200); // the first tile = the last game played

        progress?.Invoke("Opening the Homebrew Menu (starting the game while holding R)...");
        bot.Send("press R");
        Thread.Sleep(400);
        bot.Tap("A", 200);
        // With more than one user on the Switch, "Who is using the software?" comes first: pick the FIRST user, R still held.
        // (Detected, never pressed blindly - an extra A in the Homebrew Menu would start its first app.)
        for (var i = 0; i < 5; i++)
        {
            Thread.Sleep(700);
            if (bot.Screenshot() is { } pickShot && Decode(pickShot) is { } pickImg && IsUserPicker(pickImg))
            {
                progress?.Invoke("Choosing the first user (\"Who is using the software?\")...");
                for (var k = 0; k < 8; k++) bot.Tap("DLEFT", 150); // the far left = the first user
                bot.Tap("A", 200);
                break;
            }
        }
        Thread.Sleep(4500);              // keep R held while the game starts → Atmosphère opens the Homebrew Menu
        bot.Send("release R");

        // The "Nintendo / Nintendo Switch" start-up logo covers the menu for a few seconds and buttons are ignored meanwhile:
        // wait until it's gone (no black bar along the top) and the picture has settled.
        progress?.Invoke("Waiting for the Homebrew Menu...");
        double[]? prev = null;
        var ready = DateTime.Now.AddSeconds(25);
        while (DateTime.Now < ready)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(800);
            if (bot.Screenshot() is not { } boot || Decode(boot) is not { } bootImg) continue;
            var top = Fingerprint(bootImg, (0, 0, 1280, 30), 4);
            var logoGone = top.Average() > 12;   // the logo screen's top bar is black
            var now = Fingerprint(bootImg, IconBox).Concat(Fingerprint(bootImg, NameBox)).ToArray();
            if (logoGone && prev != null && Difference(now, prev) < 1.5) break;
            prev = now;
        }
        Thread.Sleep(500);

        // Step through the list and remember where each FTP app is.
        var found = new Dictionary<string, (int Index, int Port)>();
        double[]? lastName = null, lastIcon = null;
        var index = 0;
        var unchanged = 0;
        for (; index < MaxEntries; index++)
        {
            ct.ThrowIfCancellationRequested();
            var shot = bot.Screenshot();
            if (shot != null && ScreenshotDir != null) { Directory.CreateDirectory(ScreenshotDir); File.WriteAllBytes(Path.Combine(ScreenshotDir, $"entry{index:000}.jpg"), shot); }
            var img = shot == null ? null : Decode(shot);
            if (img == null) throw new IOException("Couldn't take a screenshot of the Switch.");
            var (name, port, _) = Identify(img, refs);
            if (name != null && !found.ContainsKey(name)) found[name] = (index, port);
            if (found.ContainsKey(Apps[0].Name)) break; // the preferred app: no need to look further
            var nameF = Fingerprint(img, NameBox); var iconF = Fingerprint(img, IconBox);
            if (lastName != null && Difference(nameF, lastName) < 1.5 && Difference(iconF, lastIcon!) < 1.5)
            {
                // nothing changed after D-Pad right: the end of the list (or this isn't the Homebrew Menu)
                if (++unchanged >= 2) { index -= unchanged; break; } // the cursor stayed on the last entry
            }
            else unchanged = 0;
            lastName = nameF; lastIcon = iconF;
            progress?.Invoke($"Looking for the FTP app in the Homebrew Menu (entry {index + 1})...");
            bot.Tap("DRIGHT", 450);
        }
        if (found.Count == 0)
        {
            bot.Tap("HOME", 1000);
            throw new IOException("Couldn't find ftpd or Checkpoint in the Homebrew Menu (or the Homebrew Menu didn't open - " +
                                  "Atmosphère opens it when a game is started while R is held).");
        }
        var pick = Apps.First(a => found.ContainsKey(a.Name));
        var (target, ftpPort) = found[pick.Name];

        // Go back to it and make sure it's selected before pressing A.
        for (var i = index; i > target; i--) bot.Tap("DLEFT", 300);
        Thread.Sleep(300);
        var check = bot.Screenshot() is { } s2 ? Decode(s2) : null;
        if (check == null || Identify(check, refs).Name != pick.Name)
            throw new IOException($"Lost the {pick.Name} entry in the Homebrew Menu - nothing was started.");
        progress?.Invoke($"Starting {pick.Name}...");
        bot.Tap("A", 500);

        // Wait for its FTP server.
        var until = DateTime.Now.AddSeconds(30);
        while (DateTime.Now < until)
        {
            ct.ThrowIfCancellationRequested();
            if (HealthMonitor.CanReach(host, ftpPort, 1500)) { AppPaths.Log($"Switch FTP app started: {pick.Name} on port {ftpPort}"); return ftpPort; }
            Thread.Sleep(1000);
        }
        throw new IOException(pick.Name == "Checkpoint"
            ? "Checkpoint started, but its FTP server is off. Turn it on in Checkpoint's settings and try again."
            : $"{pick.Name} started, but its FTP server doesn't answer on port {ftpPort}.");
    }
}
