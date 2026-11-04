using System;
using System.Net.Http;

namespace Jellyfin.Plugin.WrestlingFinder.Services;

/// <summary>
/// Shared HTTP client.
/// </summary>
internal static class Http
{
    /// <summary>
    /// Gets the shared client.
    /// </summary>
    public static HttpClient Client { get; } = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("JellyfinWrestlingFinder/1.0 (+https://github.com/Ollyisgreat/jellyfin-wrestling-finder)");
        return client;
    }
}
