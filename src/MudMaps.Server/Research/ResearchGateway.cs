using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace MudMaps.Server.Research;

/// <summary>
/// The one front door to graphify, shared by <c>POST /mcp</c> (JSON-RPC for MCP clients) and <c>POST /api/research</c>
/// (the research panel): the same body checks, argument validator, limits, upstream gate, scrubbing and logging.
/// <c>initialize</c>, <c>ping</c>, <c>tools/list</c> and the PR tools are answered
/// here without touching graphify.
/// </summary>
public sealed class ResearchGateway(
    IOptions<ResearchOptions> research,
    ToolCatalog catalog,
    ResearchLimits limits,
    UpstreamGate gate,
    GraphifyUpstream upstream,
    IGraphifyHost host,
    RepoGuard guard,
    OutputScrubber scrubber,
    PrTools prs,
    LinkExtractor links,
    UsageCounters usage,
    ILogger<ResearchGateway> log)
{
    public const string ServerName = "mud-maps-graphify";
    public const string ServerVersion = "0.9.108";
    public static readonly string[] ProtocolVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private readonly ResearchOptions _o = research.Value;

    /// <summary>What a tool call came to: a result, graphify's JSON-RPC error, or an HTTP-level failure.</summary>
    public sealed record CallResult(int Status, JsonObject? Result = null, JsonObject? RpcError = null, string? Error = null, int RetryAfter = 0)
    {
        /// <summary>graphify's HTTP status, or 0 when the call never reached it.</summary>
        public int Upstream { get; init; }

        public static CallResult Fail(int status, string error, int retryAfter = 0) => new(status, Error: error, RetryAfter: retryAfter);
    }

    // ---------------- POST /mcp ----------------

    public async Task HandleMcpAsync(HttpContext ctx)
    {
        var key = ClientKeys.Research(ctx);
        if (Admit(key) is { } refused)
        {
            await WriteRpcFailure(ctx, null, refused);
            return;
        }
        using var inFlight = limits.Enter(key);
        if (inFlight is null)
        {
            await WriteRpcFailure(ctx, null, CallResult.Fail(429, "one call at a time per client", 1));
            return;
        }
        var (node, failure) = await ReadJsonAsync(ctx);
        if (failure is not null)
        {
            await WriteRpcFailure(ctx, null, failure);
            return;
        }
        if (node is JsonArray)
        {
            await WriteRpc(ctx, 400, Error(null, -32600, "batch requests are not supported"));
            return;
        }
        if (node is not JsonObject msg || msg["jsonrpc"]?.GetValueKind() != JsonValueKind.String || msg["jsonrpc"]!.GetValue<string>() != "2.0")
        {
            await WriteRpc(ctx, 400, Error(null, -32600, "expected a single JSON-RPC 2.0 request object"));
            return;
        }
        if (msg["method"] is not JsonValue mv || mv.GetValueKind() != JsonValueKind.String)
        {
            await WriteRpc(ctx, 400, Error(null, -32600, "method must be a string"));
            return;
        }
        var method = mv.GetValue<string>();
        var hasId = msg.TryGetPropertyValue("id", out var id);
        if (method == "notifications/initialized")
        {
            ctx.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }
        if (!hasId || !ValidId(id))
        {
            await WriteRpc(ctx, 400, Error(null, -32600, "id must be a string of at most 64 characters or an integer"));
            return;
        }
        var parameters = msg["params"];
        if (parameters is not null and not JsonObject)
        {
            await WriteRpc(ctx, 200, Error(id, -32602, "params must be an object"));
            return;
        }
        var p = parameters as JsonObject ?? [];
        switch (method)
        {
            case "initialize":
                var asked = p["protocolVersion"] is JsonValue pv && pv.GetValueKind() == JsonValueKind.String ? pv.GetValue<string>() : null;
                await WriteRpc(ctx, 200, Result(id, new JsonObject
                {
                    ["protocolVersion"] = asked is not null && ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[0],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = ServerVersion },
                }));
                return;
            case "ping":
                await WriteRpc(ctx, 200, Result(id, []));
                return;
            case "tools/list":
                await WriteRpc(ctx, 200, Result(id, (JsonObject)catalog.ToolsListResult.DeepClone()));
                return;
            case "tools/call":
                break;
            default:
                await WriteRpc(ctx, 200, Error(id, -32601, $"method not found: {Clip(method)}"));
                return;
        }

        if (p.Any(kv => kv.Key is not ("name" or "arguments" or "_meta")))
        {
            await WriteRpc(ctx, 200, Error(id, -32602, $"unknown parameter '{Clip(p.First(kv => kv.Key is not ("name" or "arguments" or "_meta")).Key)}'"));
            return;
        }
        if (p["name"] is not JsonValue nv || nv.GetValueKind() != JsonValueKind.String)
        {
            await WriteRpc(ctx, 200, Error(id, -32602, "params.name must be a string"));
            return;
        }
        var tool = nv.GetValue<string>();
        if (p["arguments"] is not null and not JsonObject)
        {
            await WriteRpc(ctx, 200, Error(id, -32602, "params.arguments must be an object"));
            return;
        }
        var args = p["arguments"] as JsonObject ?? [];
        if (catalog.Validate(tool, args) is { } invalid)
        {
            await WriteRpc(ctx, 200, Error(id, -32602, invalid));
            return;
        }
        var result = await CallToolAsync(key, id, tool, args, ctx.RequestAborted);
        if (result.Result is not null) await WriteRpc(ctx, 200, Result(id, result.Result));
        else if (result.RpcError is not null) await WriteRpc(ctx, 200, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = result.RpcError });
        else await WriteRpcFailure(ctx, id, result);
    }

    // ---------------- POST /api/research ----------------

    public async Task HandleResearchAsync(HttpContext ctx)
    {
        var key = ClientKeys.Research(ctx);
        if (Admit(key) is { } refused)
        {
            await WriteRest(ctx, refused);
            return;
        }
        using var inFlight = limits.Enter(key);
        if (inFlight is null)
        {
            await WriteRest(ctx, CallResult.Fail(429, "one call at a time per client", 1));
            return;
        }
        var (node, failure) = await ReadJsonAsync(ctx);
        if (failure is not null)
        {
            await WriteRest(ctx, failure);
            return;
        }
        if (node is not JsonObject body || body.Any(kv => kv.Key is not ("tool" or "arguments"))
            || body["tool"] is not JsonValue tv || tv.GetValueKind() != JsonValueKind.String
            || body["arguments"] is not (null or JsonObject))
        {
            await WriteRest(ctx, CallResult.Fail(400, "expected {\"tool\": \"...\", \"arguments\": {...}}"));
            return;
        }
        var tool = tv.GetValue<string>();
        var args = body["arguments"] as JsonObject ?? [];
        if (catalog.Validate(tool, args) is { } invalid)
        {
            await WriteRest(ctx, CallResult.Fail(400, invalid));
            return;
        }
        var result = await CallToolAsync(key, 1, tool, args, ctx.RequestAborted);
        if (result.RpcError is not null)
        {
            await WriteRest(ctx, CallResult.Fail(502, result.RpcError["message"]?.ToString() ?? "graphify error"));
            return;
        }
        if (result.Result is null)
        {
            await WriteRest(ctx, result);
            return;
        }
        var text = string.Join("\n", (result.Result["content"] as JsonArray ?? [])
            .OfType<JsonObject>().Where(c => c["type"]?.ToString() == "text").Select(c => c["text"]?.ToString() ?? ""));
        var scope = args["project_path"]?.GetValue<string>();
        var (found, rooms) = ToolCatalog.GraphTools.Contains(tool) ? links.Extract(text, scope) : ([], []);
        await WriteJson(ctx, 200, new JsonObject
        {
            ["tool"] = tool,
            ["text"] = text,
            ["isError"] = result.Result["isError"] is JsonValue e && e.GetValueKind() == JsonValueKind.True,
            ["links"] = found,
            ["rooms"] = rooms,
            ["mcp"] = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args.DeepClone() },
            },
        });
    }

    // ---------------- The shared call path ----------------

    /// <summary>Runs one validated tool call: PR tools from the cache, graph tools through the gate to graphify.</summary>
    public async Task<CallResult> CallToolAsync(string clientKey, JsonNode? id, string tool, JsonObject args, CancellationToken aborted)
    {
        var started = Stopwatch.GetTimestamp();
        // Counted before the work, so a call that fails upstream still shows as an attempt.
        usage.Increment("mcp", tool);
        var result = ToolCatalog.PrTools.Contains(tool) ? RunPrTool(clientKey, tool, args) : await RunGraphToolAsync(id, tool, args, aborted);
        // Never the arguments: a question is the caller's business (graphify's own query log is off as well).
        log.LogInformation("research {Tool} {Status} upstream {Upstream} in {Ms} ms client {Client}", tool,
            result.Status, result.Upstream, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds, ClientKeys.LogHash(clientKey));
        return result;
    }

    private CallResult RunPrTool(string clientKey, string tool, JsonObject args)
    {
        if (tool == "triage_prs" && limits.CountTriage(clientKey) is var wait and > 0)
            return CallResult.Fail(429, "triage_prs is limited to 2 calls per minute", wait);
        if (!scrubber.Clean(prs.Run(tool, args), out var text))
        {
            log.LogCritical("Security: PR tool output contained token-shaped text; answer withheld");
            return CallResult.Fail(502, "answer withheld");
        }
        return new(200, TextResult(text, false));
    }

    private async Task<CallResult> RunGraphToolAsync(JsonNode? id, string tool, JsonObject args, CancellationToken aborted)
    {
        var started = Stopwatch.GetTimestamp();
        var upstreamArgs = (JsonObject)args.DeepClone();
        if (args["project_path"]?.GetValue<string>() is { } repo)
        {
            if (!guard.Available(repo)) return new(200, RpcError: new JsonObject { ["code"] = -32602, ["message"] = "repo not available" });
            // graphify only ever gets a path built here, never the caller's value.
            upstreamArgs["project_path"] = guard.ProjectDir(repo);
        }
        if (host.State != ResearchState.Ready || host.Key is null)
            return CallResult.Fail(503, "research is starting, try again shortly", host.RetryAfterSeconds);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(_o.OverallTimeoutSeconds));
        using var queued = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, aborted);
        IDisposable? permit;
        try
        {
            permit = await gate.AcquireAsync(queued.Token);
        }
        catch (OperationCanceledException)
        {
            return deadline.IsCancellationRequested ? CallResult.Fail(504, "timed out waiting for graphify") : CallResult.Fail(499, "client went away");
        }
        if (permit is null) return CallResult.Fail(503, "research is busy, try again shortly", 5);
        UpstreamResult r;
        using (permit)
        {
            if (host.State != ResearchState.Ready || host.Key is not { } key)
                return CallResult.Fail(503, "research is starting, try again shortly", host.RetryAfterSeconds);
            var left = TimeSpan.FromSeconds(_o.OverallTimeoutSeconds) - Stopwatch.GetElapsedTime(started);
            var timeout = TimeSpan.FromSeconds(Math.Max(0.1, Math.Min(_o.UpstreamTimeoutSeconds, left.TotalSeconds)));
            // Not cancelled by the client going away: the permit is released only when graphify has answered.
            r = await upstream.PostAsync(host.Port, key, GraphifyUpstream.ToolCall(id, tool, upstreamArgs), timeout, CancellationToken.None);
        }
        return Interpret(r, tool) with { Upstream = r.Status };
    }

    private CallResult Interpret(UpstreamResult r, string tool)
    {
        switch (r.Outcome)
        {
            case UpstreamOutcome.Timeout:
                host.ReportUpstreamTimeout();
                return CallResult.Fail(504, "graphify did not answer in time");
            case UpstreamOutcome.TooLarge:
                return CallResult.Fail(502, "result too large, lower token_budget");
            case UpstreamOutcome.NotJson:
                return CallResult.Fail(502, "graphify returned an unexpected answer");
            case UpstreamOutcome.HttpError or UpstreamOutcome.Unreachable:
                return CallResult.Fail(502, "graphify is unavailable");
        }
        var body = r.Body!;
        if (!scrubber.Scrub(body))
        {
            log.LogCritical("Security: graphify output for {Tool} contained token-shaped text; answer withheld", tool);
            return CallResult.Fail(502, "answer withheld");
        }
        if (body["result"] is JsonObject result) return new(200, Result: (JsonObject)result.DeepClone());
        if (body["error"] is JsonObject error)
        {
            return new(200, RpcError: new JsonObject
            {
                ["code"] = error["code"] is JsonValue c && c.TryGetValue<int>(out var code) ? code : -32603,
                ["message"] = error["message"]?.DeepClone() ?? "graphify error",
                ["data"] = error["data"]?.DeepClone(),
            });
        }
        return CallResult.Fail(502, "graphify returned an unexpected answer");
    }

    // ---------------- Request and response plumbing ----------------

    private CallResult? Admit(string key) => limits.Count(key) switch
    {
        (ResearchLimits.Verdict.ClientLimited, var after) => CallResult.Fail(429, "too many research calls, at most 30 a minute", after),
        (ResearchLimits.Verdict.GlobalLimited, var after) => CallResult.Fail(503, "research is busy, try again shortly", after),
        _ => null,
    };

    /// <summary><c>application/json</c>, optionally with <c>charset=utf-8</c> and nothing else. Anything else is a 415:
    /// a browser sends <c>text/plain</c> or a form type cross-site without a preflight, so those must never count.
    /// Shared with <c>POST /api/track</c>.</summary>
    public static bool IsJsonContentType(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var type)
        && string.Equals(type.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
        && type.Parameters.All(p => p.Name.Equals("charset", StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase));

    private async Task<(JsonNode? Node, CallResult? Failure)> ReadJsonAsync(HttpContext ctx)
    {
        if (!IsJsonContentType(ctx.Request.ContentType))
        {
            return (null, CallResult.Fail(415, "Content-Type must be application/json"));
        }
        var max = _o.MaxRequestBytes;
        if (ctx.Request.ContentLength > max) return (null, CallResult.Fail(413, $"request body over {max} bytes"));
        byte[] bytes;
        try
        {
            var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(chunk, ctx.RequestAborted)) > 0)
            {
                if (buffer.Length + read > max) return (null, CallResult.Fail(413, $"request body over {max} bytes"));
                buffer.Write(chunk, 0, read);
            }
            bytes = buffer.ToArray();
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, CallResult.Fail(413, $"request body over {max} bytes"));
        }
        try
        {
            return (JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }), null);
        }
        catch (JsonException)
        {
            return (null, CallResult.Fail(400, "request body is not valid JSON"));
        }
    }

    private static bool ValidId(JsonNode? id) => id is JsonValue v && v.GetValueKind() switch
    {
        JsonValueKind.String => v.GetValue<string>().Length <= 64,
        JsonValueKind.Number => v.TryGetValue<long>(out _),
        _ => false,
    };

    private static JsonObject TextResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Result(JsonNode? id, JsonObject result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static Task WriteRpcFailure(HttpContext ctx, JsonNode? id, CallResult failure)
    {
        if (failure.RetryAfter > 0) ctx.Response.Headers.RetryAfter = failure.RetryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var code = failure.Status switch { 400 => -32700, 429 => -32029, 504 => -32004, _ => -32000 };
        return WriteRpc(ctx, failure.Status, Error(id, code, failure.Error ?? "error"));
    }

    private static Task WriteRpc(HttpContext ctx, int status, JsonObject body) => WriteJson(ctx, status, body);

    private static Task WriteRest(HttpContext ctx, CallResult failure)
    {
        if (failure.RetryAfter > 0) ctx.Response.Headers.RetryAfter = failure.RetryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return WriteJson(ctx, failure.Status, new JsonObject { ["error"] = failure.Error ?? "error" });
    }

    private static async Task WriteJson(HttpContext ctx, int status, JsonNode body)
    {
        if (ctx.RequestAborted.IsCancellationRequested) return;
        ctx.Response.StatusCode = status;
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.ContentType = "application/json";
        await ctx.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(body));
    }

    private static string Clip(string s) => new(s.Where(c => !char.IsControl(c)).Take(64).ToArray());
}
