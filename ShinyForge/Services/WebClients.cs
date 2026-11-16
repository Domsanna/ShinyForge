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
}
