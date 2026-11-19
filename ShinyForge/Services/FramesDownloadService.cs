using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>One downloadable file in the console-frames release: the asset name, SHA-256, size.</summary>
public sealed record FramesEntry(string Asset, string Sha256, long Size);

/// <summary>
/// The console frame pictures (Game Boy … Switch 2 PNGs + screen_positions.json) are published as ConsoleFrames.zip in
/// the "console-frames-1" release of the ShinyForge GitHub repository (console-frames.json gives its checksum).
/// When pictures are missing, ShinyForge downloads the zip and unpacks only the missing files into
/// <see cref="ConsoleFrames.Folder"/> - pictures the user already has are never replaced.
/// </summary>
public static class FramesDownloadService
{
    public const string Tag = "console-frames-1";
    public static string BaseUrl => $"https://github.com/{AppInfo.Repo}/releases/download/{Tag}/";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Makes sure all frame pictures exist, downloading the missing ones. Returns a line for the user
    /// (null = nothing was needed). Throws when the download fails.
    /// </summary>
    public static async Task<string?> EnsureAsync(Action<string> progress, string? folder = null)
    {
        await Gate.WaitAsync();
        try
        {
            folder ??= ConsoleFrames.Folder;
            var missing = ConsoleFrames.All.Select(f => f.File).Where(f => !File.Exists(Path.Combine(folder, f))).ToList();
            if (missing.Count == 0) return null;

            progress("Downloading the console frame pictures from ShinyForge's GitHub page...");
            using var http = WebClients.Make(TimeSpan.FromMinutes(10), githubJson: false);
            var entry = (await http.GetFromJsonAsync<List<FramesEntry>>(BaseUrl + "console-frames.json"))?.FirstOrDefault()
                        ?? throw new IOException("The console frame list on GitHub is empty.");

            var tmp = Path.Combine(Path.GetTempPath(), $"ShinyForge-frames-{Guid.NewGuid():N}.zip");
            try
            {
                using (var resp = await http.GetAsync(BaseUrl + entry.Asset, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var src = await resp.Content.ReadAsStreamAsync();
                    await using var dst = File.Create(tmp);
                    var buf = new byte[1 << 20];
                    long done = 0; int n; long last = 0;
                    while ((n = await src.ReadAsync(buf)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n));
                        done += n;
                        if (Environment.TickCount64 - last > 300)
                        {
                            last = Environment.TickCount64;
                            progress(entry.Size > 0 ? $"Downloading the console frame pictures... {done * 100 / entry.Size}%" : $"Downloading the console frame pictures... {done / 1048576} MB");
                        }
                    }
                }
                string sha;
                await using (var s = File.OpenRead(tmp)) sha = Convert.ToHexString(await SHA256.HashDataAsync(s)).ToLowerInvariant();
                if (!sha.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The downloaded console pictures don't match their published checksum - please try again.");

                progress("Unpacking the console frame pictures...");
                Directory.CreateDirectory(folder);
                int added = 0;
                using (var zip = ZipFile.OpenRead(tmp))
                    foreach (var e in zip.Entries.Where(e => e.Name.Length > 0))
                    {
                        // flat files only (no folders, no "..") - and never replace a picture the user already has
                        var target = Path.Combine(folder, e.Name);
                        if (File.Exists(target)) continue;
                        e.ExtractToFile(target);
                        added++;
                    }
                AppPaths.Log($"Console frames downloaded: {added} file(s) into {folder}");
                return $"Console frame pictures downloaded into {folder}.";
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
        finally { Gate.Release(); }
    }
}
