using System.Security.Cryptography;
using System.Text.Json;

namespace MudMaps.Server.Maps;

/// <summary>The current map, serialized once and served as-is until the next rebuild.</summary>
public sealed class MapState
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private volatile Snapshot? _current;

    public sealed record Snapshot(byte[] Body, string ETag, string Version, DateTimeOffset BuiltAt, int Areas, int Rooms, bool PrsLive);

    public Snapshot? Current => _current;

    /// <summary>The GitHub token's expiry as last reported by GitHub.</summary>
    public DateTimeOffset? TokenExpiresAt { get; set; }

    /// <summary>True when the token expires within two weeks; live PR data stops at expiry.</summary>
    public bool TokenExpiringSoon(DateTimeOffset now) => TokenExpiresAt is { } at && at - now < TimeSpan.FromDays(14);

    public Snapshot Publish(MapDto map, DateTimeOffset builtAt)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(map, Json);
        var hash = Convert.ToHexString(SHA256.HashData(body))[..16].ToLowerInvariant();
        var snapshot = new Snapshot(body, $"\"{hash}\"", hash, builtAt, map.Hoods.Count, map.Clusters.Count, map.PrsLive);
        _current = snapshot;
        return snapshot;
    }
}
