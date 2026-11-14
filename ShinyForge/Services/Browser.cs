using System.Diagnostics;
using Microsoft.Win32;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// Opens pages in the computer's default WEB BROWSER. A local .html file is opened with the program Windows uses for
/// web links (https), not whatever program is set for .html files (that can be an editor).
/// </summary>
public static class Browser
{
    public static void Open(string pathOrUrl)
    {
        var url = File.Exists(pathOrUrl) ? new Uri(Path.GetFullPath(pathOrUrl)).AbsoluteUri : pathOrUrl;
        if (!OsInfo.IsWindows)
        {
            // macOS "open" and Linux "xdg-open" hand web addresses (and file:// pages) to the default web browser.
            OsInfo.Open(url);
            return;
        }
        if (DefaultBrowserCommand() is { } cmd && Split(cmd) is { } parts)
        {
            // Use the browser's own registered command line, e.g.  "firefox.exe" -osint -url "%1"
            var args = parts.Args.Contains("%1") ? parts.Args.Replace("%1", url.Replace("\"", "%22")) : $"{parts.Args} \"{url}\"".Trim();
            try { Process.Start(new ProcessStartInfo(parts.Exe, args) { UseShellExecute = false }); return; }
            catch (Exception e) { AppPaths.Log("Default browser failed: " + e.Message); }
        }
        // Fallback: let Windows decide (still a browser for http/https links).
        Process.Start(new ProcessStartInfo(url.StartsWith("file:") ? pathOrUrl : url) { UseShellExecute = true });
    }

    /// <summary>The command line registered for the user's default https (or http) handler, or null.</summary>
    public static string? DefaultBrowserCommand()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var scheme in new[] { "https", "http" })
        {
            try
            {
                using var choice = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{scheme}\UserChoice");
                var progId = choice?.GetValue("ProgId") as string;
                if (string.IsNullOrEmpty(progId)) continue;
                using var cmd = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
                if (cmd?.GetValue(null) is string c && Split(c) is { } p && File.Exists(p.Exe)) return c;
            }
            catch { }
        }
        return null;
    }

    /// <summary>"C:\x\chrome.exe" --single-argument %1  ->  (C:\x\chrome.exe, --single-argument %1)</summary>
    public static (string Exe, string Args)? Split(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? (command[1..end], command[(end + 1)..].Trim()) : null;
        }
        var i = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? (command[..(i + 4)], command[(i + 4)..].Trim()) : null;
    }
}
