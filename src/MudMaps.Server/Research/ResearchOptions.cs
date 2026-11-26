namespace MudMaps.Server.Research;

/// <summary>
/// Configuration for the research endpoints (<c>POST /mcp</c>, <c>/api/research*</c>) and the graphify child process
/// (<c>Maps:Research</c>). Off by default: nothing starts and the endpoints answer 404 until it is enabled.
/// </summary>
public sealed class ResearchOptions
{
    public const string Section = "Maps:Research";

    public bool Enabled { get; set; }

    /// <summary>Interpreter for the child, started as <c>{Python} -I -B {Launcher}</c>.</summary>
    public string Python { get; set; } = "/opt/graphify/venv/bin/python";

    public string Launcher { get; set; } = "/opt/graphify/run.py";

    /// <summary>Extra arguments after the launcher. Empty in production; the tests use it to steer the fake child.</summary>
    public List<string> ChildArgs { get; set; } = [];

    /// <summary>Loopback port graphify listens on (127.0.0.1 only).</summary>
    public int Port { get; set; } = 8841;

    /// <summary>The merged graph the child serves when a call names no repository.</summary>
    public string MergedGraph { get; set; } = "";

    public int MaxContexts { get; set; } = 3;

    /// <summary>The child's HOME and TMPDIR, created 0700.</summary>
    public string TempDir { get; set; } = "/tmp/graphify";

    /// <summary>The child's PATH, the only search path it gets.</summary>
    public string ChildPath { get; set; } = "/opt/graphify/venv/bin";

    // Limits. All are per research client key unless named global.
    public int ClientCallsPerMinute { get; set; } = 30;
    public int ClientConcurrency { get; set; } = 1;
    public int GlobalCallsPerMinute { get; set; } = 300;
    public int UpstreamQueue { get; set; } = 8;
    public int TriageCallsPerMinute { get; set; } = 2;
    public int PrReadsPerMinute { get; set; } = 60;
    public double UpstreamTimeoutSeconds { get; set; } = 20;
    public double OverallTimeoutSeconds { get; set; } = 30;
    public int MaxRequestBytes { get; set; } = 16 * 1024;
    public int MaxResponseBytes { get; set; } = 256 * 1024;

    // Supervision. Intervals are settings so the tests can run them fast.
    public double StartupProbeIntervalSeconds { get; set; } = 1;
    public double StartupTimeoutSeconds { get; set; } = 60;
    public double ProbeIntervalSeconds { get; set; } = 30;
    public double ProbeTimeoutSeconds { get; set; } = 25;
    public int FailedProbesBeforeRestart { get; set; } = 3;
    /// <summary>After a 504 the gate stays closed; a child still unresponsive this long afterwards is restarted.</summary>
    public double UnresponsiveRestartSeconds { get; set; } = 30;
    /// <summary>The warm-up calls' timeout; a cold merged query takes about 18 s.</summary>
    public double WarmTimeoutSeconds { get; set; } = 60;
    public List<double> RestartBackoffSeconds { get; set; } = [1, 2, 4, 8, 16, 30];
    public double BackoffResetSeconds { get; set; } = 300;
    public double StopGraceSeconds { get; set; } = 5;
    public long RssLimitMiB { get; set; } = 1843; // 1.8 GiB
}
