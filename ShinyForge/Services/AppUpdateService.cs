using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>A published ShinyForge release (from the GitHub releases of <see cref="AppInfo.Repo"/>).</summary>
public sealed record AppRelease(string Tag, Version Version, string Name, string Notes, string PageUrl, DateTime Published,
    string? SetupUrl, long SetupSize, string? ZipUrl, long ZipSize)
{
    public int Build => Version.Build;
    public bool IsNewer => Version > AppInfo.Comparable;
}

/// <summary>
/// Checks GitHub for a newer ShinyForge and installs it:
///  - installed copy: downloads ShinyForge-Setup.exe and runs it (it closes ShinyForge, updates, and starts it again);
///  - portable copy: downloads ShinyForge-Portable.zip and replaces the program files after ShinyForge closes
///    (the Data, Spreadsheets and Backup Tools folders are kept).
/// </summary>
public static class AppUpdateService
{
    private static readonly HttpClient Http = MakeClient(TimeSpan.FromSeconds(20));
    public static AppRelease? Latest { get; private set; }

    private static HttpClient MakeClient(TimeSpan timeout)
    {
        var h = new HttpClient { Timeout = timeout };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"ShinyForge/{AppInfo.Comparable}");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    /// <summary>The newest published release (null if none yet or GitHub can't be reached).</summary>
    public static async Task<AppRelease?> CheckAsync()
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{AppInfo.Repo}/releases/latest");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return Latest = null; // no release published yet
        resp.EnsureSuccessStatusCode();
        var j = await resp.Content.ReadFromJsonAsync<JsonObject>() ?? throw new IOException("GitHub returned nothing.");
        return Latest = Parse(j);
    }

    public static AppRelease? Parse(JsonObject j)
    {
        var tag = j["tag_name"]?.ToString() ?? "";
        var v = AppInfo.ParseTag(tag);
        if (v == null) return null;
        string? setup = null, zip = null;
        long setupSize = 0, zipSize = 0;
        foreach (var a in j["assets"] as JsonArray ?? new JsonArray())
        {
            var name = a?["name"]?.ToString() ?? "";
            var url = a?["browser_download_url"]?.ToString();
            var size = a?["size"]?.GetValue<long>() ?? 0;
            if (name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase)) { setup = url; setupSize = size; }
            else if (name.Contains("Portable", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { zip = url; zipSize = size; }
        }
        DateTime.TryParse(j["published_at"]?.ToString(), out var published);
        return new AppRelease(tag, v, j["name"]?.ToString() is { Length: > 0 } n ? n : tag, j["body"]?.ToString() ?? "",
            j["html_url"]?.ToString() ?? AppInfo.ReleasesUrl, published, setup, setupSize, zip, zipSize);
    }

    /// <summary>Downloads the right file for this copy (installer or portable zip) and starts the update.
    /// Returns true when ShinyForge should now close so the update can finish.</summary>
    public static async Task<bool> InstallAsync(AppRelease r, Action<string> progress, CancellationToken token = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ShinyForgeUpdate", r.Tag);
        Directory.CreateDirectory(dir);
        if (!AppPaths.IsPortable)
        {
            if (r.SetupUrl == null) throw new IOException("This release has no installer to download - get it from the release page.");
            var setup = Path.Combine(dir, "ShinyForge-Setup.exe");
            await DownloadAsync(r.SetupUrl, setup, r.SetupSize, progress, token);
            progress("Starting the installer...");
            // Inno Setup: closes ShinyForge if needed, installs over the old version, restarts it afterwards.
            Process.Start(new ProcessStartInfo(setup, "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /NOCANCEL") { UseShellExecute = true });
            return true;
        }

        if (r.ZipUrl == null) throw new IOException("This release has no portable zip to download - get it from the release page.");
        var zipFile = Path.Combine(dir, "ShinyForge-Portable.zip");
        await DownloadAsync(r.ZipUrl, zipFile, r.ZipSize, progress, token);
        progress("Unpacking...");
        var unpack = Path.Combine(dir, "unpacked");
        if (Directory.Exists(unpack)) Directory.Delete(unpack, true);
        await Task.Run(() => ZipFile.ExtractToDirectory(zipFile, unpack), token);
        // The zip contains a "ShinyForge Portable" folder.
        var newApp = Directory.EnumerateFiles(unpack, "ShinyForge.exe", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault()
                     ?? throw new IOException("The downloaded zip doesn't contain ShinyForge.exe.");
        var appDir = AppPaths.AppDir.TrimEnd('\\');
        var script = Path.Combine(dir, "apply_update.cmd");
        // Waits for ShinyForge to close, copies the new program files over (keeping the user's folders), restarts it.
        File.WriteAllText(script,
            "@echo off\r\n" +
            "title ShinyForge update\r\n" +
            "echo Updating ShinyForge - please wait...\r\n" +
            $":wait\r\ntasklist /FI \"PID eq {Environment.ProcessId}\" | find \"{Environment.ProcessId}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
            $"robocopy \"{newApp}\" \"{appDir}\" /E /XD Data \"Backup Tools\" Spreadsheets /XF portable.flag /NFL /NDL /NJH /NJS /NP\r\n" +
            "if %ERRORLEVEL% GEQ 8 (echo The update could not be copied. & pause & exit /b 1)\r\n" +
            $"start \"\" \"{Path.Combine(appDir, "ShinyForge.exe")}\"\r\n");
        progress("Restarting ShinyForge to finish the update...");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized });
        return true;
    }

    private static async Task DownloadAsync(string url, string file, long expected, Action<string> progress, CancellationToken token)
    {
        using var http = MakeClient(TimeSpan.FromHours(2));
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
