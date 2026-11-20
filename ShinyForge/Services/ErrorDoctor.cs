using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>What ShinyForge thinks went wrong, what the user can do about it, and (when it can) a fix it can try itself.</summary>
public sealed record Diagnosis(string Cause, IReadOnlyList<string> Steps, string? FixLabel = null, Func<Task<string>>? Fix = null);

/// <summary>
/// Self-diagnosis for the problems the system check finds:
/// <list type="bullet">
/// <item><see cref="AffectsGameplay"/> splits problems that stop or endanger a hunt / a save (shown to the user) from minor ones
/// (overlay, clips, backups, recordings...) that are only written to <see cref="IssuesLog"/> and listed under "minor issues".</item>
/// <item><see cref="Diagnose"/> explains a problem, gives step-by-step solutions and, where possible, a fix ShinyForge tries itself.</item>
/// <item><see cref="ReportUrl"/> builds a GitHub "new issue" link with the problem, the diagnosis and a log excerpt,
/// with personal details (user name, PC name, folders, IP and e-mail addresses) removed. Nothing is sent automatically:
/// the browser opens the form and the user decides whether to submit it.</item>
/// </list>
/// </summary>
public static class ErrorDoctor
{
    public const string ReportTitlePrefix = "[Error report]";
    public static string IssuesLog => Path.Combine(AppPaths.DataDir, "issues.log");

    // ------------------------------------------------------------------ gameplay or not

    /// <summary>
    /// True when the problem stops or endangers hunting / playing / the user's saves. Everything else (recordings, clips,
    /// stream overlay, collection tracker, backups, background hiccups) is logged quietly instead of interrupting the user.
    /// </summary>
    public static bool AffectsGameplay(HealthItem i)
    {
        var f = i.Feature;
        if (f.StartsWith("Python hunter") || f.StartsWith("3DS hunt") || f == "Keep awake"
            || f == "Program files" || f == "Settings / data folder") return true;
        if (f == "Disk space" || f == "Program") return i.State == HealthState.Error;
        if (f.StartsWith("Recording") || f.StartsWith("Shiny clips") || f.StartsWith("Collection tracker")
            || f == "Overlay / remote" || f == "Backups") return false;
        return i.State == HealthState.Error;   // anything new: errors are shown, warnings are logged
    }

    /// <summary>Writes a minor (non-gameplay) problem to issues.log so it can still be reported later.</summary>
    public static void LogQuietly(HealthItem i)
    {
        try
        {
            var line = $"{i.When:yyyy-MM-dd HH:mm:ss}  {i.State}  {i.Feature}: {i.Message}{Environment.NewLine}";
            if (i.Details != null) line += "    " + i.Details.Replace("\n", "\n    ").TrimEnd() + Environment.NewLine;
            File.AppendAllText(IssuesLog, line);
            Trim(IssuesLog, 2_000_000, 500_000);
        }
        catch { /* logging must never crash the app */ }
    }

    // ------------------------------------------------------------------ diagnosis

    public static Diagnosis Diagnose(HealthItem i)
    {
        var f = i.Feature;
        var m = i.Message;
        var s = AppSettings.Current;

        if (f == "Disk space")
            return new("The drive ShinyForge keeps its data, saves and recordings on is almost full.",
                new[]
                {
                    "Empty the Recycle Bin / Trash.",
                    "Move or delete old recordings and shiny clips (they are the biggest files).",
                    "If you record, choose a folder on a bigger drive in the game page's recording settings.",
                },
                "Free up space used by ShinyForge's logs", () => Task.FromResult(FreeLogSpace()));

        if (f == "Program files")
            return new("Some of ShinyForge's own files are missing - usually an antivirus quarantined them or the zip was only partly extracted.",
                new[]
                {
                    "Download the latest version from the Releases page and install it (or extract the whole portable zip again).",
                    "If your antivirus removed files, add the ShinyForge folder to its exceptions first.",
                },
                "Open the download page", () => { DependencyService.OpenUrl(AppInfo.ReleasesUrl); return Task.FromResult("Opened the Releases page in your browser."); });

        if (f == "Settings / data folder")
            return new("ShinyForge can't write its settings and data folder, so settings, saves and hunt logs can't be stored.",
                new[]
                {
                    "Make sure the folder isn't read-only and the drive isn't full or unplugged.",
                    OsInfo.IsWindows ? "Windows Security → Ransomware protection → Controlled folder access: allow ShinyForge." : "Check the folder's permissions (it must belong to you).",
                    "Portable copy: don't run it from inside Program Files or a zip file - extract it to a normal folder.",
                },
                "Repair the data folder", () => Task.FromResult(RepairDataFolder()));

        if (f == "Keep awake")
            return new("The computer went to sleep during a hunt or recording, which pauses everything.",
                new[]
                {
                    OsInfo.IsWindows ? "Settings → System → Power: set \"When plugged in, put my device to sleep after\" to Never while hunting." : "Turn off automatic sleep while hunting.",
                    "Laptops: keep the lid open (or set \"closing the lid\" to Do nothing) and keep it plugged in.",
                });

        if (f.StartsWith("Python hunter") || f.StartsWith("Collection tracker") && m.Contains("Switch"))
        {
            if (m.Contains("Can't reach the Switch") || m.Contains("can't reach the Switch"))
                return new("ShinyForge can't connect to sys-botbase on the Switch.",
                    new[]
                    {
                        "Make sure the Switch is on, not asleep, and on the same WiFi network as this computer.",
                        "The Switch must run Atmosphère with sys-botbase installed (it listens on port 6000).",
                        "Check the Switch's IP address: System Settings → Internet → Connection Status, and enter it in Settings if it changed.",
                    },
                    "Test the connection to the Switch", () => Task.Run(() => TestPort("Switch", s.SwitchIP, 6000)));
            if (m.Contains("has stopped"))
                return new("The Python hunter closed (it crashed or found a shiny).",
                    new[]
                    {
                        "If Hunter output on the game page says it found a shiny, it stopped on purpose - catch it!",
                        "Otherwise, restart the hunt. If it keeps stopping, check that the game is in the right spot for the hunt.",
                    },
                    "Restart the hunt", () => Task.Run(HuntService.RestartFromDoctor));
            if (m.Contains("The same") || m.Contains("came back"))
                return new("The game isn't resetting between encounters, so the same Pokémon appears again and again.",
                    new[]
                    {
                        "Watch the Switch for a moment: the game should go back to the title screen after every encounter.",
                        "Make sure no controller is holding a button and that sys-botbase is up to date.",
                        "Restart the hunt from the right spot in the game.",
                    },
                    "Restart the hunt", () => Task.Run(HuntService.RestartFromDoctor));
            if (m.Contains("No new encounter"))
                return new("The hunt is running but no new Pokémon has been logged for a while - the game may be stuck on a menu.",
                    new[] { "Look at the Switch screen and Hunter output on the game page.", "Restart the hunt if the game is stuck." },
                    "Restart the hunt", () => Task.Run(HuntService.RestartFromDoctor));
            if (f.StartsWith("Collection tracker"))
                return new("Live collection monitoring can't read the game from the Switch.",
                    new[] { "Make sure the Switch is on and sys-botbase runs, then check its IP address in Settings." },
                    "Test the connection to the Switch", () => Task.Run(() => TestPort("Switch", s.SwitchIP, 6000)));
            return new("The Python hunter reported something unusual.",
                new[] { "Look at Hunter output on the game page for details; it usually fixes these itself and keeps going." });
        }

        if (f.StartsWith("3DS hunt"))
            return new("ShinyForge lost contact with the 3DS (Luma3DS Rosalina debugger / input redirection).",
                new[]
                {
                    "Make sure the 3DS is on, the game is running and WiFi is on (same network as this computer).",
                    "Open the Rosalina menu (L + Down + Select) → Debugger options → Enable debugger, and Input redirection.",
                    "Check the 3DS's IP address in the Rosalina menu and enter it in Settings if it changed.",
                },
                "Test the connection to the 3DS", () => Task.Run(() => TestPort("3DS", s.ThreeDSIP, 4000)));

        if (f.StartsWith("Recording"))
            return new("The recording stopped or couldn't start. ShinyForge restarts it automatically when it can.",
                new[]
                {
                    "Make sure ffmpeg is installed (Tools) and the capture window / capture card is open and visible.",
                    "Check that the recording folder's drive has free space.",
                });

        if (f.StartsWith("Collection tracker"))
            return new("The collection tracker couldn't read the game folder or update the spreadsheet.",
                new[]
                {
                    "If the game folder is on an external drive or SD card, plug it in.",
                    "Close the tracker spreadsheet in Excel if it's open, then sync again.",
                });

        if (f.StartsWith("Shiny clips"))
            return new("The shiny-clip buffer couldn't save or keep recording.",
                new[] { "Make sure ffmpeg is installed (Tools) and the clips folder's drive has free space." });

        if (f == "Overlay / remote")
            return new("The stream overlay / phone remote couldn't start - another program is probably using its port.",
                new[] { "Close the other program, or let ShinyForge pick a free port (then use the new link in Settings)." },
                "Switch to a free port", () => Task.Run(UseFreeWebPort));

        if (f == "Backups")
            return new("A save backup couldn't be written.",
                new[] { "Check that the drive isn't full and the save file isn't open in another program (PKHeX, Checkpoint sync...)." });

        // "Program" and anything unknown: a bug in ShinyForge itself.
        return new("This looks like a bug in ShinyForge itself.",
            new[]
            {
                "Restart ShinyForge and try again.",
                "Update to the latest version - it may already be fixed.",
                "If it happens again, report it to GitHub so it can be fixed.",
            },
            "Check for an update", CheckForUpdate);
    }

    // ------------------------------------------------------------------ fixes

    private static string FreeLogSpace()
    {
        long before = 0, after = 0;
        foreach (var file in new[] { AppPaths.LogFile, IssuesLog })
        {
            if (!File.Exists(file)) continue;
            before += new FileInfo(file).Length;
            Trim(file, 1_000_000, 200_000);
            after += new FileInfo(file).Length;
        }
        var freed = (before - after) / 1e6;
        string free = "";
        try { free = $" {new DriveInfo(Path.GetPathRoot(AppPaths.DataDir)!).AvailableFreeSpace / 1e9:0.0} GB is free now."; } catch { }
        return (freed > 0.05 ? $"Trimmed ShinyForge's logs ({freed:0.0} MB)." : "ShinyForge's logs are already small - space has to be freed elsewhere (see the steps).") + free;
    }

    private static string RepairDataFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var probe = Path.Combine(AppPaths.DataDir, ".health");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return "The data folder can be written again.";
        }
        catch (Exception e) { return "Still can't write the data folder: " + e.Message + " - follow the steps above."; }
    }

    private static string TestPort(string what, string ip, int port)
    {
        if (string.IsNullOrWhiteSpace(ip)) return $"No {what} IP address is set yet - enter it in Settings.";
        return HealthMonitor.CanReach(ip, port, 3000)
            ? $"The {what} answers now - the problem should clear at the next check."
            : $"The {what} still doesn't answer at {ip}:{port}. Follow the steps above.";
    }

    private static string UseFreeWebPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        AppSettings.Current.WebPort = port;
        AppSettings.Current.Save();
        WebServer.Apply();
        return WebServer.Running ? $"The overlay / remote now uses port {port} (the links in Settings changed)." : "Still couldn't start the web server - see the log.";
    }

    private static async Task<string> CheckForUpdate()
    {
        try
        {
            var r = await AppUpdateService.CheckAsync();
            return r != null && r.Version > AppInfo.Comparable
                ? $"Version {r.Tag} is available - install it from the Updates button, it may already fix this."
                : "You already have the latest version.";
        }
        catch (Exception e) { return "Couldn't check for updates: " + e.Message; }
    }

    /// <summary>Keeps a log file below <paramref name="max"/> bytes by keeping only its last <paramref name="keep"/> bytes.</summary>
    private static void Trim(string file, long max, int keep)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length <= max) return;
        var bytes = File.ReadAllBytes(file);
        var tail = Encoding.UTF8.GetString(bytes, bytes.Length - keep, keep);
        var nl = tail.IndexOf('\n');
        File.WriteAllText(file, nl >= 0 ? tail[(nl + 1)..] : tail);
    }

    // ------------------------------------------------------------------ GitHub report

    /// <summary>Removes the user name, PC name, home folder, IP addresses, e-mail addresses and long serial-like numbers.</summary>
    public static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var t = text;
        var home = OsInfo.Home;
        if (!string.IsNullOrEmpty(home))
        {
            t = t.Replace(home, "~", StringComparison.OrdinalIgnoreCase);
            t = t.Replace(home.Replace('\\', '/'), "~", StringComparison.OrdinalIgnoreCase);
        }
        t = Regex.Replace(t, @"[\w.+-]+@[\w-]+(\.[\w-]+)+", "<email>");
        t = Regex.Replace(t, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "<ip>");
        t = Regex.Replace(t, @"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", "<mac>");
        foreach (var name in new[] { Environment.UserName, Environment.MachineName })
            if (name.Length >= 3) t = Regex.Replace(t, Regex.Escape(name), name == Environment.UserName ? "<user>" : "<pc>", RegexOptions.IgnoreCase);
        return t;
    }

    /// <summary>The issue text: what happened, ShinyForge's diagnosis, what the fix did, and a sanitized log excerpt.</summary>
    public static string ReportBody(HealthItem i, Diagnosis d, string? fixResult, string? userNote = null, int logLines = 25)
    {
        var b = new StringBuilder();
        b.AppendLine("**What happened**");
        b.AppendLine($"- Feature: {i.Feature}");
        b.AppendLine($"- Severity: {i.State}{(AffectsGameplay(i) ? " (affects hunting/playing)" : " (minor - doesn't affect gameplay)")}");
        b.AppendLine($"- Message: {i.Message}");
        b.AppendLine($"- When: {i.When:yyyy-MM-dd HH:mm}");
        if (!string.IsNullOrWhiteSpace(userNote)) { b.AppendLine(); b.AppendLine("**What I was doing**"); b.AppendLine(userNote.Trim()); }
        b.AppendLine();
        b.AppendLine("**ShinyForge's diagnosis**");
        b.AppendLine(d.Cause);
        if (d.FixLabel != null) b.AppendLine($"- Automatic fix \"{d.FixLabel}\": {fixResult ?? "not tried"}");
        b.AppendLine();
        b.AppendLine("**System**");
        b.AppendLine($"- ShinyForge {AppInfo.Display}{(AppPaths.IsPortable ? " portable" : "")}");
        b.AppendLine($"- {OsInfo.Name} ({RuntimeInformation.OSDescription.Trim()}, {RuntimeInformation.OSArchitecture})");
        if (!string.IsNullOrWhiteSpace(i.Details))
        {
            b.AppendLine(); b.AppendLine("**Error details**"); b.AppendLine("```"); b.AppendLine(Shorten(i.Details, 2500)); b.AppendLine("```");
        }
        var log = LastLines(AppPaths.LogFile, logLines);
        if (log.Length > 0)
        {
            b.AppendLine(); b.AppendLine("**Log (last lines)**"); b.AppendLine("```"); b.AppendLine(string.Join("\n", log)); b.AppendLine("```");
        }
        b.AppendLine();
        b.AppendLine("_Sent from ShinyForge's system check. Personal details (user name, PC name, folders, IP and e-mail addresses) were removed._");
        return Sanitize(b.ToString());
    }

    /// <summary>The confirmation shown before the browser opens: what happens, and a preview of the (sanitized) report.</summary>
    public static string ReportPrompt(HealthItem i, Diagnosis d, string? fixResult)
    {
        var preview = ReportBody(i, d, fixResult, logLines: 6);
        if (preview.Length > 1400) preview = preview[..1400] + "\n…";
        return "Your browser will open GitHub's \"New issue\" page for ShinyForge, filled in with this report. Your user name, PC name, " +
               "folders and IP / e-mail addresses have been removed. Nothing is sent until you press \"Create\" on GitHub " +
               "(a free GitHub account is needed), and you can add what you were doing first.\n\n" + preview;
    }

    public static string ReportTitle(HealthItem i) => Sanitize($"{ReportTitlePrefix} {i.Feature}: {Shorten(i.Message, 90)}");

    /// <summary>
    /// A link to GitHub's "new issue" form, already filled in. Long logs are cut so the link stays under GitHub's length limit.
    /// </summary>
    public static string ReportUrl(HealthItem i, Diagnosis d, string? fixResult, string? userNote = null)
    {
        string Url(int lines) => $"{AppInfo.RepoUrl}/issues/new?title={Uri.EscapeDataString(ReportTitle(i))}&body={Uri.EscapeDataString(ReportBody(i, d, fixResult, userNote, lines))}";
        foreach (var lines in new[] { 25, 12, 5, 0 })
        {
            var url = Url(lines);
            if (url.Length <= 7500) return url;
        }
        var item = i with { Details = Shorten(i.Details ?? "", 600) };
        return $"{AppInfo.RepoUrl}/issues/new?title={Uri.EscapeDataString(ReportTitle(item))}&body={Uri.EscapeDataString(Shorten(ReportBody(item, d, fixResult, Shorten(userNote ?? "", 400), 0), 4000))}";
    }

    /// <summary>Minor problems written to issues.log (newest first), to look at or report.</summary>
    public static IReadOnlyList<string> QuietLogLines(int count = 200) => LastLines(IssuesLog, count).Reverse().ToList();

    private static string[] LastLines(string path, int count)
    {
        if (count <= 0) return Array.Empty<string>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, fs.Length - 64_000);
            fs.Seek(start, SeekOrigin.Begin);
            var lines = new StreamReader(fs).ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
            if (start > 0 && lines.Length > 0) lines = lines[1..];   // the first line may be cut in half
            return lines.TakeLast(count).Select(l => Shorten(l, 300)).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
