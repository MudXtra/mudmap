using System.Security.Cryptography;
using System.Text.Json;
using MudMaps.Server.GitHub;

namespace MudMaps.Server.Maps;

/// <summary>The current map, serialized once and served as-is until the next rebuild.</summary>
public sealed class MapState : IPrCacheSnapshot, IRepoGraphs
{
    private volatile IReadOnlyDictionary<string, RepoGraph> _graphs = new Dictionary<string, RepoGraph>();
    private volatile OpenPrSnapshot _openPrs = OpenPrSnapshot.Empty;

    /// <summary>The loaded graph of each repository, by full name; replaced whole when one reloads.</summary>
    public IReadOnlyDictionary<string, RepoGraph> Graphs
    {
        get => _graphs;
        set => _graphs = value;
    }

    public RepoGraph? Graph(string fullName) => _graphs.GetValueOrDefault(fullName);

    /// <summary>Open pull requests from the last refresh, for the PR tools. Never fetched on demand.</summary>
    public OpenPrSnapshot OpenPrs
    {
        get => _openPrs;
        set => _openPrs = value;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private volatile Snapshot? _current;

    public sealed record Snapshot(byte[] Body, string ETag, string Version, DateTimeOffset BuiltAt, int Areas, int Rooms, bool PrsLive, string GeneratedAt);

    public Snapshot? Current => _current;

    private long _activityTicks;

    /// <summary>
    /// When pull request activity was last fetched successfully for every repo, or null when PR data is not live.
    /// Moves every refresh without touching the map body, so the map's ETag stays put on a quiet night.
    /// </summary>
    public DateTimeOffset? ActivityRefreshedAt
    {
        get => Interlocked.Read(ref _activityTicks) is var t and > 0 ? new DateTimeOffset(t, TimeSpan.Zero) : null;
        set => Interlocked.Exchange(ref _activityTicks, value?.UtcTicks ?? 0);
    }

    /// <summary>The GitHub token's expiry as last reported by GitHub.</summary>
    public DateTimeOffset? TokenExpiresAt { get; set; }

    /// <summary>True when the token expires within two weeks; live PR data stops at expiry.</summary>
    public bool TokenExpiringSoon(DateTimeOffset now) => TokenExpiresAt is { } at && at - now < TimeSpan.FromDays(14);

    public Snapshot Publish(MapDto map, DateTimeOffset builtAt)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(map, Json);
        var hash = Convert.ToHexString(SHA256.HashData(body))[..16].ToLowerInvariant();
        var snapshot = new Snapshot(body, $"\"{hash}\"", hash, builtAt, map.Hoods.Count, map.Clusters.Count, map.PrsLive, map.GeneratedAt);
        _current = snapshot;
        return snapshot;
    }
}

/// <summary>Read-only access to the loaded repository graphs, without reloading anything.</summary>
public interface IRepoGraphs
{
    RepoGraph? Graph(string fullName);
}
