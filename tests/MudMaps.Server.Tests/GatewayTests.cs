using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MudMaps.Server.Research;

namespace MudMaps.Server.Tests;

public class GatewayTests
{
    internal static async Task<(HttpStatusCode Status, JsonObject? Body, HttpResponseMessage Response)> Post(
        HttpClient client, string path, string json, string contentType = "application/json", Action<HttpRequestMessage>? edit = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        edit?.Invoke(request);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) as JsonObject : null, response);
    }

    internal static string Call(string tool, string args, string id = "1") =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + (id.All(char.IsDigit) ? id : $"\"{id}\"")
        + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + args + "}}";

    private static string Text(JsonObject body) => body["result"]!["content"]![0]!["text"]!.GetValue<string>();

    private static int? ErrorCode(JsonObject? body) => body?["error"]?["code"]?.GetValue<int>();

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("application/json; charset=latin1")]
    public async Task Non_json_content_type_is_415(string type)
    {
        await using var h = new ResearchHarness();
        var (status, _, _) = await Post(h.Client(), "/mcp", """{"jsonrpc":"2.0","id":1,"method":"ping"}""", type);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, status);
        var rest = await Post(h.Client(), "/api/research", """{"tool":"graph_stats"}""", type);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, rest.Status);
        Assert.Equal("Content-Type must be application/json", rest.Body!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Body_up_to_16_KiB_is_read_and_one_byte_more_is_413()
    {
        await using var h = new ResearchHarness();
        var head = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";
        var fits = head[..^1] + new string(' ', 16 * 1024 - head.Length) + "}";
        Assert.Equal(16 * 1024, Encoding.UTF8.GetByteCount(fits));
        var ok = await Post(h.Client(), "/mcp", fits, "application/json; charset=utf-8");
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        var over = await Post(h.Client(), "/mcp", fits[..^1] + " }");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.Status);
    }

    [Theory]
    [InlineData("""[{"jsonrpc":"2.0","id":1,"method":"ping"}]""", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":{"a":1},"method":"ping"}""", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1.5,"method":"ping"}""", -32600)]
    [InlineData("""{"jsonrpc":"2.0","method":"ping"}""", -32600)]
    [InlineData("""{"jsonrpc":"1.0","id":1,"method":"ping"}""", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"resources/list"}""", -32601)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"prompts/get"}""", -32601)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"run_shell","arguments":{}}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_node","arguments":{"label":"a","path":"/"}}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"query_graph","arguments":{"question":"a","token_budget":9000}}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_node","arguments":{"label":"a"},"cursor":"x"}}""", -32602)]
    public async Task Malformed_or_disallowed_requests_get_json_rpc_errors_and_never_reach_graphify(string json, int code)
    {
        await using var h = new ResearchHarness();
        await h.WaitReadyAsync();
        var before = h.ToolCalls().Count;
        var (_, body, _) = await Post(h.Client(), "/mcp", json);
        Assert.Equal(code, ErrorCode(body));
        Assert.Equal(before, h.ToolCalls().Count);
    }

    [Fact]
    public async Task Id_longer_than_64_characters_is_rejected()
    {
        await using var h = new ResearchHarness();
        var (_, body, _) = await Post(h.Client(), "/mcp", $$"""{"jsonrpc":"2.0","id":"{{new string('i', 65)}}","method":"ping"}""");
        Assert.Equal(-32600, ErrorCode(body));
        var (_, ok, _) = await Post(h.Client(), "/mcp", $$"""{"jsonrpc":"2.0","id":"{{new string('i', 64)}}","method":"ping"}""");
        Assert.NotNull(ok!["result"]);
    }

    [Fact]
    public async Task Initialize_ping_and_tools_list_are_answered_in_dotnet()
    {
        await using var h = new ResearchHarness();
        await h.WaitReadyAsync();
        int NonProbe() => h.Events("request").Count(e => !e["body"]!.GetValue<string>().Contains("\"id\":\"probe\""));
        var before = NonProbe();
        var client = h.Client();
        var (_, init, response) = await Post(client, "/mcp", """{"jsonrpc":"2.0","id":"a","method":"initialize","params":{"protocolVersion":"2025-03-26"}}""");
        Assert.Equal("2025-03-26", init!["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("""{"name":"mud-maps-graphify","version":"0.9.108"}""", init["result"]!["serverInfo"]!.ToJsonString());
        Assert.Equal("""{"tools":{}}""", init["result"]!["capabilities"]!.ToJsonString());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var (_, future, _) = await Post(client, "/mcp", """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"2099-01-01"}}""");
        Assert.Equal("2025-06-18", future!["result"]!["protocolVersion"]!.GetValue<string>());
        var (_, ping, _) = await Post(client, "/mcp", """{"jsonrpc":"2.0","id":3,"method":"ping"}""");
        Assert.Equal("{}", ping!["result"]!.ToJsonString());
        var (_, list, _) = await Post(client, "/mcp", """{"jsonrpc":"2.0","id":4,"method":"tools/list"}""");
        var tools = list!["result"]!["tools"]!.AsArray();
        Assert.Equal(10, tools.Count);
        Assert.Equal(ResearchHarness.Repos, tools[0]!["inputSchema"]!["properties"]!["project_path"]!["enum"]!.AsArray().Select(e => e!.GetValue<string>()));
        var (status, _, _) = await Post(client, "/mcp", """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(before, NonProbe()); // graphify heard none of it
    }

    [Fact]
    public async Task Upstream_request_is_canonical_with_fixed_headers_and_no_client_header_gets_through()
    {
        await using var h = new ResearchHarness();
        await h.WaitReadyAsync();
        var json = """{"jsonrpc":"2.0","id":"abc","method":"tools/call","extra":"x","params":{"name":"get_node","arguments":{"label":"Foo"},"_meta":{"progressToken":7}}}""";
        var (status, body, _) = await Post(h.Client("203.0.113.5"), "/mcp", json, edit: r =>
        {
            r.Headers.Add("Mcp-Session-Id", "client-session");
            r.Headers.Add("Authorization", "Bearer client-secret");
            r.Headers.Add("X-API-Key", "client-key");
            r.Headers.Add("Cookie", "a=b");
            r.Headers.Add("X-Forwarded-For", "198.51.100.1");
            r.Headers.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
            r.Headers.Add("baggage", "user=evil");
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("abc", body!["id"]!.GetValue<string>());
        var seen = h.Events("request").Last();
        Assert.Equal("""{"jsonrpc":"2.0","id":"abc","method":"tools/call","params":{"name":"get_node","arguments":{"label":"Foo"}}}""", seen["body"]!.GetValue<string>());
        var headers = seen["headers"]!.AsObject().ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value!.GetValue<string>());
        Assert.Equal($"127.0.0.1:{h.Port}", headers["host"]);
        Assert.Equal("application/json", headers["content-type"]);
        Assert.Equal("application/json, text/event-stream", headers["accept"]);
        Assert.Equal("2025-06-18", headers["mcp-protocol-version"]);
        Assert.Equal(h.Host.Key, headers["x-api-key"]);
        foreach (var name in new[] { "mcp-session-id", "authorization", "cookie", "x-forwarded-for", "cf-connecting-ip" }) Assert.False(headers.ContainsKey(name), name);
        var extra = headers.Keys.Except(["host", "content-type", "accept", "mcp-protocol-version", "x-api-key", "content-length"]).ToList();
        Assert.True(extra.Count == 0, string.Join(", ", extra));
    }

    [Fact]
    public async Task Project_path_must_be_configured_and_is_rewritten_to_the_clone_directory()
    {
        await using var h = new ResearchHarness();
        await h.WaitReadyAsync();
        var (_, body, _) = await Post(h.Client(), "/mcp", Call("graph_stats", """{"project_path":"MudXtra/MudX"}"""));
        Assert.Equal(Path.Combine(h.ReposRoot, "MudXtra/MudX"), h.ToolCalls().Last()["params"]!["arguments"]!["project_path"]!.GetValue<string>());
        Assert.StartsWith("Graph: MudXtra/MudX (5 nodes)", Text(body!));
        Assert.DoesNotContain(h.Dir, body!.ToJsonString());

        var (_, outside, _) = await Post(h.Client(), "/mcp", Call("graph_stats", $$"""{"project_path":"{{h.ReposRoot}}/MudXtra/MudX"}"""));
        Assert.Equal(-32602, ErrorCode(outside));
    }

    [Theory]
    [InlineData("graphify-out")]
    [InlineData("graphify-out/graph.json")]
    [InlineData("repo")]
    public async Task Symlinked_graph_or_clone_is_refused(string which)
    {
        await using var h = new ResearchHarness();
        await h.WaitReadyAsync();
        var repo = Path.Combine(h.ReposRoot, "MudXtra/MudX");
        var elsewhere = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(elsewhere, "graphify-out"));
        File.WriteAllText(Path.Combine(elsewhere, "graphify-out", "graph.json"), "{}");
        var target = which == "repo" ? repo : Path.Combine(repo, which);
        if (which.EndsWith(".json")) File.Delete(target); else Directory.Delete(target, recursive: true);
        var source = which == "repo" ? elsewhere : Path.Combine(elsewhere, which);
        if (which.EndsWith(".json")) File.CreateSymbolicLink(target, source); else Directory.CreateSymbolicLink(target, source);

        var before = h.ToolCalls().Count;
        var (_, body, _) = await Post(h.Client(), "/mcp", Call("graph_stats", """{"project_path":"MudXtra/MudX"}"""));
        Assert.Equal(-32602, ErrorCode(body));
        Assert.Equal("repo not available", body!["error"]!["message"]!.GetValue<string>());
        Assert.Equal(before, h.ToolCalls().Count);
        Assert.True(h.Logs.Has(LogLevel.Warning, "Research refuses repository MudXtra/MudX"), h.Logs.All);
        // The other repository is unaffected.
        var (_, ok, _) = await Post(h.Client(), "/mcp", Call("graph_stats", """{"project_path":"MudBlazor/MudBlazor"}"""));
        Assert.NotNull(ok!["result"]);
    }

    [Fact]
    public async Task Every_string_is_scrubbed_including_tool_errors_and_json_rpc_errors()
    {
        await using var h = new ResearchHarness();
        await h.WaitReadyAsync();
        var (_, paths, _) = await Post(h.Client(), "/mcp", Call("query_graph", """{"question":"paths"}"""));
        var text = Text(paths!);
        Assert.StartsWith("Graph: mud (merged) (7 nodes)", text);
        Assert.True(paths!["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("NODE Foo [src=src/A.cs", text);
        var (_, error, _) = await Post(h.Client(), "/mcp", Call("query_graph", """{"question":"errpath"}"""));
        Assert.Equal(-32603, ErrorCode(error));
        Assert.Equal("graph.json not found: <path>", error!["error"]!["message"]!.GetValue<string>());
        Assert.Equal("<path>", error["error"]!["data"]!["where"]!.GetValue<string>());
        foreach (var body in new[] { paths, error })
        {
            Assert.DoesNotMatch("/data/|/mnt/|/home/|/root/|/opt/|/tmp/|InternalRepos", body.ToJsonString());
            Assert.DoesNotContain(h.Dir, body.ToJsonString());
        }
    }

    [Theory]
    [InlineData("token", "answer withheld")]
    [InlineData("nonjson", "graphify returned an unexpected answer")]
    [InlineData("sse", "graphify returned an unexpected answer")]
    [InlineData("big", "result too large, lower token_budget")]
    public async Task Bad_upstream_answers_become_502(string behaviour, string message)
    {
        await using var h = new ResearchHarness();
        await h.WaitReadyAsync();
        var (status, body, _) = await Post(h.Client(), "/mcp", Call("query_graph", $$"""{"question":"{{behaviour}}"}"""));
        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Equal(message, body!["error"]!["message"]!.GetValue<string>());
        Assert.DoesNotContain("ghp_", body.ToJsonString());
        if (behaviour == "token") Assert.True(h.Logs.Has(LogLevel.Critical, "Security: graphify output"), h.Logs.All);
        Assert.DoesNotContain(h.Logs.Entries, e => e.Message.Contains("ghp_"));
    }

    [Fact]
    public async Task Per_client_window_counts_every_method_on_both_endpoints_then_429()
    {
        await using var h = new ResearchHarness(new() { ["Maps:Research:ClientCallsPerMinute"] = "3" });
        var client = h.Client("203.0.113.7");
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "/mcp", """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "/mcp", """{"jsonrpc":"2.0","id":1,"method":"ping"}""")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, "/api/research", """{"tool":"nope"}""")).Status);
        var (status, _, response) = await Post(client, "/mcp", """{"jsonrpc":"2.0","id":1,"method":"ping"}""");
        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.OK, (await Post(h.Client("203.0.113.8"), "/mcp", """{"jsonrpc":"2.0","id":1,"method":"ping"}""")).Status);
    }

    [Fact]
    public async Task Ipv6_clients_in_one_64_share_a_bucket()
    {
        await using var h = new ResearchHarness(new() { ["Maps:Research:ClientCallsPerMinute"] = "2" });
        var ping = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";
        Assert.Equal(HttpStatusCode.OK, (await Post(h.Client("2001:db8:1:2::1"), "/mcp", ping)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Post(h.Client("2001:db8:1:2:ffff::2"), "/mcp", ping)).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Post(h.Client("2001:db8:1:2:abcd:ef01:2345:6789"), "/mcp", ping)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Post(h.Client("2001:db8:1:3::1"), "/mcp", ping)).Status);
    }

    [Fact]
    public async Task Global_window_caps_all_clients_together_with_503()
    {
        await using var h = new ResearchHarness(new() { ["Maps:Research:GlobalCallsPerMinute"] = "2" });
        var ping = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";
        Assert.Equal(HttpStatusCode.OK, (await Post(h.Client("198.51.100.1"), "/mcp", ping)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Post(h.Client("198.51.100.2"), "/mcp", ping)).Status);
        var (status, _, response) = await Post(h.Client("198.51.100.3"), "/mcp", ping);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    [Fact]
    public async Task A_second_concurrent_call_from_one_client_is_429_while_another_client_is_served()
    {
        await using var h = new ResearchHarness();
        await h.WaitReadyAsync();
        var slow = Post(h.Client("203.0.113.1"), "/mcp", Call("query_graph", """{"question":"sleep:1.5"}"""));
        await ResearchHarness.WaitUntilAsync(() => h.ToolCalls().Any(c => c.ToJsonString().Contains("sleep:1.5")), 5, "slow call never arrived");
        var (second, _, response) = await Post(h.Client("203.0.113.1"), "/mcp", Call("graph_stats", "{}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, second);
        Assert.NotNull(response.Headers.RetryAfter);
        var other = await Post(h.Client("203.0.113.2"), "/mcp", Call("graph_stats", "{}"));
        Assert.Equal(HttpStatusCode.OK, other.Status); // queued behind the slow call, then served
        Assert.Equal(HttpStatusCode.OK, (await slow).Status);
    }

    [Fact]
    public async Task Queue_overflow_is_503_with_retry_after_5()
    {
        await using var h = new ResearchHarness(new() { ["Maps:Research:UpstreamQueue"] = "1" });
        await h.WaitReadyAsync();
        var first = Post(h.Client("203.0.113.1"), "/mcp", Call("query_graph", """{"question":"sleep:1.5"}"""));
        await ResearchHarness.WaitUntilAsync(() => h.ToolCalls().Any(c => c.ToJsonString().Contains("sleep:1.5")), 5, "slow call never arrived");
        var queued = Post(h.Client("203.0.113.2"), "/mcp", Call("graph_stats", "{}"));
        await Task.Delay(200);
        var (status, body, response) = await Post(h.Client("203.0.113.3"), "/mcp", Call("graph_stats", "{}"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter!.Delta);
        Assert.Equal("research is busy, try again shortly", body!["error"]!["message"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await first).Status);
        Assert.Equal(HttpStatusCode.OK, (await queued).Status);
    }

    [Fact]
    public async Task Timeout_is_504_and_the_gate_stays_closed_until_a_probe_succeeds()
    {
        await using var h = new ResearchHarness(new() { ["Maps:Research:UpstreamTimeoutSeconds"] = "1", ["Maps:Research:UnresponsiveRestartSeconds"] = "30" });
        await h.WaitReadyAsync();
        var (status, _, _) = await Post(h.Client("203.0.113.1"), "/mcp", Call("query_graph", """{"question":"sleep:2.5"}"""));
        Assert.Equal(HttpStatusCode.GatewayTimeout, status);
        Assert.Equal(ResearchState.Starting, h.Host.State);
        var (closed, _, response) = await Post(h.Client("203.0.113.2"), "/mcp", Call("graph_stats", "{}"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, closed);
        Assert.NotNull(response.Headers.RetryAfter);
        await h.WaitReadyAsync(10); // the fake finishes its sleep, the next probe answers, the gate reopens
        Assert.Single(h.Events("start")); // reopened, not restarted
        Assert.Equal(HttpStatusCode.OK, (await Post(h.Client("203.0.113.3"), "/mcp", Call("graph_stats", "{}"))).Status);
    }

    [Fact]
    public async Task Child_still_unresponsive_after_a_timeout_is_restarted()
    {
        await using var h = new ResearchHarness(new() { ["Maps:Research:UpstreamTimeoutSeconds"] = "1", ["Maps:Research:UnresponsiveRestartSeconds"] = "2" });
        await h.WaitReadyAsync();
        var (status, _, _) = await Post(h.Client(), "/mcp", Call("query_graph", """{"question":"sleep:60"}"""));
        Assert.Equal(HttpStatusCode.GatewayTimeout, status);
        await ResearchHarness.WaitUntilAsync(() => h.Events("start").Count == 2, 15, "not restarted");
        await h.WaitReadyAsync();
        Assert.True(h.Logs.Has(LogLevel.Error, "unresponsive 2 s after a timed-out call"), h.Logs.All);
    }

    [Fact]
    public async Task Child_not_ready_is_503_at_once_without_reaching_it()
    {
        await using var h = new ResearchHarness(childArgs: ["--hang-initialize"]);
        _ = h.Host; // starts the app
        await ResearchHarness.WaitUntilAsync(() => h.Events("start").Count == 1, 10, "child never started");
        var (status, body, response) = await Post(h.Client(), "/mcp", Call("graph_stats", "{}"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Equal("research is starting, try again shortly", body!["error"]!["message"]!.GetValue<string>());
        Assert.Empty(h.ToolCalls());
        // tools/list does not need graphify at all.
        Assert.Equal(HttpStatusCode.OK, (await Post(h.Client("203.0.113.9"), "/mcp", """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")).Status);
    }

    [Fact]
    public async Task Get_and_delete_on_mcp_are_405()
    {
        await using var h = new ResearchHarness();
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await h.Client().GetAsync("/mcp")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await h.Client().DeleteAsync("/mcp")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await h.Client().PutAsync("/api/research", new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await h.Client().PostAsync("/api/map", new StringContent("{}"))).StatusCode);
    }

    [Fact]
    public async Task Disabled_research_is_404_everywhere_and_starts_nothing()
    {
        await using var h = new ResearchHarness(new() { ["Maps:Research:Enabled"] = "false" });
        Assert.Equal(HttpStatusCode.NotFound, (await Post(h.Client(), "/mcp", """{"jsonrpc":"2.0","id":1,"method":"ping"}""")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(h.Client(), "/api/research", """{"tool":"graph_stats"}""")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client().GetAsync("/api/research/prs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client().GetAsync("/mcp")).StatusCode);
        Assert.Equal(ResearchState.Disabled, h.Host.State);
        await Task.Delay(300);
        Assert.False(File.Exists(h.LogFile));
    }

    [Fact]
    public async Task Kill_hook_exists_only_in_development()
    {
        await using var prod = new ResearchHarness(environment: "Production");
        await prod.WaitReadyAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await prod.Client().GetAsync("/api/research/_dev/kill")).StatusCode);
        Assert.Single(prod.Events("start"));

        await using var dev = new ResearchHarness();
        await dev.WaitReadyAsync();
        Assert.Equal(HttpStatusCode.OK, (await dev.Client().GetAsync("/api/research/_dev/kill")).StatusCode);
        await ResearchHarness.WaitUntilAsync(() => dev.Events("start").Count == 2, 10, "kill hook did not restart the child");
    }

    [Fact]
    public async Task Research_rest_returns_text_links_and_the_equivalent_mcp_call()
    {
        await using var h = new ResearchHarness();
        await h.WaitReadyAsync();
        var (status, body, response) = await Post(h.Client(), "/api/research", """{"tool":"get_node","arguments":{"label":"Foo","project_path":"MudXtra/MudX"}}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("get_node", body!["tool"]!.GetValue<string>());
        Assert.StartsWith("Graph: MudXtra/MudX (5 nodes) | tool=get_node", body["text"]!.GetValue<string>());
        Assert.False(body["isError"]!.GetValue<bool>());
        var link = body["links"]!.AsArray().Single()!;
        Assert.Equal("MudXtra/MudX", link["repo"]!.GetValue<string>());
        Assert.Equal("src/A.cs", link["file"]!.GetValue<string>());
        Assert.Equal(3, link["line"]!.GetValue<int>());
        Assert.Equal("https://github.com/MudXtra/MudX/blob/HEAD/src/A.cs#L3", link["github"]!.GetValue<string>());
        Assert.Empty(body["rooms"]!.AsArray()); // no graph loaded for MudX in this test
        Assert.Equal("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_node","arguments":{"label":"Foo","project_path":"MudXtra/MudX"}}}""",
            body["mcp"]!.ToJsonString());
    }

    [Theory]
    [InlineData("""{"tool":"query_graph","arguments":{"question":"x","depth":9}}""", "depth must be between 1 and 6")]
    [InlineData("""{"tool":"query_graph","arguments":{"question":"x"},"extra":1}""", "expected {\"tool\": \"...\", \"arguments\": {...}}")]
    [InlineData("""{"tool":"run_shell"}""", "unknown tool 'run_shell'")]
    public async Task Research_rest_validation_errors_are_400_with_the_validator_message(string json, string error)
    {
        await using var h = new ResearchHarness();
        var (status, body, _) = await Post(h.Client(), "/api/research", json);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(error, body!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_limits_endpoint_names_the_research_server_version_the_Connect_page_prints()
    {
        // The page used to print /api/health's map build version as the MCP server's version (CB review item 3).
        await using var h = new ResearchHarness();
        var body = JsonNode.Parse(await h.Client().GetStringAsync("/api/research/limits"))!;
        Assert.Equal(ResearchGateway.ServerVersion, body["serverVersion"]?.GetValue<string>());
    }
}
