using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MudMaps.Shared.Research;

/// <summary>
/// The research endpoints beside the map service: graphify tools (<c>api/research</c>) and the open pull request cache
/// (<c>api/research/prs</c>, <c>api/research/pr</c>). Errors come back as an outcome with a reader-facing message.
/// </summary>
public sealed class ResearchClient(HttpClient http)
{
    public const string Overloaded = "Research is busy right now.";
    public const string Timeout = "The graph is reloading or the query is too broad; lower depth or budget.";

    public Task<ResearchOutcome<ResearchResponse>> CallAsync(string tool, IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct = default) =>
        SendAsync<ResearchResponse>(() => http.PostAsJsonAsync(ServiceUri("api/research"), new { tool, arguments }, ct), ct);

    /// <summary>Open pull requests in <paramref name="repos"/> (full names; none means every repo).</summary>
    public Task<ResearchOutcome<PrList>> PrsAsync(IEnumerable<string> repos, bool actionable, CancellationToken ct = default)
    {
        var query = string.Join("&", repos.Select(r => "repo=" + Uri.EscapeDataString(r)).Append($"actionable={(actionable ? "true" : "false")}"));
        return SendAsync<PrList>(() => http.GetAsync(ServiceUri("api/research/prs?" + query), ct), ct);
    }

    public Task<ResearchOutcome<PrItem>> PrAsync(string repo, int number, CancellationToken ct = default) =>
        SendAsync<PrItem>(() => http.GetAsync(ServiceUri($"api/research/pr?repo={Uri.EscapeDataString(repo)}&number={number}"), ct), ct);

    private static async Task<ResearchOutcome<T>> SendAsync<T>(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await send();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new(default, 0, "The research service did not answer.");
        }
        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
                    return value is null ? new(default, status, "The research service sent an empty answer.") : new(value, status, null);
                }
                catch (JsonException)
                {
                    return new(default, status, "The research service sent an answer this page cannot read.");
                }
            }
            var retry = response.Headers.RetryAfter?.Delta is { } d ? (int)Math.Ceiling(d.TotalSeconds)
                : response.Headers.RetryAfter?.Date is { } at ? Math.Max(1, (int)Math.Ceiling((at - DateTimeOffset.UtcNow).TotalSeconds)) : (int?)null;
            var message = response.StatusCode switch
            {
                HttpStatusCode.GatewayTimeout => Timeout,
                HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => Overloaded,
                _ => await ErrorTextAsync(response, ct) ?? $"The research service answered {status}.",
            };
            return new(default, status, message, retry);
        }
    }

    private static async Task<string?> ErrorTextAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The service sits one level above each app, as for <c>api/map</c> (see <see cref="MapDataService"/>).</summary>
    private Uri ServiceUri(string path) => new(http.BaseAddress!, "../" + path);
}
