namespace MudMaps.Server;

/// <summary>
/// The one app a visitor last opened (a small cookie that records the
/// choice of City or Space and returns the visitor there next time). First-party, holds one of two words, no identity and no tracking — the only
/// thing it remembers is which map they were looking at. Still no third-party analytics and no per-user profile.
/// </summary>
public static class AppPreference
{
    public const string CookieName = "mm-app";

    /// <summary>The apps the UI offers: the chooser lists them, the root redirect follows them and only
    /// they are remembered. Colony and Islands are still served by direct URL but are not offered, so a saved
    /// <c>colony</c> or <c>islands</c> shows the chooser and opening one never overwrites a saved Space or City.
    /// Must match <c>MudMaps.Shared.MapNav.All</c> (seen by a test through <c>UsageEvents.Names</c>).</summary>
    public static readonly IReadOnlyList<string> Listed = ["space", "city"];

    /// <summary>The offered apps this deployment actually serves, in <see cref="Listed"/> order.</summary>
    public static IReadOnlyList<string> Offered(IReadOnlyCollection<string> sites) =>
        Listed.Where(s => sites.Contains(s, StringComparer.Ordinal)).ToList();

    /// <summary>A year, refreshed every time an app is opened, so a returning visitor is never asked again.</summary>
    private static readonly CookieOptions Options = new()
    {
        Path = "/", // host root, so Space and City share one value and switching apps overwrites it
        MaxAge = TimeSpan.FromDays(365),
        SameSite = SameSiteMode.Lax,
        Secure = true,
        HttpOnly = true, // only the server reads it, on the root redirect
    };

    /// <summary>The saved app, or null when the value is absent or not one of the given apps — a stale value left
    /// behind by a renamed or unlisted site must never send someone to a 404 or to an app the UI no longer offers.
    /// Callers pass <see cref="Offered"/>.</summary>
    public static string? Read(string? cookieValue, IReadOnlyCollection<string> sites) =>
        cookieValue is { Length: > 0 } value && sites.Contains(value, StringComparer.Ordinal) ? value : null;

    public static string? Read(HttpRequest request, IReadOnlyCollection<string> sites) =>
        Read(request.Cookies[CookieName], sites);

    /// <summary>Remembers the app this response belongs to. Called when an app's page is served, so a split-button
    /// pick is recorded by the navigation itself — no script needed to make the choice stick.</summary>
    public static void Set(HttpResponse response, string segment) => response.Cookies.Append(CookieName, segment, Options);
}
