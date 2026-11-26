using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace MudMaps.Server.Research;

/// <summary>
/// The research endpoints' per-client and global limits, keyed on the research client
/// key. The one-at-a-time upstream gate is separate (<see cref="UpstreamGate"/>).
/// </summary>
public sealed class ResearchLimits : IDisposable
{
    private readonly PartitionedRateLimiter<string> _perClient;
    private readonly PartitionedRateLimiter<string> _concurrency;
    private readonly PartitionedRateLimiter<string> _triage;
    private readonly FixedWindowRateLimiter _global;

    public ResearchLimits(IOptions<ResearchOptions> options)
    {
        var o = options.Value;
        _perClient = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = o.ClientCallsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        _concurrency = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetConcurrencyLimiter(key, _ =>
            new ConcurrencyLimiterOptions { PermitLimit = o.ClientConcurrency, QueueLimit = 0 }));
        _triage = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = o.TriageCallsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        _global = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            { PermitLimit = o.GlobalCallsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    }

    public enum Verdict { Ok, ClientLimited, GlobalLimited }

    /// <summary>Counts one call against the client's window and the global window. Every method counts.</summary>
    public (Verdict Verdict, int RetryAfterSeconds) Count(string clientKey)
    {
        using var client = _perClient.AttemptAcquire(clientKey);
        if (!client.IsAcquired) return (Verdict.ClientLimited, RetryAfter(client, 60));
        using var global = _global.AttemptAcquire();
        if (!global.IsAcquired) return (Verdict.GlobalLimited, RetryAfter(global, 60));
        return (Verdict.Ok, 0);
    }

    /// <summary>The client's one in-flight call, or null when it already has one.</summary>
    public RateLimitLease? Enter(string clientKey)
    {
        var lease = _concurrency.AttemptAcquire(clientKey);
        if (lease.IsAcquired) return lease;
        lease.Dispose();
        return null;
    }

    /// <summary>triage_prs' extra window; the retry hint in seconds, or 0 when allowed.</summary>
    public int CountTriage(string clientKey)
    {
        using var lease = _triage.AttemptAcquire(clientKey);
        return lease.IsAcquired ? 0 : RetryAfter(lease, 60);
    }

    private static int RetryAfter(RateLimitLease lease, int fallback) =>
        lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? Math.Max(1, (int)Math.Ceiling(after.TotalSeconds)) : fallback;

    public void Dispose()
    {
        _perClient.Dispose();
        _concurrency.Dispose();
        _triage.Dispose();
        _global.Dispose();
    }
}
