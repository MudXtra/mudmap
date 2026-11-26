namespace MudMaps.Shared;

/// <summary>
/// The published "Connect via MCP" facts. The limit values are
/// the deployed defaults and match <c>ResearchOptions</c>; the page prefers the live <c>api/research/limits</c> response
/// and falls back to these, so the page cannot go blank when the service is unreachable. No internal paths or IPs here.
/// </summary>
public static class McpFacts
{
    /// <summary>The public MCP URLs. The first is the one the snippets use.</summary>
    public static readonly IReadOnlyList<string> Endpoints =
    [
        "https://mudmap.coderproject.org/mcp",
        "https://mudmap.arctechonline.tech/mcp",
    ];

    public const string PrimaryEndpoint = "https://mudmap.coderproject.org/mcp";
    public const string Server = "mud-maps-graphify";
    public const string Protocol = "2025-06-18";
    public const string Transport = "MCP over Streamable HTTP — POST; a GET on the same URL answers 405 Method Not Allowed";

    /// <summary>What a caller needs to sign in: nothing.</summary>
    public const string SignInNote = "None. No account, key or token.";

    /// <summary>What the server does with the caller's address (plain words, no header names). True to
    /// <c>ClientKeys</c>: the address is an in-memory limiter key, and the research log line carries only
    /// <c>ClientKeys.LogHash</c> (8 hex digits of an HMAC-SHA256 under a random per-process secret kept in memory only,
    /// so it cannot be reversed and changes on restart). Speaks only about this server's own log.</summary>
    public const string AddressNote =
        "Counted per network address (IPv6 per /64 network) so one caller cannot use up everyone's share. " +
        "This server holds the address in memory only for counting, never shows or saves it, and never uses it to decide access. " +
        "Its own log holds only a short keyed hash of the address, which cannot be turned back into the address and changes when the server restarts.";

    public static readonly IReadOnlyList<string> Tools =
    [
        "get_community", "get_neighbors", "get_node", "get_pr_impact", "god_nodes",
        "graph_stats", "list_prs", "query_graph", "shortest_path", "triage_prs",
    ];

    /// <summary>The repositories the published map covers.</summary>
    public static readonly IReadOnlyList<string> Repos =
    [
        "0phois/MudBlazor.StaticInput", "CodeBeamOrg/CodeBeam.MudBlazor.Extensions", "CodeBeamOrg/UltimateAuth",
        "MudBlazor/MudBlazor", "MudBlazor/MudBlazor.Icons", "MudBlazor/Templates", "MudBlazor/ThemeManager",
        "MudBlazor/TryMudBlazor", "MudXtra/LottiePlayer", "MudXtra/MudX", "MudXtra/MudXThemeCreator",
        "MudXtra/MudXtra.CodeAnalysis", "danheron/Heron.MudCalendar", "fgilde/MudBlazor.Extensions",
        "fgilde/MudExRichTextEditor",
    ];

    /// <summary>The published limits as label, value and the setting they come from.</summary>
    public static readonly IReadOnlyList<(string Label, string Value, string Source)> Limits =
    [
        ("Calls per client", "30 / minute (fixed window, no queue)", "ClientCallsPerMinute"),
        ("Concurrent calls per client", "1", "ClientConcurrency"),
        ("Global calls", "300 / minute", "GlobalCallsPerMinute"),
        ("triage_prs", "2 / minute", "TriageCallsPerMinute"),
        ("Pull-request reads", "60 / minute", "PrReadsPerMinute"),
        ("Upstream queue", "8", "UpstreamQueue"),
        ("Upstream timeout", "20 s", "UpstreamTimeoutSeconds"),
        ("Overall timeout", "30 s", "OverallTimeoutSeconds"),
        ("Max request size", "16 KiB", "MaxRequestBytes"),
        ("Max response size", "256 KiB", "MaxResponseBytes"),
        ("Graphs loaded at once", "3", "MaxContexts"),
        ("Interpreter memory cap", "1.8 GiB", "RssLimitMiB"),
    ];

    /// <summary>The one client snippet shown: Claude Code's documented form for a remote HTTP server
    /// (https://code.claude.com/docs/en/mcp). Other clients' config shapes were not verifiable against their docs, so
    /// none is published.</summary>
    public const string ClaudeCli = "claude mcp add --transport http mud-maps https://mudmap.coderproject.org/mcp";

    /// <summary>What the server says when an answer does not fit the token budget.</summary>
    public const string TruncationNote =
        "Long answers are truncated to a token budget and say so, for example: TRUNCATED: showing 15 of 254 lines (~600-token budget).";

    /// <summary>What the server returns when a limit is passed.</summary>
    public const string RateLimitNote =
        "Over any limit the answer is 429 with a Retry-After header, for example: triage_prs is limited to 2 calls per minute.";
}

/// <summary>The live limits served by <c>api/research/limits</c>, used instead of the published defaults when present.</summary>
public sealed record ResearchLimits(
    int ClientCallsPerMinute,
    int ClientConcurrency,
    int GlobalCallsPerMinute,
    int UpstreamQueue,
    int TriageCallsPerMinute,
    int PrReadsPerMinute,
    double UpstreamTimeoutSeconds,
    double OverallTimeoutSeconds,
    int MaxRequestBytes,
    int MaxResponseBytes,
    int MaxContexts,
    long RssLimitMiB,
    string? ServerVersion = null);
