using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MudMaps.Server.Maps;

/// <summary>What <c>GET /api/status</c> returns. Small and cheap; nothing here is part of the map body.</summary>
public sealed record StatusDto(DateTimeOffset? CheckedAt, string? MapGeneratedAt, bool PrsLive);

/// <summary>
/// Answers "when was the data last confirmed current?" without touching the map. That is the older of the last
/// successful pull request refresh and the last successful repository refresh (the hourly run, which records itself in
/// <c>_status/last-run.json</c> under the repos root). Without a usable run record it falls back to the newest graph file.
/// </summary>
public sealed class StatusReport(IOptions<MapsOptions> options, MapState state, TimeProvider clock)
{
    private const string RunFile = "last-run.json";

    private (DateTime Stamp, long Length, DateTimeOffset? Ended) _runCache;

    public StatusDto Build()
    {
        var opts = options.Value;
        var repoRefresh = LastSuccessfulRepoRefresh(opts) ?? NewestGraphWrite(opts);
        var prs = state.ActivityRefreshedAt;

        DateTimeOffset? checkedAt = (repoRefresh, prs) switch
        {
            ({ } r, { } p) => r < p ? r : p,
            ({ } r, null) => r,
            _ => null,
        };
        // A clock slightly behind the writer must not show a time in the future.
        if (checkedAt is { } at && at > clock.GetUtcNow()) checkedAt = clock.GetUtcNow();

        return new StatusDto(checkedAt, state.Current?.GeneratedAt, prs is not null);
    }

    /// <summary>The "ended" time of the last repository refresh that finished with rc 0; null if missing, unreadable or failed.</summary>
    private DateTimeOffset? LastSuccessfulRepoRefresh(MapsOptions opts)
    {
        var path = Path.Combine(opts.ReposRoot, "_status", RunFile);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            lock (this)
            {
                if (_runCache.Stamp == info.LastWriteTimeUtc && _runCache.Length == info.Length && _runCache.Stamp != default) return _runCache.Ended;
                var ended = ReadEnded(path);
                _runCache = (info.LastWriteTimeUtc, info.Length, ended);
                return ended;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadEnded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("rc", out var rc) || rc.ValueKind != JsonValueKind.Number || !rc.TryGetInt32(out var code) || code != 0) return null;
        if (!root.TryGetProperty("ended", out var ended) || ended.ValueKind != JsonValueKind.String) return null;
        return DateTimeOffset.TryParse(ended.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUniversalTime() : null;
    }

    private static DateTimeOffset? NewestGraphWrite(MapsOptions opts)
    {
        DateTime newest = default;
        foreach (var repo in opts.Repos)
        {
            var path = Path.Combine(opts.ReposRoot, repo.FullName, "graphify-out", "graph.json");
            try
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                // A missing file reports 1601; ignore it.
                if (stamp.Year > 2000 && stamp > newest) newest = stamp;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return newest == default ? null : new DateTimeOffset(newest, TimeSpan.Zero);
    }
}
