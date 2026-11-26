using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace MudMaps.Server.Research;

public enum ResearchState { Disabled, Starting, Ready, Down }

/// <summary>What the research gateway needs from the child's supervisor.</summary>
public interface IGraphifyHost
{
    ResearchState State { get; }
    int Port { get; }

    /// <summary>The current child's loopback key; null while no child runs.</summary>
    string? Key { get; }

    /// <summary>Seconds a caller should wait before trying again while the state is not ready.</summary>
    int RetryAfterSeconds { get; }

    /// <summary>A client call timed out: close the gate until a probe succeeds, and restart if it never does (C10).</summary>
    void ReportUpstreamTimeout();

    /// <summary>Kills the child as if it had crashed. Reachable only through the Development test hook.</summary>
    void KillForTest();
}

/// <summary>
/// Runs graphify as a child process and keeps it healthy: an explicit
/// environment with a fresh key per start, readiness and liveness probes, restarts with backoff, a memory recycle, a
/// warm-up through the upstream gate, and SIGTERM then SIGKILL on stop.
/// </summary>
public sealed partial class GraphifyHost(
    IOptions<ResearchOptions> research,
    GraphifyUpstream upstream,
    UpstreamGate gate,
    TimeProvider clock,
    ILogger<GraphifyHost> log) : BackgroundService, IGraphifyHost
{
    private const int MaxLogLine = 500;
    private readonly ResearchOptions _o = research.Value;
    private volatile string? _key;
    private int _state = (int)ResearchState.Starting;
    private long _timeoutReportedTicks;
    private DateTimeOffset _nextStartAt;
    private readonly SemaphoreSlim _wake = new(0);
    private Process? _child;

    public ResearchState State => _o.Enabled ? (ResearchState)Volatile.Read(ref _state) : ResearchState.Disabled;
    public int Port => _o.Port;
    public string? Key => _key;

    public int RetryAfterSeconds => State == ResearchState.Down
        ? (int)Math.Clamp(Math.Ceiling((_nextStartAt - clock.GetUtcNow()).TotalSeconds) + 5, 5, 60)
        : 5;

    private void SetState(ResearchState s) => Volatile.Write(ref _state, (int)s);

    public void ReportUpstreamTimeout()
    {
        Interlocked.CompareExchange(ref _timeoutReportedTicks, clock.GetUtcNow().UtcTicks, 0);
        if (State == ResearchState.Ready) SetState(ResearchState.Starting);
        log.LogWarning("graphify call timed out; research gate closed until a health probe succeeds");
        _wake.Release();
    }

    public void KillForTest()
    {
        try { _child?.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    /// <summary>Delay before restart attempt <paramref name="attempt"/> (0-based): 1, 2, 4, 8, 16, then 30 s.</summary>
    public static TimeSpan Backoff(IReadOnlyList<double> steps, int attempt) =>
        TimeSpan.FromSeconds(steps[Math.Min(attempt, steps.Count - 1)]);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_o.Enabled) return;
        PrepareTempDir();
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            DateTimeOffset? healthySince = null;
            try
            {
                var child = Start();
                _child = child;
                healthySince = await SuperviseAsync(child, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                log.LogError("graphify could not start: {Message}", ex.Message);
            }
            finally
            {
                await StopChildAsync();
                _child?.Dispose();
                _child = null;
                _key = null;
                Interlocked.Exchange(ref _timeoutReportedTicks, 0);
                SetState(ResearchState.Down);
            }
            if (stoppingToken.IsCancellationRequested) break;
            if (healthySince is { } since && clock.GetUtcNow() - since >= TimeSpan.FromSeconds(_o.BackoffResetSeconds)) attempt = 0;
            var delay = Backoff(_o.RestartBackoffSeconds, attempt++);
            _nextStartAt = clock.GetUtcNow() + delay;
            log.LogWarning("graphify restarting in {Delay:0.#} s (attempt {Attempt})", delay.TotalSeconds, attempt);
            try { await Task.Delay(delay, clock, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await StopChildAsync();
    }

    private void PrepareTempDir()
    {
        Directory.CreateDirectory(_o.TempDir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_o.TempDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>The child's whole environment: exactly these keys, nothing inherited from this process.</summary>
    internal static Dictionary<string, string> ChildEnvironment(ResearchOptions o, string key) => new()
    {
        ["GRAPHIFY_API_KEY"] = key,
        ["GRAPHIFY_GRAPH"] = o.MergedGraph,
        ["GRAPHIFY_PORT"] = o.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["GRAPHIFY_MAX_CONTEXTS"] = o.MaxContexts.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["GRAPHIFY_QUERY_LOG_DISABLE"] = "1",
        ["MALLOC_ARENA_MAX"] = "2",
        ["OPENBLAS_NUM_THREADS"] = "1",
        ["OMP_NUM_THREADS"] = "1",
        ["HOME"] = o.TempDir,
        ["TMPDIR"] = o.TempDir,
        ["PATH"] = o.ChildPath,
    };

    private Process Start()
    {
        var key = Base64Url(RandomNumberGenerator.GetBytes(32));
        var psi = new ProcessStartInfo(_o.Python)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = _o.TempDir,
        };
        foreach (var a in (string[])["-I", "-B", _o.Launcher, .. _o.ChildArgs]) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var (k, v) in ChildEnvironment(_o, key)) psi.Environment[k] = v;

        var child = new Process { StartInfo = psi, EnableRaisingEvents = true };
        child.OutputDataReceived += (_, e) => Relay(e.Data);
        child.ErrorDataReceived += (_, e) => Relay(e.Data);
        child.Start();
        child.StandardInput.Close();
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();
        try { child.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log.LogWarning("graphify priority could not be lowered: {Message}", ex.Message);
        }
        _key = key;
        SetState(ResearchState.Starting);
        log.LogInformation("graphify started (pid {Pid})", child.Id);
        return child;
    }

    private void Relay(string? line)
    {
        if (line is null) return;
        if (line.Length > MaxLogLine) line = line[..MaxLogLine];
        if (line.StartsWith("landlock:", StringComparison.Ordinal)) log.LogInformation("graphify: {Line}", line);
        else log.LogDebug("graphify: {Line}", line);
    }

    /// <summary>Runs until the child must be replaced; returns when it became healthy, or null if it never did.</summary>
    private async Task<DateTimeOffset?> SuperviseAsync(Process child, CancellationToken ct)
    {
        var exited = child.WaitForExitAsync(ct);
        var startDeadline = clock.GetUtcNow() + TimeSpan.FromSeconds(_o.StartupTimeoutSeconds);
        while (true)
        {
            if (exited.IsCompleted) return Exited(child, null);
            if (await ProbeAsync(TimeSpan.FromSeconds(_o.ProbeTimeoutSeconds), ct)) break;
            if (clock.GetUtcNow() >= startDeadline)
            {
                log.LogError("graphify did not become ready within {Seconds} s", _o.StartupTimeoutSeconds);
                return null;
            }
            await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(_o.StartupProbeIntervalSeconds), clock, ct));
            ct.ThrowIfCancellationRequested();
        }
        CheckHardening(child);
        var graphStamp = GraphStamp();
        await WarmAsync(ct);
        SetState(ResearchState.Ready);
        var healthySince = clock.GetUtcNow();
        log.LogInformation("graphify ready");

        var failures = 0;
        while (true)
        {
            var closedTicks = Interlocked.Read(ref _timeoutReportedTicks);
            var interval = closedTicks != 0 ? _o.StartupProbeIntervalSeconds : _o.ProbeIntervalSeconds;
            await Task.WhenAny(exited, _wake.WaitAsync(TimeSpan.FromSeconds(interval), ct));
            ct.ThrowIfCancellationRequested();
            if (exited.IsCompleted) return Exited(child, healthySince);

            closedTicks = Interlocked.Read(ref _timeoutReportedTicks);
            if (closedTicks != 0)
            {
                // After a 504 the gate stays closed until graphify answers again; if it never does, replace it (C10).
                var deadline = new DateTimeOffset(closedTicks, TimeSpan.Zero) + TimeSpan.FromSeconds(_o.UnresponsiveRestartSeconds);
                var left = deadline - clock.GetUtcNow();
                if (!gate.Busy && left > TimeSpan.Zero
                    && await ProbeAsync(TimeSpan.FromSeconds(Math.Max(1, Math.Min(_o.ProbeTimeoutSeconds, left.TotalSeconds))), ct))
                {
                    Interlocked.Exchange(ref _timeoutReportedTicks, 0);
                    SetState(ResearchState.Ready);
                    log.LogInformation("graphify answering again; research gate reopened");
                    continue;
                }
                if (clock.GetUtcNow() >= deadline)
                {
                    log.LogError("graphify unresponsive {Seconds} s after a timed-out call; restarting", _o.UnresponsiveRestartSeconds);
                    return healthySince;
                }
                continue;
            }

            // A call in flight means graphify is busy, not dead: neither probe nor recycle it now.
            if (gate.Busy) continue;
            if (RssMiB(child) is { } rss && rss > _o.RssLimitMiB)
            {
                log.LogWarning("graphify RSS {Rss} MiB is over {Limit} MiB while idle; recycling", rss, _o.RssLimitMiB);
                return healthySince;
            }
            if (GraphStamp() is var stamp && stamp != graphStamp)
            {
                graphStamp = stamp;
                log.LogInformation("Merged graph changed; warming graphify");
                await WarmAsync(ct);
                continue;
            }
            if (await ProbeAsync(TimeSpan.FromSeconds(_o.ProbeTimeoutSeconds), ct))
            {
                failures = 0;
                continue;
            }
            if (++failures >= _o.FailedProbesBeforeRestart)
            {
                log.LogError("graphify failed {Count} health probes in a row; restarting", failures);
                return healthySince;
            }
        }
    }

    private DateTimeOffset? Exited(Process child, DateTimeOffset? healthySince)
    {
        log.LogError("graphify exited with code {Code}", child.HasExited ? child.ExitCode : -1);
        return healthySince;
    }

    private async Task<bool> ProbeAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_key is not { } key) return false;
        var r = await upstream.PostAsync(_o.Port, key, GraphifyUpstream.InitializeRequest(), timeout, ct);
        return r.Outcome == UpstreamOutcome.Ok;
    }

    /// <summary>
    /// Pays graphify's cold-load cost before callers do: graph_stats and one small query through the gate, holding
    /// the permit, after every (re)start and every merged-graph change.
    /// </summary>
    private async Task WarmAsync(CancellationToken ct)
    {
        if (_key is not { } key) return;
        using var permit = await gate.AcquireAsync(ct);
        if (permit is null) return;
        var timeout = TimeSpan.FromSeconds(_o.WarmTimeoutSeconds);
        var started = clock.GetUtcNow();
        var stats = await upstream.PostAsync(_o.Port, key, GraphifyUpstream.ToolCall("warm-1", "graph_stats", []), timeout, ct);
        var query = await upstream.PostAsync(_o.Port, key, GraphifyUpstream.ToolCall("warm-2", "query_graph",
            new JsonObject { ["question"] = "mud", ["depth"] = 1, ["token_budget"] = 100 }), timeout, ct);
        log.LogInformation("graphify warm-up: graph_stats {Stats}, query_graph {Query} in {Seconds:0.0} s",
            stats.Outcome, query.Outcome, (clock.GetUtcNow() - started).TotalSeconds);
    }

    private (DateTime, long)? GraphStamp()
    {
        try
        {
            var f = new FileInfo(_o.MergedGraph);
            return f.Exists ? (f.LastWriteTimeUtc, f.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>run.py raises oom_score_adj to 1000 so graphify is the OOM victim before the map.</summary>
    private void CheckHardening(Process child)
    {
        var oom = ReadProc(child.Id, "oom_score_adj")?.Trim();
        var stat = ReadProc(child.Id, "stat");
        // Field 19 of /proc/<pid>/stat is the nice value; the command name (field 2) may hold spaces, so count after ')'.
        var nice = stat is null ? null : stat[(stat.LastIndexOf(')') + 2)..].Split(' ').ElementAtOrDefault(16);
        if (oom != "1000") log.LogWarning("graphify oom_score_adj is {Value}, expected 1000", oom ?? "unreadable");
        if (nice is null or "0") log.LogWarning("graphify nice is {Value}, expected below normal priority", nice ?? "unreadable");
        log.LogInformation("graphify oom_score_adj={Oom} nice={Nice}", oom, nice);
    }

    private static long? RssMiB(Process child)
    {
        var status = ReadProc(child.Id, "status");
        var line = status?.Split('\n').FirstOrDefault(l => l.StartsWith("VmRSS:", StringComparison.Ordinal));
        if (line is null) return null;
        var kb = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
        return long.TryParse(kb, out var k) ? k / 1024 : null;
    }

    private static string? ReadProc(int pid, string file)
    {
        try { return File.ReadAllText($"/proc/{pid}/{file}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private async Task StopChildAsync()
    {
        var child = _child;
        if (child is null) return;
        try
        {
            if (child.HasExited) return;
            // Process.Kill is SIGKILL; graphify gets SIGTERM first so uvicorn can shut down cleanly.
            _ = Kill(child.Id, 15);
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(_o.StopGraceSeconds));
            try { await child.WaitForExitAsync(grace.Token); }
            catch (OperationCanceledException)
            {
                log.LogWarning("graphify ignored SIGTERM for {Seconds} s; killing", _o.StopGraceSeconds);
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
