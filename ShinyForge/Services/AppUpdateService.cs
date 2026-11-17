using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>A published ShinyForge release (from the GitHub releases of <see cref="AppInfo.Repo"/>).</summary>
public sealed record AppRelease(string Tag, Version Version, string Name, string Notes, string PageUrl, DateTime Published,
    string? ZipUrl, long ZipSize)
{
    /// <summary>"1.1.0"</summary>
    public string VersionText => AppInfo.Text(Version);
    public bool IsNewer => Version > AppInfo.Comparable;
}

/// <summary>
/// Checks GitHub for a newer ShinyForge and installs it. Both the installed and the portable copy update the same way:
/// ShinyForge-Portable.zip (the program files, ~150 MB - not the multi-GB installer with the spreadsheets) is downloaded
/// and its files replace the old ones after ShinyForge closes. The Data, Spreadsheets, Saves and Backup Tools folders,
/// portable.flag and the Python bot's config.yaml are kept.
/// </summary>
public static class AppUpdateService
{
    private static readonly HttpClient Http = WebClients.Make(TimeSpan.FromSeconds(20));
    public static AppRelease? Latest { get; private set; }

    /// <summary>
    /// The newest published release (null if none yet). The tag comes first from github.com (no API quota): when this copy is
    /// current that's all, and the API (60 questions an hour) is only asked for the notes and download of a NEWER release.
    /// If the API is out of questions, the release is still offered with its standard download link.
    /// </summary>
    public static async Task<AppRelease?> CheckAsync()
    {
        var tag = await WebClients.LatestGitHubTag(AppInfo.Repo);
        if (AppInfo.ParseTag(tag) is not { } v) return Latest = null;
        var page = $"https://github.com/{AppInfo.Repo}/releases/tag/{tag}";
        if (v <= AppInfo.Comparable) return Latest = new AppRelease(tag, v, tag, "", page, default, null, 0);
        try
        {
            using var resp = await Http.GetAsync($"https://api.github.com/repos/{AppInfo.Repo}/releases/latest");
            resp.EnsureSuccessStatusCode();
            if (await resp.Content.ReadFromJsonAsync<JsonObject>() is { } j && Parse(j) is { } full && full.Tag == tag) return Latest = full;
        }
        catch (Exception e) { AppPaths.Log($"ShinyForge {tag} release details: {e.Message} - offering it with the standard download"); }
        return Latest = new AppRelease(tag, v, "ShinyForge " + tag, "The release notes couldn't be loaded right now - they're on the release page (View on GitHub).",
            page, default, $"https://github.com/{AppInfo.Repo}/releases/download/{tag}/ShinyForge-Portable.zip", 0);
    }

    public static AppRelease? Parse(JsonObject j)
    {
        var tag = j["tag_name"]?.ToString() ?? "";
        var v = AppInfo.ParseTag(tag);
        if (v == null) return null;
        string? zip = null;
        long zipSize = 0;
        foreach (var a in j["assets"] as JsonArray ?? new JsonArray())
        {
            var name = a?["name"]?.ToString() ?? "";
            var url = a?["browser_download_url"]?.ToString();
            var size = a?["size"]?.GetValue<long>() ?? 0;
            if (name.Contains("Portable", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { zip = url; zipSize = size; }
        }
        DateTime.TryParse(j["published_at"]?.ToString(), out var published);
        return new AppRelease(tag, v, j["name"]?.ToString() is { Length: > 0 } n ? n : tag, j["body"]?.ToString() ?? "",
            j["html_url"]?.ToString() ?? AppInfo.ReleasesUrl, published, zip, zipSize);
    }

    /// <summary>Downloads the program files and starts the update. Returns true when ShinyForge should now close so it can finish.</summary>
    public static async Task<bool> InstallAsync(AppRelease r, Action<string> progress, CancellationToken token = default)
    {
        if (!OsInfo.IsWindows)
        {
            // macOS / Linux: the release page has the .app / AppImage / packages - the user downloads the one they use.
            OsInfo.Open(r.PageUrl);
            return false;
        }
        var dir = Path.Combine(Path.GetTempPath(), "ShinyForgeUpdate", r.Tag);
        Directory.CreateDirectory(dir);
        if (r.ZipUrl == null) throw new IOException("This release has no program download (ShinyForge-Portable.zip) - get it from the release page.");
        var zipFile = Path.Combine(dir, "ShinyForge-Portable.zip");
        await DownloadAsync(r.ZipUrl, zipFile, r.ZipSize, progress, token);
        progress("Unpacking...");
        var script = await Task.Run(() => PreparePortableUpdate(zipFile, dir, AppPaths.AppDir, Environment.ProcessId), token);
        progress("Restarting ShinyForge to finish the update...");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized });
        return true;
    }

    /// <summary>
    /// Unpacks a portable zip into <paramref name="workDir"/> and writes apply_update.cmd, which waits for process
    /// <paramref name="waitForPid"/> to exit, copies the new program files into <paramref name="appDir"/> (keeping the
    /// Data, Backup Tools, Spreadsheets and Saves folders, portable.flag and the bot's config.yaml) and starts ShinyForge again. Returns the script path.
    /// </summary>
    public static string PreparePortableUpdate(string zipFile, string workDir, string appDir, int waitForPid, bool restart = true)
    {
        var unpack = Path.Combine(workDir, "unpacked");
        if (Directory.Exists(unpack)) Directory.Delete(unpack, true);
        ZipFile.ExtractToDirectory(zipFile, unpack);
        // The zip contains a "ShinyForge Portable" folder.
        var newApp = Directory.EnumerateFiles(unpack, "ShinyForge.exe", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault()
                     ?? throw new IOException("The downloaded zip doesn't contain ShinyForge.exe.");
        appDir = appDir.TrimEnd('\\');
        var script = Path.Combine(workDir, "apply_update.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            "title ShinyForge update\r\n" +
            "echo Updating ShinyForge - please wait...\r\n" +
            $":wait\r\ntasklist /FI \"PID eq {waitForPid}\" /NH | find \" {waitForPid} \" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
            $"robocopy \"{newApp}\" \"{appDir}\" /E /XD Data \"Backup Tools\" Spreadsheets Saves /XF portable.flag config.yaml /R:5 /W:2 /NFL /NDL /NJH /NJS /NP\r\n" +
            "if %ERRORLEVEL% GEQ 8 (echo The update could not be copied. & pause & exit /b 1)\r\n" +
            (restart ? $"start \"\" \"{Path.Combine(appDir, "ShinyForge.exe")}\"\r\n" : "") +
            "exit /b 0\r\n");
        return script;
    }

    private static async Task DownloadAsync(string url, string file, long expected, Action<string> progress, CancellationToken token)
    {
        using var http = WebClients.Make(TimeSpan.FromHours(2));
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? expected;
        await using var src = await resp.Content.ReadAsStreamAsync(token);
        await using (var dst = File.Create(file))
        {
            var buf = new byte[1 << 20];
            long done = 0;
            var last = Environment.TickCount64;
            int n;
            while ((n = await src.ReadAsync(buf, token)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), token);
                done += n;
                if (Environment.TickCount64 - last > 250)
                {
                    last = Environment.TickCount64;
                    progress(total > 0 ? $"Downloading... {done / 1048576:N0} of {total / 1048576:N0} MB ({done * 100 / total}%)" : $"Downloading... {done / 1048576:N0} MB");
                }
            }
        }
        if (total > 0 && new FileInfo(file).Length != total) throw new IOException("The download was incomplete - please try again.");
    }
}
