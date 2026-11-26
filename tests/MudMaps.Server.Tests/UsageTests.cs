using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MudMaps.Server.Maps;

namespace MudMaps.Server.Tests;

/// <summary>Usage counters: what gets counted, how it is stored, and what the two endpoints answer.</summary>
public class UsageTests
{
    private static readonly string[] Sites = ["space", "city", "colony", "islands"];

    [Theory]
    // A document request is a view; the assets behind it are not.
    // The chooser is counted by the endpoint that renders it (a returning visitor is redirected), not here.
    [InlineData("GET", "/", null, null)]
    [InlineData("GET", "/space/", "views", "space")]
    [InlineData("GET", "/city/index.html", "views", "city")]
    [InlineData("GET", "/space/app.css", null, null)]
    [InlineData("GET", "/notasite/", null, null)]
    // The Connect-via-MCP page, under any app, is one view bucket of its own.
    [InlineData("GET", "/city/connect", "views", "connect")]
    [InlineData("GET", "/space/connect/", "views", "connect")]
    [InlineData("GET", "/notasite/connect", null, null)]
    [InlineData("POST", "/city/connect", null, null)]
    [InlineData("GET", "/city/connect/extra", null, null)]
    // Each API route is its own bucket.
    [InlineData("GET", "/api/map", "api", "map")]
    [InlineData("GET", "/api/status", "api", "status")]
    [InlineData("GET", "/api/health", "api", "health")]
    [InlineData("GET", "/api/stats", "api", "stats")]
    [InlineData("GET", "/api/research/prs", "api", "research_prs")]
    [InlineData("GET", "/api/research/pr", "api", "research_pr")]
    [InlineData("GET", "/api/research/limits", "api", "research_limits")]
    [InlineData("GET", "/api/research", "api", "research")]
    [InlineData("POST", "/api/track", "api", "track")]
    // /mcp is counted per tool by the gateway, and a hub socket is not a request worth counting.
    [InlineData("POST", "/mcp", null, null)]
    [InlineData("POST", "/api/map", null, null)]
    public void Classify_routes_a_request_to_one_bucket(string method, string path, string? kind, string? key)
    {
        var bucket = UsageCounters.Classify(method, path, Sites);
        Assert.Equal(kind, bucket?.Kind);
        Assert.Equal(key, bucket?.Key);
        // A state file may only bring back what Classify can produce, so the two lists must agree.
        if (bucket is { Kind: "api" } api) Assert.Contains(api.Key, UsageCounters.ApiKeys);
    }

    [Fact]
    public void The_server_accepts_exactly_the_events_the_apps_send()
    {
        // Every allowed event has a caller, and every caller's event is allowed: no dead buckets, no silent 400s.
        Assert.Equal(MudMaps.Shared.UsageEvents.Names.OrderBy(n => n, StringComparer.Ordinal),
            UsageCounters.TrackEvents.OrderBy(n => n, StringComparer.Ordinal));
        // Colony and Islands left the switch, and their events with them.
        Assert.DoesNotContain("nav_colony", UsageCounters.TrackEvents);
        Assert.DoesNotContain("nav_islands", UsageCounters.TrackEvents);
    }

    [Fact]
    public void Counters_bucket_by_day_and_serve_the_documented_shape()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 23, 18, 0, 0, TimeSpan.Zero));
        var counters = new UsageCounters(new UsageOptions { StateFile = "" }, clock, NullLogger<UsageCounters>.Instance, Sites);

        counters.Increment("views", "space");
        counters.Increment("views", "space");
        counters.Increment("views", "city");
        counters.Track("mcp_chip_click");
        Assert.False(counters.Track("made_up_event")); // closed allow-list

        using var doc = JsonDocument.Parse(counters.Build().ToJsonString());
        Assert.Equal("2026-11-23T18:00:00.0000000+00:00", doc.RootElement.GetProperty("generatedAt").GetString());
        var day = doc.RootElement.GetProperty("days").GetProperty("2026-11-23");
        Assert.Equal(2, day.GetProperty("views").GetProperty("space").GetInt32());
        Assert.Equal(1, day.GetProperty("views").GetProperty("city").GetInt32());
        Assert.Equal(1, day.GetProperty("ui").GetProperty("mcp_chip_click").GetInt32());
        Assert.Single(doc.RootElement.GetProperty("days").EnumerateObject()); // one day, not one per call
    }

    [Fact]
    public void Counters_write_once_and_read_themselves_back_after_a_restart()
    {
        var file = Path.Combine(Path.GetTempPath(), $"mudmaps-usage-{Guid.NewGuid():N}.json");
        var options = new UsageOptions { StateFile = file, FlushSeconds = 60 };
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 24, 3, 0, 0, TimeSpan.Zero));

        var first = new UsageCounters(options, clock, NullLogger<UsageCounters>.Instance, Sites);
        first.Increment("views", "space");
        Assert.True(first.Flush());                    // forced write
        Assert.False(first.Flush());                   // nothing new to write
        first.Increment("views", "space");
        Assert.False(first.Flush());                   // inside the once-a-minute window
        Assert.True(first.Flush(force: true));

        var reopened = new UsageCounters(options, clock, NullLogger<UsageCounters>.Instance, Sites);
        using var doc = JsonDocument.Parse(reopened.Build().ToJsonString());
        Assert.Equal(2, doc.RootElement.GetProperty("days").GetProperty("2026-11-24").GetProperty("views").GetProperty("space").GetInt32());
        File.Delete(file);
    }

    [Fact]
    public async Task The_flush_timer_writes_the_counters_without_anyone_reading_stats()
    {
        var file = Path.Combine(Path.GetTempPath(), $"mudmaps-usage-{Guid.NewGuid():N}.json");
        var options = new UsageOptions { StateFile = file, FlushSeconds = 60 };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 3, 0, 0, TimeSpan.Zero));
        var counters = new UsageCounters(options, clock, NullLogger<UsageCounters>.Instance, Sites);
        using var timer = new UsageFlushService(counters, options, clock);
        await timer.StartAsync(CancellationToken.None);

        counters.Increment("views", "space");
        clock.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(100);
        Assert.False(File.Exists(file));               // not yet: one tick a FlushSeconds

        clock.Advance(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 50 && !File.Exists(file); i++) await Task.Delay(50);
        Assert.True(File.Exists(file));
        using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
            Assert.Equal(1, doc.RootElement.GetProperty("days").GetProperty("2026-11-24").GetProperty("views").GetProperty("space").GetInt32());

        await timer.StopAsync(CancellationToken.None);
        File.Delete(file);
    }

    [Fact]
    public void A_damaged_state_file_is_ignored_rather_than_refusing_to_start()
    {
        var file = Path.Combine(Path.GetTempPath(), $"mudmaps-usage-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, "{ not json");
        var counters = new UsageCounters(new UsageOptions { StateFile = file },
            new FixedClock(new DateTimeOffset(2026, 11, 24, 3, 0, 0, TimeSpan.Zero)), NullLogger<UsageCounters>.Instance, Sites);
        counters.Increment("views", "space");
        using var doc = JsonDocument.Parse(counters.Build().ToJsonString());
        Assert.Equal(1, doc.RootElement.GetProperty("days").GetProperty("2026-11-24").GetProperty("views").GetProperty("space").GetInt32());
        File.Delete(file);
    }
}

/// <summary>The state file is read back at start-up and republished on public /api/stats, and the
/// graphify child can write to its folder. So only a small file is read, and only the buckets this server writes.</summary>
public class UsageLoadTests
{
    private static readonly string[] Sites = ["space", "city", "colony", "islands"];
    private static readonly TimeProvider Clock = new FixedClock(new DateTimeOffset(2026, 11, 24, 3, 0, 0, TimeSpan.Zero));

    private static (UsageCounters Counters, string File) Reopen(string json)
    {
        var file = Path.Combine(Path.GetTempPath(), $"mudmaps-usage-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, json);
        return (new UsageCounters(new UsageOptions { StateFile = file }, Clock, NullLogger<UsageCounters>.Instance, Sites), file);
    }

    private static JsonElement Days(UsageCounters counters) =>
        JsonDocument.Parse(counters.Build().ToJsonString()).RootElement.GetProperty("days");

    [Fact]
    public void An_oversized_state_file_is_ignored()
    {
        // Valid JSON with a real-looking bucket, padded past 1 MiB.
        var json = """{ "days": { "2026-11-23": { "views": { "space": 5 } } } }""" + new string(' ', 1024 * 1024);
        var (counters, file) = Reopen(json);
        Assert.Empty(Days(counters).EnumerateObject());
        File.Delete(file);
    }

    [Fact]
    public void Foreign_kinds_keys_days_and_values_are_dropped_on_load()
    {
        var (counters, file) = Reopen("""
            { "days": {
                "2026-11-23": {
                  "views": { "space": 3, "chooser": 2, "connect": 1, "evil.example": 9, "city": -3, "colony": 99999999999, "islands": 1.5 },
                  "api": { "map": 2, "made_up": 1 },
                  "ui": { "nav_city": 1, "search_query": 4 },
                  "mcp": { "query_graph": 4, "rm_rf": 1 },
                  "secrets": { "x": 1 }
                },
                "not-a-day": { "views": { "space": 1 } },
                "2026-13-45": { "views": { "space": 1 } },
                "2026-11-22T00:00": { "views": { "space": 1 } }
            } }
            """);
        var days = Days(counters);
        var day = Assert.Single(days.EnumerateObject());
        Assert.Equal("2026-11-23", day.Name);
        string Keys(string kind) => string.Join(",", day.Value.GetProperty(kind).EnumerateObject().Select(k => $"{k.Name}={k.Value}"));
        Assert.Equal("chooser=2,connect=1,space=3", Keys("views"));
        Assert.Equal("map=2", Keys("api"));
        Assert.Equal("nav_city=1", Keys("ui"));
        Assert.Equal("query_graph=4", Keys("mcp"));
        Assert.False(day.Value.TryGetProperty("secrets", out _));
        File.Delete(file);
    }

    [Fact]
    public void Old_colony_and_islands_nav_counts_are_dropped_on_load_but_their_page_views_are_kept()
    {
        // The shape already deployed: nav_colony / nav_islands from before those buttons were removed.
        var (counters, file) = Reopen("""
            { "days": { "2026-11-23": {
                "views": { "colony": 4, "islands": 2, "space": 7 },
                "ui": { "nav_space": 3, "nav_city": 2, "nav_colony": 5, "nav_islands": 1 }
            } } }
            """);
        var day = Days(counters).GetProperty("2026-11-23");
        string Keys(string kind) => string.Join(",", day.GetProperty(kind).EnumerateObject().Select(k => $"{k.Name}={k.Value}"));
        Assert.Equal("nav_city=2,nav_space=3", Keys("ui"));
        Assert.Equal("colony=4,islands=2,space=7", Keys("views"));
        File.Delete(file);
    }

    [Theory]
    // JsonObject throws ArgumentException on a duplicate name while it is enumerated.
    [InlineData("""{ "days": { "2026-11-23": { "views": { "space": 1 } }, "2026-11-23": { "views": { "city": 1 } } } }""")]
    [InlineData("""{ "days": { "2026-11-23": { "views": { "space": 1, "space": 2 } } } }""")]
    public void A_state_file_with_duplicate_names_loads_as_empty(string json)
    {
        var (counters, file) = Reopen(json);
        Assert.Empty(Days(counters).EnumerateObject());
        File.Delete(file);
    }

    private const string RealBucket = """{ "days": { "2026-11-23": { "views": { "space": 5 } } } }""";

    [Fact]
    public void A_symlinked_state_file_is_ignored()
    {
        // The state path must be the file itself, not a link to one somewhere else. (Unix only:
        // creating a symlink on Windows needs a privilege test hosts rarely have.)
        if (OperatingSystem.IsWindows()) return;
        var target = Path.Combine(Path.GetTempPath(), $"mudmaps-usage-{Guid.NewGuid():N}.json");
        var link = Path.Combine(Path.GetTempPath(), $"mudmaps-usage-{Guid.NewGuid():N}.json");
        File.WriteAllText(target, RealBucket);
        File.CreateSymbolicLink(link, target);
        try
        {
            var counters = new UsageCounters(new UsageOptions { StateFile = link }, Clock, NullLogger<UsageCounters>.Instance, Sites);
            Assert.Empty(Days(counters).EnumerateObject());
        }
        finally { File.Delete(link); File.Delete(target); }
    }

    [Fact]
    public async Task A_fifo_at_the_state_path_is_ignored_without_blocking()
    {
        // A FIFO would block the open (and startup) until something writes to it.
        if (!OperatingSystem.IsLinux()) return;
        var fifo = Path.Combine(Path.GetTempPath(), $"mudmaps-usage-{Guid.NewGuid():N}.json");
        using (var mk = System.Diagnostics.Process.Start("mkfifo", fifo)) mk.WaitForExit();
        try
        {
            var load = Task.Run(() => new UsageCounters(new UsageOptions { StateFile = fifo }, Clock, NullLogger<UsageCounters>.Instance, Sites));
            var finished = await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(5))) == load;
            // A hung open has a reader waiting, so opening the write end releases it instead of leaking the thread.
            if (!finished) using (var release = new FileStream(fifo, FileMode.Open, FileAccess.Write)) { }
            Assert.True(finished, "loading from a FIFO blocked");
            Assert.Empty(Days(await load).EnumerateObject());
        }
        finally { File.Delete(fifo); }
    }

    [Fact]
    public void At_most_400_days_are_kept_on_load_and_on_flush()
    {
        // 450 days ending yesterday (the clock's today is 2026-11-24).
        var first = new DateOnly(2026, 11, 23).AddDays(-449);
        var days = string.Join(",", Enumerable.Range(0, 450).Select(i => $"\"{first.AddDays(i):yyyy-MM-dd}\": {{ \"views\": {{ \"space\": 1 }} }}"));
        var (counters, file) = Reopen($"{{ \"days\": {{ {days} }} }}");

        var loaded = Days(counters).EnumerateObject().Select(d => d.Name).ToList();
        Assert.Equal(400, loaded.Count);
        Assert.Equal(first.AddDays(50).ToString("yyyy-MM-dd"), loaded[0]); // the oldest 50 are gone
        Assert.Equal("2026-11-23", loaded[^1]);

        counters.Increment("views", "city"); // today makes 401 in memory
        Assert.True(counters.Flush(force: true));
        using var written = JsonDocument.Parse(File.ReadAllText(file));
        var flushed = written.RootElement.GetProperty("days").EnumerateObject().Select(d => d.Name).ToList();
        Assert.Equal(400, flushed.Count);
        Assert.Equal("2026-11-24", flushed[^1]);
        Assert.Equal(first.AddDays(51).ToString("yyyy-MM-dd"), flushed[0]);
        File.Delete(file);
    }
}

/// <summary>The two endpoints, on the same test host the other API tests use.</summary>
public class UsageApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private WebApplicationFactory<Program> App(string stateFile) => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("Maps:ReposRoot", "/nonexistent");
        b.UseSetting("Maps:Sites:colony", "/nonexistent");
        b.UseSetting("Usage:StateFile", stateFile);
        b.ConfigureServices(s =>
        {
            foreach (var d in s.Where(d => d.ImplementationType == typeof(MapRefreshService)).ToList()) s.Remove(d);
        });
    });

    [Fact]
    public async Task Track_counts_an_allowed_event_and_rejects_any_other_name()
    {
        var app = App(Path.Combine(Path.GetTempPath(), $"mudmaps-usage-{Guid.NewGuid():N}.json"));
        var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/stats")).StatusCode);

        var ok = await client.PostAsJsonAsync("/api/track", new { @event = "nav_city" });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        var bogus = await client.PostAsJsonAsync("/api/track", new { @event = "whatever_a_client_invents" });
        Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);

        using var doc = JsonDocument.Parse(await (await client.GetAsync("/api/stats")).Content.ReadAsStringAsync());
        var today = doc.RootElement.GetProperty("days").GetProperty(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"));
        Assert.Equal(1, today.GetProperty("ui").GetProperty("nav_city").GetInt32());
        Assert.Equal(2, today.GetProperty("api").GetProperty("track").GetInt32()); // the rejected call is a request too
        Assert.Equal(2, today.GetProperty("api").GetProperty("stats").GetInt32()); // the two reads above are counted too
    }

    [Theory]
    [InlineData("text/plain")]                      // the cross-site "simple request" another site can make its visitors send
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("application/json; charset=latin1")]
    [InlineData(null)]
    public async Task Track_takes_only_a_json_body(string? contentType)
    {
        var client = App(Path.Combine(Path.GetTempPath(), $"mudmaps-usage-{Guid.NewGuid():N}.json")).CreateClient();
        var content = new StringContent("""{"event":"nav_city"}""");
        content.Headers.ContentType = contentType is null ? null : System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync("/api/track", content)).StatusCode);

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/stats"));
        Assert.False(doc.RootElement.GetProperty("days").GetProperty(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")).TryGetProperty("ui", out _));
    }

    [Fact]
    public async Task The_chooser_is_counted_only_where_it_renders()
    {
        var app = App(Path.Combine(Path.GetTempPath(), $"mudmaps-usage-{Guid.NewGuid():N}.json"));
        var fresh = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var returning = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        returning.DefaultRequestHeaders.Add("Cookie", "mm-app=city");

        Assert.Equal(HttpStatusCode.Found, (await returning.GetAsync("/")).StatusCode); // sent back to City: no chooser
        Assert.Equal(HttpStatusCode.Found, (await returning.GetAsync("/")).StatusCode); // and again
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/")).StatusCode);        // rendered
        Assert.Equal(HttpStatusCode.OK, (await returning.GetAsync("/apps")).StatusCode); // rendered, saved choice or not

        using var doc = JsonDocument.Parse(await fresh.GetStringAsync("/api/stats"));
        var views = doc.RootElement.GetProperty("days").GetProperty(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")).GetProperty("views");
        Assert.Equal(2, views.GetProperty("chooser").GetInt32());
    }

    [Fact]
    public async Task Stats_is_limited_to_30_reads_a_minute_per_client()
    {
        // Each read runs Build() and Flush(); the weekly report needs one.
        var client = App(Path.Combine(Path.GetTempPath(), $"mudmaps-usage-{Guid.NewGuid():N}.json")).CreateClient();
        for (var i = 1; i <= 30; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/stats")).StatusCode);
    }

    [Fact]
    public async Task A_fresh_app_reports_the_read_itself_and_nothing_else()
    {
        var client = App(Path.Combine(Path.GetTempPath(), $"mudmaps-usage-{Guid.NewGuid():N}.json")).CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/stats"));
        Assert.True(doc.RootElement.TryGetProperty("generatedAt", out _));
        // The only thing a brand-new app can have counted is this very read: GET /api/stats is itself a request.
        var today = doc.RootElement.GetProperty("days").GetProperty(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"));
        Assert.Equal(1, today.GetProperty("api").GetProperty("stats").GetInt32());
        Assert.False(today.TryGetProperty("ui", out _));
    }
}

/// <summary>A clock that stands still, so a test's "today" and its flush window are both fixed.</summary>
file sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
