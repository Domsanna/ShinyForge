using System.Reflection;

namespace ShinyForge.Core;

/// <summary>
/// Version of this copy of ShinyForge, and where its releases are published. Versions are 1.X.Z: X goes up for big
/// updates (Z starts again at 0), Z for patches and small fixes such as answers to error reports. build.ps1 takes the
/// version from version.txt in the source folder.
/// </summary>
public static class AppInfo
{
    /// <summary>GitHub repository ("owner/name") the update check reads its releases from.</summary>
    public const string Repo = "Domsanna/ShinyForge";
    public static string RepoUrl => $"https://github.com/{Repo}";
    public static string ReleasesUrl => $"{RepoUrl}/releases";

    public static Version Version { get; } = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0, 0);
    /// <summary>Z of 1.X.Z: the patch number.</summary>
    public static int Patch => Math.Max(0, Version.Build);
    /// <summary>"1.0.15"</summary>
    public static string Display => Text(Comparable);
    /// <summary>A version as ShinyForge shows it: "1.0.15".</summary>
    public static string Text(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

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
    public static Version Comparable => new(Version.Major, Version.Minor, Patch);
}
