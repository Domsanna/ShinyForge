using System.Reflection;

namespace ShinyForge.Core;

/// <summary>
/// Version and build number of this copy of ShinyForge, and where its releases are published.
/// The build number counts the changes ShinyForge has gone through: build.ps1 sets it to the number of commits in the
/// source repository (every change is one commit), so the version is 1.0.&lt;build&gt;. A build made outside the
/// repository shows build 0.
/// </summary>
public static class AppInfo
{
    /// <summary>GitHub repository ("owner/name") the update check reads its releases from.</summary>
    public const string Repo = "Domsanna/ShinyForge";
    public static string RepoUrl => $"https://github.com/{Repo}";
    public static string ReleasesUrl => $"{RepoUrl}/releases";

    public static Version Version { get; } = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0, 0);
    public static int Build => Math.Max(0, Version.Build);
    /// <summary>"1.0" </summary>
    public static string ShortVersion => $"{Version.Major}.{Version.Minor}";
    /// <summary>"1.0 (build 12)"</summary>
    public static string Display => $"{ShortVersion} (build {Build})";

    /// <summary>Parses a release tag such as "v1.0.12" (or "1.0.12") into a comparable version.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('v', 'V');
        var cut = t.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut > 0) t = t[..cut];
        return Version.TryParse(t, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    /// <summary>This copy's version as Major.Minor.Build (the revision part is ignored).</summary>
    public static Version Comparable => new(Version.Major, Version.Minor, Build);
}
