using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MudMaps.Server.Maps;

namespace MudMaps.Server.Tests;

public class StatusTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static DirectoryInfo Root(string? lastRun = null)
    {
        var root = Directory.CreateTempSubdirectory();
        if (lastRun is not null)
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "_status"));
            File.WriteAllText(Path.Combine(root.FullName, "_status", "last-run.json"), lastRun);
        }
        return root;
    }

    private static string Run(DateTimeOffset ended, int rc = 0) =>
        $$"""{ "started": "{{ended.AddMinutes(-2):o}}", "repos": [{ "repo": "a/b", "rc": 1, "ended": "2001-01-01T00:00:00+00:00" }], "ended": "{{ended:o}}", "rc": {{rc}} }""";

    private static StatusReport Report(string root, MapState state) =>
        new(Options.Create(new MapsOptions { ReposRoot = root, Repos = [new("a/b", "B", 1)] }), state, new FixedClock(Now));

    private WebApplicationFactory<Program> App(string root) => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("Maps:ReposRoot", root);
        b.UseSetting("Maps:Sites:colony", "/nonexistent");
        b.ConfigureServices(s => s.Remove(s.Single(d => d.ImplementationType == typeof(MapRefreshService))));
    });

    private static MapDto Map(DateTimeOffset generated, bool prsLive = false) =>
        MapComposer.Compose([], new Dictionary<string, RepoActivity>(), 30, generated, prsLive);

    [Fact]
    public async Task Status_endpoint_has_the_documented_shape_and_is_not_cached()
    {
        var root = Root(Run(DateTimeOffset.UtcNow.AddMinutes(-20)));
        var app = App(root.FullName);
        var state = app.Services.GetRequiredService<MapState>();
        state.Publish(Map(DateTimeOffset.UnixEpoch), DateTimeOffset.UnixEpoch);
        state.ActivityRefreshedAt = DateTimeOffset.UtcNow.AddMinutes(-3);

        var response = await app.CreateClient().GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-cache", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var names = json.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["checkedAt", "mapGeneratedAt", "prsLive"], names);
        Assert.True(json.RootElement.GetProperty("prsLive").GetBoolean());
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("checkedAt").ValueKind);
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("mapGeneratedAt").ValueKind);
    }

    [Fact]
    public async Task Status_is_available_before_the_first_map_is_built()
    {
        var response = await App(Root().FullName).CreateClient().GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("mapGeneratedAt").ValueKind);
        Assert.False(json.RootElement.GetProperty("prsLive").GetBoolean());
    }

    [Fact]
    public void CheckedAt_is_the_older_of_the_pr_refresh_and_the_repo_refresh()
    {
        var root = Root(Run(Now.AddMinutes(-40)));
        var state = new MapState { ActivityRefreshedAt = Now.AddMinutes(-5) };
        Assert.Equal(Now.AddMinutes(-40), Report(root.FullName, state).Build().CheckedAt);

        state.ActivityRefreshedAt = Now.AddMinutes(-90);
        Assert.Equal(Now.AddMinutes(-90), Report(root.FullName, state).Build().CheckedAt);
    }

    [Fact]
    public void Without_live_prs_only_the_repo_refresh_counts()
    {
        var root = Root(Run(Now.AddMinutes(-40)));
        var status = Report(root.FullName, new MapState()).Build();
        Assert.Equal(Now.AddMinutes(-40), status.CheckedAt);
        Assert.False(status.PrsLive);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "ended": "2026-01-15T11:00:00+00:00" }""")]
    [InlineData("""{ "ended": "2026-01-15T11:00:00+00:00", "rc": 1 }""")]
    [InlineData("""{ "ended": "garbage", "rc": 0 }""")]
    public void Missing_unreadable_or_failed_run_record_falls_back_to_the_newest_graph_file(string? lastRun)
    {
        var root = Root(lastRun);
        var graph = Path.Combine(root.FullName, "a", "b", "graphify-out", "graph.json");
        Directory.CreateDirectory(Path.GetDirectoryName(graph)!);
        File.WriteAllText(graph, "{}");
        var written = Now.AddHours(-7);
        File.SetLastWriteTimeUtc(graph, written.UtcDateTime);

        var status = Report(root.FullName, new MapState()).Build();

        Assert.Equal(written, status.CheckedAt);
    }

    [Fact]
    public void Nothing_to_go_on_gives_no_checkedAt()
    {
        Assert.Null(Report(Root().FullName, new MapState()).Build().CheckedAt);
    }

    [Fact]
    public void CheckedAt_is_never_in_the_future()
    {
        var root = Root(Run(Now.AddMinutes(10)));
        Assert.Equal(Now, Report(root.FullName, new MapState()).Build().CheckedAt);
    }

    [Fact]
    public void Map_etag_is_unchanged_when_only_checkedAt_moves()
    {
        var root = Root(Run(Now.AddMinutes(-40)));
        var state = new MapState();
        var first = state.Publish(Map(Now), Now);
        var report = Report(root.FullName, state);

        state.ActivityRefreshedAt = Now.AddMinutes(-50);
        var before = report.Build().CheckedAt;
        state.ActivityRefreshedAt = Now.AddMinutes(-55);
        var after = report.Build().CheckedAt;
        var second = state.Publish(Map(Now), Now.AddMinutes(10));

        Assert.NotEqual(before, after);
        Assert.Equal(first.ETag, second.ETag);
        Assert.Equal(first.Version, second.Version);
    }
}
