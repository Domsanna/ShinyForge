using System.Net.Http.Json;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>The one way ShinyForge makes HTTP clients (GitHub API and downloads): its own User-Agent, GitHub's JSON type.</summary>
internal static class WebClients
{
    /// <param name="githubJson">false for plain file downloads (no GitHub API Accept header).</param>
    public static HttpClient Make(TimeSpan timeout, bool githubJson = true)
    {
        var h = new HttpClient { Timeout = timeout };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"ShinyForge/{AppInfo.Comparable}");
        if (githubJson) h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    private static readonly HttpClient NoRedirect = MakeNoRedirect();
    private static HttpClient MakeNoRedirect()
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"ShinyForge/{AppInfo.Comparable}");
        return h;
    }
    private static readonly HttpClient Api = Make(TimeSpan.FromSeconds(20));

    /// <summary>
    /// The newest release's tag of a GitHub project ("v0.66.7"). Read from github.com/…/releases/latest, which redirects to
    /// …/releases/tag/&lt;tag&gt; - that page has no quota. GitHub's API allows only 60 questions an hour per computer, and
    /// when they ran out every tool's check failed quietly and "No updates found" was shown. The API is only the fallback.
    /// </summary>
    public static async Task<string> LatestGitHubTag(string repo)
    {
        try
        {
            using var resp = await NoRedirect.GetAsync($"https://github.com/{repo}/releases/latest", HttpCompletionOption.ResponseHeadersRead);
            var to = resp.Headers.Location?.ToString() ?? "";
            var i = to.IndexOf("/releases/tag/", StringComparison.Ordinal);
            if (i >= 0 && Uri.UnescapeDataString(to[(i + "/releases/tag/".Length)..]).Trim('/') is { Length: > 0 } tag) return tag;
        }
        catch (Exception e) { AppPaths.Log($"github.com latest release of {repo}: {e.Message} - asking the API"); }
        var j = await Api.GetFromJsonAsync<System.Text.Json.Nodes.JsonObject>($"https://api.github.com/repos/{repo}/releases/latest");
        return j?["tag_name"]?.ToString() is { Length: > 0 } t ? t : throw new IOException($"GitHub didn't say which {repo} release is the newest.");
    }
}
