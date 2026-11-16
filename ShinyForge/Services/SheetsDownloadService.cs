using System.Net.Http.Json;
using System.Security.Cryptography;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>One Pokédex tracker published on GitHub: the asset name, where it goes ("Gen 7\(25) Sun\Sun Pokedex Tracker.xlsm"), SHA-256, size.</summary>
public sealed record SheetEntry(string Asset, string Path, string Sha256, long Size);

/// <summary>
/// The Pokédex tracker spreadsheets are published as separate files in the "spreadsheets-1" release of the ShinyForge
/// GitHub repository (with spreadsheets.json listing them). The installer downloads the ones the user doesn't have yet;
/// inside ShinyForge (portable, macOS, Linux, or a skipped download) a game's tracker can be downloaded on demand.
/// Existing files are never overwritten - they hold the user's progress.
/// </summary>
public static class SheetsDownloadService
{
    public const string Tag = "spreadsheets-1";
    public static string BaseUrl => $"https://github.com/{AppInfo.Repo}/releases/download/{Tag}/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static SheetsDownloadService() => Http.DefaultRequestHeaders.UserAgent.ParseAdd($"ShinyForge/{AppInfo.Comparable}");

    public static async Task<List<SheetEntry>> ManifestAsync() =>
        await Http.GetFromJsonAsync<List<SheetEntry>>(BaseUrl + "spreadsheets.json") ?? new();

    /// <summary>The manifest entry whose folder ("(25) Sun") matches the game's tracker name.</summary>
    public static SheetEntry? EntryFor(Game game, IEnumerable<SheetEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(game.Spreadsheet)) return null;
        var want = Collection.Normalize(game.Spreadsheet);
        return entries.FirstOrDefault(e =>
        {
            var parts = e.Path.Split('\\', '/');
            return parts.Length >= 2 && Collection.Normalize(SpreadsheetService.LabelOf(parts[^2])) == want;
        });
    }

    /// <summary>Downloads one tracker into the bundled spreadsheet folder (verified against its SHA-256). Returns its path.</summary>
    public static async Task<string> DownloadAsync(SheetEntry e, Action<string> progress, string? root = null)
    {
        var target = System.IO.Path.Combine(root ?? SpreadsheetService.BundledRoot, e.Path.Replace('\\', System.IO.Path.DirectorySeparatorChar));
        if (File.Exists(target)) return target; // never overwrite progress
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        var tmp = target + ".download";
        using (var http = WebClients.Make(TimeSpan.FromHours(1), githubJson: false))
        {
            using var resp = await http.GetAsync(BaseUrl + e.Asset, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(tmp);
            var buf = new byte[1 << 20];
            long done = 0; int n; var last = 0L;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if (Environment.TickCount64 - last > 300)
                {
                    last = Environment.TickCount64;
                    progress(e.Size > 0 ? $"Downloading the tracker... {done * 100 / e.Size}% of {e.Size / 1048576} MB" : $"Downloading the tracker... {done / 1048576} MB");
                }
            }
        }
        string sha;
        await using (var s = File.OpenRead(tmp)) sha = Convert.ToHexString(await SHA256.HashDataAsync(s)).ToLowerInvariant();
        if (!sha.Equals(e.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tmp);
            throw new IOException("The downloaded tracker doesn't match its published checksum - please try again.");
        }
        File.Move(tmp, target);
        AppPaths.Log("Tracker downloaded: " + target);
        return target;
    }
}
