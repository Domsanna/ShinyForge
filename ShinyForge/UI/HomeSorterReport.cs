using System.Diagnostics;
using System.Text.RegularExpressions;
using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>
/// When the HOME Box Sorter fails: its run log is kept, and the user is asked whether to send a report to GitHub as an
/// issue titled "[Error report] HOME Box Sorter: ..." - the title ShinyForge's scheduled issue check looks for, so the
/// problem is picked up for the next update. Filed directly with the GitHub CLI when it's installed and signed in, else
/// GitHub's "new issue" page opens filled in. Personal details are removed (ErrorDoctor.Sanitize, plus OT names and IDs).
/// </summary>
public static class HomeSorterReport
{
    public static void Offer(IWin32Window owner, string error, string? logFile)
    {
        var tail = "";
        try
        {
            if (logFile != null && File.Exists(logFile))
            {
                using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                var lines = sr.ReadToEnd().Split('\n');
                tail = string.Join("\n", lines.TakeLast(60));
            }
        }
        catch { }
        // the user's Pokémon: trainer names and IDs stay private
        tail = Regex.Replace(tail, @"OT [^(\n]*\(\d+\)", "OT <hidden>");
        var item = new HealthItem("HOME Box Sorter", HealthState.Error, error.Split('\n')[0], DateTime.Now,
            $"{error}\n\nRun log (last lines):\n{tail}");
        var r = MessageBox.Show(owner,
            $"The HOME Box Sorter stopped:\n\n{Shorten(error, 400)}\n\nThe log was saved{(logFile != null ? $" as {Path.GetFileName(logFile)}" : "")} in Documents\\ShinyForge\\HOME Sorter.\n\n" +
            "Send this error report to GitHub as an issue, so it gets fixed in the next update?\n" +
            "(Your user name, PC name, folders, IP addresses, OT names and IDs are removed. Screenshots stay on this PC.)",
            "HOME Box Sorter - report the problem?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r != DialogResult.Yes) return;
        var d = ErrorDoctor.Diagnose(item);
        var title = ErrorDoctor.ReportTitle(item);
        var body = ErrorDoctor.ReportBody(item, d, null, logLines: 10);
        if (FileWithGh(title, body, out var url))
        {
            AppPaths.Log("[home sorter] error report filed: " + url);
            MessageBox.Show(owner, $"Thank you - the report was filed on GitHub:\n{url}", "HOME Box Sorter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        DependencyService.OpenUrl(ErrorDoctor.ReportUrl(item, d, null));
        MessageBox.Show(owner, "GitHub's \"New issue\" page opened in your browser with the report filled in - press \"Create\" there to send it. Thank you!",
            "HOME Box Sorter", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Files the issue with the GitHub CLI (gh) when it's installed and signed in. False = use the browser.</summary>
    private static bool FileWithGh(string title, string body, out string url)
    {
        url = "";
        var gh = new[] { @"C:\Program Files\GitHub CLI\gh.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub CLI", "gh.exe") }
            .FirstOrDefault(File.Exists) ?? "gh";
        try
        {
            if (Run(gh, new[] { "auth", "status" }, out _) != 0) return false;
            var tmp = Path.Combine(Path.GetTempPath(), $"shinyforge-report-{Guid.NewGuid():N}.md");
            File.WriteAllText(tmp, body);
            try
            {
                if (Run(gh, new[] { "issue", "create", "--repo", AppInfo.Repo, "--title", title, "--body-file", tmp }, out var output) != 0) return false;
                url = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("https://")) ?? output.Trim();
                return true;
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
        catch { return false; }
    }

    private static int Run(string exe, string[] args, out string output)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return -1; }
        return p.ExitCode;
    }

    private static string Shorten(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
