using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace MudMaps.Shared;

/// <summary>
/// Loads the map data. Prefers the live service (api/map beside the app) when the app is hosted by MudMaps.Server,
/// and falls back to the snapshot file baked into the app when it is not.
/// </summary>
public sealed class MapDataService(HttpClient http) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, Task<MapData>> _loads = [];
    private HubConnection? _hub;

    /// <summary>True when the last load came from the live service.</summary>
    public bool IsLive { get; private set; }

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
                return live;
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
        return await LoadAsync(fallbackFile);
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
