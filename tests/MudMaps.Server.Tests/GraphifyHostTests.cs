using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudMaps.Server.Research;

namespace MudMaps.Server.Tests;

public class GraphifyHostTests
{
    private static readonly string[] Allowed =
    [
        "GRAPHIFY_API_KEY", "GRAPHIFY_GRAPH", "GRAPHIFY_PORT", "GRAPHIFY_MAX_CONTEXTS", "GRAPHIFY_QUERY_LOG_DISABLE",
        "MALLOC_ARENA_MAX", "OPENBLAS_NUM_THREADS", "OMP_NUM_THREADS", "HOME", "TMPDIR", "PATH",
    ];

    [Fact]
    public async Task Child_environment_is_exactly_the_allowlist_with_a_fresh_key_and_isolated_python()
    {
        Environment.SetEnvironmentVariable("MUDMAPS_TEST_LEAK", "must-not-reach-the-child");
        await using var h = new ResearchHarness();
        await h.WaitReadyAsync();
        var start = h.Events("start").Single();
        var env = start["env"]!.AsObject();
        Assert.Equal(Allowed.Order(), env.Select(kv => kv.Key).Order());
        Assert.Equal(h.MergedGraph, env["GRAPHIFY_GRAPH"]!.GetValue<string>());
        Assert.Equal(h.Port.ToString(), env["GRAPHIFY_PORT"]!.GetValue<string>());
        Assert.Equal("3", env["GRAPHIFY_MAX_CONTEXTS"]!.GetValue<string>());
        Assert.Equal("1", env["GRAPHIFY_QUERY_LOG_DISABLE"]!.GetValue<string>());
        Assert.Equal("/usr/bin", env["PATH"]!.GetValue<string>());
        var key = env["GRAPHIFY_API_KEY"]!.GetValue<string>();
        Assert.Equal(43, key.Length); // 32 random bytes, base64url without padding
        Assert.Matches("^[A-Za-z0-9_-]+$", key);
        Assert.Equal(1, start["flags"]!.GetValue<int>()); // python -I
        var tmp = new DirectoryInfo(env["HOME"]!.GetValue<string>());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, tmp.UnixFileMode);

        // A crash gets a new child with a new key.
        h.Host.KillForTest();
        await ResearchHarness.WaitUntilAsync(() => h.Events("start").Count == 2 && h.Host.State == ResearchState.Ready, 20, "no restart");
        var second = h.Events("start")[1]["env"]!["GRAPHIFY_API_KEY"]!.GetValue<string>();
        Assert.NotEqual(key, second);
        Assert.Equal(second, h.Host.Key);
    }

    [Fact]
    public void Backoff_steps_are_1_2_4_8_16_then_capped_at_30()
    {
        var steps = new ResearchOptions().RestartBackoffSeconds;
        Assert.Equal([1, 2, 4, 8, 16, 30, 30, 30], Enumerable.Range(0, 8).Select(i => GraphifyHost.Backoff(steps, i).TotalSeconds));
    }

    [Fact]
    public async Task A_child_that_keeps_dying_is_restarted_with_growing_backoff()
    {
        await using var h = new ResearchHarness(childArgs: ["--exit-at-start"]);
        _ = h.Host;
        await ResearchHarness.WaitUntilAsync(() => h.Events("start").Count >= 4, 20, "fewer than 4 starts");
        var t = h.Events("start").Select(e => e["t"]!.GetValue<double>()).ToList();
        var gaps = t.Zip(t.Skip(1), (a, b) => b - a).Take(3).ToList();
        // Configured steps 0.2, 0.4, 0.8 s (plus the child's own start-up time).
        Assert.True(gaps[0] >= 0.2 && gaps[1] >= 0.4 && gaps[2] >= 0.8, string.Join(", ", gaps));
        Assert.True(gaps[2] > gaps[0] + 0.4, string.Join(", ", gaps));
        Assert.NotEqual(ResearchState.Ready, h.Host.State);
    }

    [Fact]
    public async Task Three_failed_probes_after_ready_restart_the_child()
    {
        // Answers the startup probe, then hangs on initialize: every later probe times out.
        await using var h = new ResearchHarness(childArgs: ["--initialize-ok", "1"]);
        await h.WaitReadyAsync();
        await ResearchHarness.WaitUntilAsync(() => h.Events("start").Count >= 2, 20, "never restarted");
        Assert.True(h.Logs.Has(LogLevel.Error, "failed 3 health probes in a row"), h.Logs.All);
    }

    [Fact]
    public async Task Warm_up_sends_graph_stats_and_one_small_query_before_ready_and_again_when_the_merged_graph_changes()
    {
        await using var h = new ResearchHarness();
        await h.WaitReadyAsync();
        var calls = h.ToolCalls();
        Assert.Equal(["graph_stats", "query_graph"], calls.Select(c => c["params"]!["name"]!.GetValue<string>()));
        Assert.Equal("""{"question":"mud","depth":1,"token_budget":100}""", calls[1]["params"]!["arguments"]!.ToJsonString());

        File.SetLastWriteTimeUtc(h.MergedGraph, DateTime.UtcNow.AddMinutes(1));
        await ResearchHarness.WaitUntilAsync(() => h.ToolCalls().Count == 4, 10, "no second warm-up");
        Assert.Equal("graph_stats", h.ToolCalls()[2]["params"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Probes_are_skipped_while_a_call_holds_the_upstream_permit()
    {
        await using var h = new ResearchHarness();
        await h.WaitReadyAsync();
        var gate = h.App.Services.GetRequiredService<UpstreamGate>();
        int Probes() => h.Events("request").Count(e => e["body"]!.GetValue<string>().Contains("\"initialize\""));

        using (await gate.AcquireAsync(CancellationToken.None))
        {
            await Task.Delay(400); // let a probe that was already in flight land
            var before = Probes();
            await Task.Delay(1500); // five probe intervals
            Assert.Equal(before, Probes());
        }
        var after = Probes();
        await ResearchHarness.WaitUntilAsync(() => Probes() > after, 5, "probes did not resume");
    }

    [Fact]
    public async Task Child_over_the_memory_limit_while_idle_is_recycled()
    {
        await using var h = new ResearchHarness(new() { ["Maps:Research:RssLimitMiB"] = "1" });
        await h.WaitReadyAsync();
        await ResearchHarness.WaitUntilAsync(() => h.Events("start").Count >= 2, 10, "not recycled");
        Assert.True(h.Logs.Has(LogLevel.Warning, "while idle; recycling"), h.Logs.All);
    }

    [Fact]
    public async Task Stop_sends_sigterm_and_the_child_is_gone()
    {
        var h = new ResearchHarness();
        await h.WaitReadyAsync();
        var pid = h.Events("start").Single()["pid"]!.GetValue<int>();
        await h.App.DisposeAsync();
        Assert.Single(h.Events("sigterm"));
        Assert.False(Directory.Exists($"/proc/{pid}"));
        await h.DisposeAsync();
    }

    [Fact]
    public async Task Startup_logs_landlock_line_and_checks_oom_score()
    {
        await using var h = new ResearchHarness();
        await h.WaitReadyAsync();
        await ResearchHarness.WaitUntilAsync(() => h.Logs.Has(LogLevel.Information, "graphify: landlock: unavailable"), 5, "no landlock line");
        Assert.True(h.Logs.Has(LogLevel.Information, "oom_score_adj=1000"), h.Logs.All);
        Assert.False(h.Logs.Has(LogLevel.Warning, "oom_score_adj is"), h.Logs.All);
        Assert.True(h.Logs.Has(LogLevel.Debug, "graphify: some stdout line"), h.Logs.All);

        // A child that does not raise its score inherits ours; only when ours is below 1000 can the warning show.
        var inherited = File.ReadAllText("/proc/self/oom_score_adj").Trim();
        await using var noOom = new ResearchHarness(childArgs: ["--no-oom"]);
        await noOom.WaitReadyAsync();
        Assert.True(noOom.Logs.Has(LogLevel.Information, $"oom_score_adj={inherited}"), noOom.Logs.All);
        Assert.Equal(inherited != "1000", noOom.Logs.Has(LogLevel.Warning, "expected 1000"));
    }

    [Fact]
    public async Task Disabled_starts_no_child_and_health_says_disabled()
    {
        await using var h = new ResearchHarness(new() { ["Maps:Research:Enabled"] = "false" });
        var health = await h.Client().GetFromJsonAsync<JsonObject>("/api/health");
        Assert.Equal("disabled", health!["research"]!.GetValue<string>());
        await Task.Delay(500);
        Assert.False(File.Exists(h.LogFile));
    }

    [Fact]
    public async Task Health_reports_ready_without_paths_or_pid()
    {
        await using var h = new ResearchHarness();
        await h.WaitReadyAsync();
        var body = await h.Client().GetStringAsync("/api/health");
        Assert.Contains("\"research\":\"ready\"", body);
        Assert.DoesNotContain(h.Dir, body);
        Assert.DoesNotContain(h.Events("start").Single()["pid"]!.ToJsonString(), body);
    }
}
