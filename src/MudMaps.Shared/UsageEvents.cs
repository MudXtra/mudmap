using System.Net.Http.Json;

namespace MudMaps.Shared;

/// <summary>
/// The client-side events the apps report to <c>api/track</c>: clicks that make no server request of
/// their own. The server's allow-list (<c>UsageCounters.TrackEvents</c>) must equal <see cref="Names"/>; a test holds
/// the two together.
/// </summary>
public static class UsageEvents
{
    /// <summary>The hero's MCP chip, which opens the Connect page inside the app.</summary>
    public const string McpChip = "mcp_chip_click";

    /// <summary>A pick in the hero's app switch.</summary>
    public static string Nav(string segment) => "nav_" + segment;

    /// <summary>Every event an app can send.</summary>
    public static IReadOnlyList<string> Names { get; } = [McpChip, .. MapNav.All.Select(Nav)];
}

/// <summary>Sends a <see cref="UsageEvents"/> name to <c>api/track</c>, fire-and-forget. Counting is best effort: a
/// failure is never shown, and a static host with no service simply drops it.</summary>
public sealed class UsageTracker(HttpClient http)
{
    // The browser fetch options key System.Net.Http's browser handler reads (the same key SetBrowserRequestOption sets).
    private static readonly HttpRequestOptionsKey<IDictionary<string, object>> FetchOptions = new("WebAssemblyFetchOptions");

    public void Track(string eventName) => _ = SendAsync(eventName);

    private async Task SendAsync(string eventName)
    {
        try
        {
            // The service sits one level above the app, as api/map does.
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(http.BaseAddress!, "../api/track"))
            {
                Content = JsonContent.Create(new { @event = eventName }), // application/json; charset=utf-8
            };
            // keepalive: an app-switch click navigates to another app at once, and the count must outlive the page.
            request.Options.Set(FetchOptions, new Dictionary<string, object> { ["keepalive"] = true });
            using var response = await http.SendAsync(request);
        }
        catch (Exception)
        {
            // best effort; see the summary
        }
    }
}
