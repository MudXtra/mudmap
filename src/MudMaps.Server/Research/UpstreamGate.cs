using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace MudMaps.Server.Research;

/// <summary>
/// The one-at-a-time door to graphify, which in practice handles one request at a time: one permit,
/// a queue of 8 served oldest first. Client calls, the warmer and nothing else take it; health probes are skipped while
/// it is held, because graphify cannot answer them then.
/// </summary>
public sealed class UpstreamGate(IOptions<ResearchOptions> options) : IDisposable
{
    private readonly ConcurrencyLimiter _limiter = new(new ConcurrencyLimiterOptions
    {
        PermitLimit = 1,
        QueueLimit = options.Value.UpstreamQueue,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    });

    private int _held;

    /// <summary>True while a call holds the permit.</summary>
    public bool Busy => Volatile.Read(ref _held) > 0;

    /// <summary>The permit, or null when the queue is full. Throws when <paramref name="ct"/> fires while queued.</summary>
    public async Task<IDisposable?> AcquireAsync(CancellationToken ct)
    {
        var lease = await _limiter.AcquireAsync(1, ct);
        if (!lease.IsAcquired)
        {
            lease.Dispose();
            return null;
        }
        Interlocked.Increment(ref _held);
        return new Permit(this, lease);
    }

    public void Dispose() => _limiter.Dispose();

    private sealed class Permit(UpstreamGate gate, RateLimitLease lease) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 1) return;
            Interlocked.Decrement(ref gate._held);
            lease.Dispose();
        }
    }
}
