using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace MudMaps.Shared;

/// <summary>
/// Loads the map data. Prefers the live service (api/map beside the app) when the app is hosted by MudMaps.Server,
/// and falls back to the snapshot file baked into the app when it is not.
/// </summary>
/// <summary>The service's <c>api/status</c>: when the data was last confirmed current, and when the map body was built.</summary>
public sealed record ServiceStatus(DateTimeOffset? CheckedAt, string? MapGeneratedAt, bool PrsLive);

public sealed class MapDataService(HttpClient http) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, Task<MapData>> _loads = [];
    private HubConnection? _hub;

    /// <summary>True when the last load came from the live service.</summary>
    public bool IsLive { get; private set; }

    /// <summary>The map from the last <see cref="LoadLiveAsync"/>, test code included.</summary>
    public MapData? Current { get; private set; }

    /// <param name="file">"mud.json" (two repos) or "mud-v2.json" (every mapped repo, PRs per room).</param>
    public Task<MapData> LoadAsync(string file = "mud.json")
    {
        if (!_loads.TryGetValue(file, out var load))
        {
            load = http.GetFromJsonAsync<MapData>($"_content/MudMaps.Shared/{file}", Options)
                .ContinueWith(t => t.Result ?? throw new InvalidOperationException($"{file} was empty."), TaskScheduler.Default);
            _loads[file] = load;
        }
        return load;
    }

    /// <summary>The live map if the service is there, otherwise the snapshot.</summary>
    public async Task<MapData> LoadLiveAsync(string fallbackFile = "mud-v2.json")
    {
        try
        {
            using var response = await http.GetAsync(ServiceUri("api/map"));
            if (response.StatusCode == HttpStatusCode.OK
                && response.Content.Headers.ContentType?.MediaType == "application/json"
                && await response.Content.ReadFromJsonAsync<MapData>(Options) is { } live)
            {
                IsLive = true;
                return Current = live;
            }
        }
        catch (HttpRequestException)
        {
            // No service in front of this app: use the snapshot.
        }
        catch (JsonException)
        {
        }
        IsLive = false;
        return Current = await LoadAsync(fallbackFile);
    }

    /// <summary>
    /// When the service last confirmed the data current (<c>api/status</c>), or null when there is no service or it does not answer.
    /// Moves without the map changing, so it is fetched on its own.
    /// </summary>
    public async Task<ServiceStatus?> LoadStatusAsync()
    {
        if (!IsLive) return null;
        try
        {
            using var response = await http.GetAsync(ServiceUri("api/status"));
            if (response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "application/json")
            {
                return await response.Content.ReadFromJsonAsync<ServiceStatus>(Options);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // The footer falls back to the map's own stamp.
        }
        return null;
    }

    /// <summary>Call <paramref name="onUpdate"/> whenever the service publishes a new map. No-op without the service.</summary>
    public async Task WatchAsync(Func<Task> onUpdate)
    {
        if (!IsLive || _hub is not null) return;
        _hub = new HubConnectionBuilder()
            .WithUrl(ServiceUri("hubs/map"))
            .WithAutomaticReconnect()
            .Build();
        _hub.On<string>("mapUpdated", _ => onUpdate());
        try
        {
            await _hub.StartAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            // Live updates are a bonus; the map still works without them.
        }
    }

    /// <summary>
    /// The service sits one level above each app ("/mudmap/colony/" -> "/mudmap/api/map"), so the same build
    /// works at the site root or under any path base.
    /// </summary>
    private Uri ServiceUri(string path) => new(http.BaseAddress!, "../" + path);

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
    }
}
