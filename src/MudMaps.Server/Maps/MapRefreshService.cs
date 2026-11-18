using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using MudMaps.Server.GitHub;

namespace MudMaps.Server.Maps;

/// <summary>
/// Keeps the map current: reloads a repository's graph when its graph.json changes, refreshes pull
/// request activity on a timer, and tells connected maps when the published version changes.
/// Never runs git, Graphify or any other process.
/// </summary>
public sealed class MapRefreshService(
    IOptions<MapsOptions> options,
    MapState state,
    GitHubActivityClient github,
    IHubContext<MapHub> hub,
    TimeProvider clock,
    ILogger<MapRefreshService> log) : BackgroundService
{
    private readonly Dictionary<string, (DateTime Stamp, RepoGraph Graph)> _graphs = [];
    private readonly Dictionary<string, RepoActivity> _activity = [];
    private DateTimeOffset _nextPrRefresh = DateTimeOffset.MinValue;
    private bool _prsLive;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var changed = LoadChangedGraphs(opts);
                if (clock.GetUtcNow() >= _nextPrRefresh)
                {
                    changed |= await RefreshActivityAsync(opts, stoppingToken);
                    _nextPrRefresh = clock.GetUtcNow().AddMinutes(opts.PullRequestRefreshMinutes);
                }
                if (changed || state.Current is null) await PublishAsync(opts, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Keep serving the last good map; try again on the next tick.
                log.LogError(ex, "Map refresh failed; keeping the previous map");
            }
            await Task.Delay(TimeSpan.FromSeconds(opts.GraphPollSeconds), clock, stoppingToken);
        }
    }

    private bool LoadChangedGraphs(MapsOptions opts)
    {
        var changed = false;
        foreach (var repo in opts.Repos)
        {
            var path = GraphPath(opts, repo.FullName);
            if (!File.Exists(path))
            {
                log.LogWarning("No graph for {Repo} at {Path}", repo.FullName, path);
                continue;
            }
            var stamp = File.GetLastWriteTimeUtc(path);
            if (_graphs.TryGetValue(repo.FullName, out var cached) && cached.Stamp == stamp) continue;
            using var stream = File.OpenRead(path);
            var graph = RepoGraph.Load(repo.FullName, repo.Name, repo.AreaBudget, stream, repo.Name == MapComposer.MudBlazor);
            _graphs[repo.FullName] = (stamp, graph);
            log.LogInformation("Loaded graph for {Repo}: {Nodes} items, {Rooms} rooms", repo.FullName, graph.Nodes, graph.Rooms.Count);
            changed = true;
        }
        return changed;
    }

    private async Task<bool> RefreshActivityAsync(MapsOptions opts, CancellationToken ct)
    {
        var token = ReadToken(opts);
        if (token is null)
        {
            if (_prsLive) log.LogWarning("GitHub token unavailable; pull request activity is no longer live");
            _prsLive = false;
            return false;
        }
        var since = clock.GetUtcNow().AddDays(-opts.WindowDays);
        var changed = false;
        foreach (var repo in opts.Repos)
        {
            try
            {
                var act = await github.GetActivityAsync(repo.FullName, token, since, ct);
                if (!_activity.TryGetValue(repo.FullName, out var old) || !SameActivity(old, act)) changed = true;
                _activity[repo.FullName] = act;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                log.LogWarning("GitHub activity for {Repo} failed: {Message}", repo.FullName, ex.Message);
            }
        }
        _prsLive = true;
        state.TokenExpiresAt = github.TokenExpiresAt;
        if (state.TokenExpiringSoon(clock.GetUtcNow()))
        {
            log.LogWarning("GitHub token expires {Expiry:u}; regenerate GitHub_ReadOnly (1Password, Atlas Infrastructure) before then", state.TokenExpiresAt);
        }
        return changed;
    }

    private async Task PublishAsync(MapsOptions opts, CancellationToken ct)
    {
        var graphs = opts.Repos.Where(r => _graphs.ContainsKey(r.FullName)).Select(r => _graphs[r.FullName].Graph).ToList();
        if (graphs.Count == 0) return;
        var map = MapComposer.Compose(graphs, _activity, opts.WindowDays, clock.GetUtcNow(), _prsLive);
        var previous = state.Current?.Version;
        var snapshot = state.Publish(map, clock.GetUtcNow());
        if (snapshot.Version == previous) return;
        log.LogInformation("Published map {Version}: {Areas} areas, {Rooms} rooms", snapshot.Version, snapshot.Areas, snapshot.Rooms);
        await hub.Clients.All.SendAsync("mapUpdated", snapshot.Version, ct);
    }

    private static bool SameActivity(RepoActivity a, RepoActivity b) =>
        a.Stars == b.Stars && a.PullRequests.Count == b.PullRequests.Count
        && a.PullRequests.Zip(b.PullRequests).All(p => p.First.Number == p.Second.Number && p.First.MergedAt == p.Second.MergedAt);

    private static string GraphPath(MapsOptions opts, string fullName) =>
        Path.Combine(opts.ReposRoot, fullName, "graphify-out", "graph.json");

    private static string? ReadToken(MapsOptions opts)
    {
        var file = opts.GitHubTokenFile;
        if (string.IsNullOrWhiteSpace(file))
        {
            // systemd LoadCredential= puts secrets here.
            var dir = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
            if (dir is not null) file = Path.Combine(dir, "github-token");
        }
        if (file is null || !File.Exists(file)) return null;
        var token = File.ReadAllText(file).Trim();
        return token.Length > 0 ? token : null;
    }
}
