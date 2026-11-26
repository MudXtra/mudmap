using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using MudMaps.Server.Maps;

namespace MudMaps.Server.Tests;

/// <summary>Compression, rate limits and Cache-Control: how the server delivers the map and the static apps.</summary>
public class DeliveryTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static MapDto EmptyMap() =>
        MapComposer.Compose([], new Dictionary<string, RepoActivity>(), 30, DateTimeOffset.UnixEpoch, prsLive: false);

    // A new factory per call: every test gets its own rate limiter state.
    private WebApplicationFactory<Program> NewApp(string? siteFolder = null, bool publishMap = true)
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Maps:ReposRoot", "/nonexistent");
            b.UseSetting("Maps:Sites:colony", siteFolder ?? "/nonexistent");
            b.UseSetting("Usage:StateFile", Path.Combine(Path.GetTempPath(), $"mudmaps-usage-{Guid.NewGuid():N}.json"));
            b.ConfigureServices(s => s.Remove(s.Single(d => d.ImplementationType == typeof(MapRefreshService))));
        });
        if (publishMap) app.Services.GetRequiredService<MapState>().Publish(EmptyMap(), DateTimeOffset.UnixEpoch);
        return app;
    }

    private static HttpRequestMessage Get(string url, string? clientIp = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (clientIp is not null) request.Headers.Add("CF-Connecting-IP", clientIp);
        return request;
    }

    private static string Decode(byte[] body, string encoding)
    {
        using var input = new MemoryStream(body);
        using Stream decoder = encoding switch
        {
            "br" => new BrotliStream(input, CompressionMode.Decompress),
            "gzip" => new GZipStream(input, CompressionMode.Decompress),
            _ => throw new ArgumentException(encoding),
        };
        using var reader = new StreamReader(decoder);
        return reader.ReadToEnd();
    }

    // ---- compression ----

    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task Map_json_is_compressed_with_the_encoding_the_client_asks_for(string encoding)
    {
        var client = NewApp().CreateClient();
        var plain = await client.GetByteArrayAsync("/api/map");

        using var request = Get("/api/map");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(encoding));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(encoding, response.Content.Headers.ContentEncoding.Single());
        Assert.Contains("Accept-Encoding", response.Headers.Vary);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(plain), Decode(await response.Content.ReadAsByteArrayAsync(), encoding));
    }

    [Fact]
    public async Task Map_json_is_not_compressed_unless_asked()
    {
        var response = await NewApp().CreateClient().GetAsync("/api/map");
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task Brotli_is_preferred_when_both_are_offered()
    {
        using var request = Get("/api/map");
        request.Headers.AcceptEncoding.ParseAdd("gzip, br");
        var response = await NewApp().CreateClient().SendAsync(request);
        Assert.Equal("br", response.Content.Headers.ContentEncoding.Single());
    }

    // ---- rate limits ----

    [Fact]
    public async Task Map_allows_30_requests_a_minute_per_client_then_429()
    {
        var client = NewApp().CreateClient();
        for (var i = 0; i < 30; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/map", "203.0.113.7"))).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Get("/api/map", "203.0.113.7"))).StatusCode);
        // Another client (by CF-Connecting-IP) is unaffected.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/map", "203.0.113.8"))).StatusCode);
    }

    [Fact]
    public async Task Static_files_have_no_dotnet_rate_limit()
    {
        var site = Directory.CreateTempSubdirectory();
        File.WriteAllText(Path.Combine(site.FullName, "zoom.js"), "// js");
        var client = NewApp(site.FullName).CreateClient();
        for (var i = 0; i < 200; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/colony/zoom.js", "203.0.113.7"))).StatusCode);
    }

    [Fact]
    public async Task A_map_request_does_not_put_static_files_or_health_on_the_map_limit()
    {
        // Regression: the map limiter and the unlimited catch-all shared the bare client key, so the first request
        // decided the partition for both. After one map request, every static file and health check counted toward 30.
        var site = Directory.CreateTempSubdirectory();
        File.WriteAllText(Path.Combine(site.FullName, "zoom.js"), "// js");
        var client = NewApp(site.FullName).CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/map", "203.0.113.7"))).StatusCode);
        for (var i = 0; i < 40; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/colony/zoom.js", "203.0.113.7"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/health", "203.0.113.7"))).StatusCode);
        }
        // And the map's own 30 are still all there: 29 more, then 429.
        for (var i = 1; i < 30; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/map", "203.0.113.7"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Get("/api/map", "203.0.113.7"))).StatusCode);
    }

    [Fact]
    public async Task A_static_request_first_does_not_lift_the_map_limit()
    {
        // The same collision the other way round: an unlimited partition created first made the map unlimited.
        var site = Directory.CreateTempSubdirectory();
        File.WriteAllText(Path.Combine(site.FullName, "zoom.js"), "// js");
        var client = NewApp(site.FullName).CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/colony/zoom.js", "203.0.113.7"))).StatusCode);
        for (var i = 0; i < 30; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/map", "203.0.113.7"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Get("/api/map", "203.0.113.7"))).StatusCode);
    }

    [Fact]
    public async Task Each_limiter_counts_only_its_own_requests()
    {
        var client = NewApp().CreateClient();
        // Use up the map's 30; status, stats and track still have their whole windows.
        for (var i = 0; i < 30; i++) await client.SendAsync(Get("/api/map", "203.0.113.7"));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Get("/api/map", "203.0.113.7"))).StatusCode);
        for (var i = 0; i < 120; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/status", "203.0.113.7"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Get("/api/status", "203.0.113.7"))).StatusCode);
        for (var i = 0; i < 30; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/stats", "203.0.113.7"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Get("/api/stats", "203.0.113.7"))).StatusCode);
        for (var i = 0; i < 60; i++)
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Track("203.0.113.7"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Track("203.0.113.7"))).StatusCode);
    }

    private static HttpRequestMessage Track(string clientIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/track")
        {
            Content = new StringContent($$"""{"event":"{{UsageCounters.TrackEvents.First()}}"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("CF-Connecting-IP", clientIp);
        return request;
    }

    private static HttpRequestMessage Negotiate(string clientIp) =>
        new(HttpMethod.Post, "/hubs/map/negotiate?negotiateVersion=1") { Headers = { { "CF-Connecting-IP", clientIp } } };

    [Theory]
    [InlineData("map", 30)]
    [InlineData("status", 120)]
    [InlineData("stats", 30)]
    [InlineData("track", 60)]
    [InlineData("hub-connect", 10)]
    public async Task A_rate_limit_429_carries_retry_after_and_the_security_headers(string limiter, int limit)
    {
        var client = NewApp().CreateClient();
        Func<HttpRequestMessage> next = limiter switch
        {
            "track" => () => Track("203.0.113.9"),
            "hub-connect" => () => Negotiate("203.0.113.9"),
            _ => () => Get($"/api/{limiter}", "203.0.113.9"),
        };
        for (var i = 0; i < limit; i++) Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.SendAsync(next())).StatusCode);
        var limited = await client.SendAsync(next());
        AssertLimitedResponse(limited);
    }

    internal static void AssertLimitedResponse(HttpResponseMessage limited)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var after = limited.Headers.RetryAfter?.Delta;
        Assert.NotNull(after);
        Assert.InRange(after!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        Assert.Equal("nosniff", limited.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", limited.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("frame-ancestors 'self'", limited.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-cache", limited.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Hub_allows_about_10_connects_a_minute_per_client_then_429()
    {
        var client = NewApp().CreateClient();
        for (var i = 0; i < 10; i++)
        {
            var ok = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/hubs/map/negotiate?negotiateVersion=1")
                { Headers = { { "CF-Connecting-IP", "203.0.113.7" } } });
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        var limited = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/hubs/map/negotiate?negotiateVersion=1")
            { Headers = { { "CF-Connecting-IP", "203.0.113.7" } } });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Hub_allows_5_open_connections_per_client_and_refuses_the_6th()
    {
        var app = NewApp();
        var open = new List<HubConnection>();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var hub = Connect(app, "203.0.113.7");
                open.Add(hub);
                await hub.StartAsync();
            }
            var sixth = Connect(app, "203.0.113.7");
            open.Add(sixth);
            await Assert.ThrowsAnyAsync<Exception>(() => sixth.StartAsync());

            // A different client still connects, and closing one of the first five frees a slot.
            var other = Connect(app, "203.0.113.8");
            open.Add(other);
            await other.StartAsync();
            await open[0].StopAsync();
            await WaitUntilAsync(async () =>
            {
                var again = Connect(app, "203.0.113.7");
                open.Add(again);
                try { await again.StartAsync(); return true; } catch { return false; }
            });
        }
        finally
        {
            foreach (var hub in open) await hub.DisposeAsync();
        }
    }

    private static HubConnection Connect(WebApplicationFactory<Program> app, string clientIp) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "/hubs/map"), o =>
            {
                // WebSockets, as browsers use: one open request per connection (long polling needs a second one for sends).
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
                o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                o.Headers["CF-Connecting-IP"] = clientIp;
                o.WebSocketFactory = async (context, cancel) =>
                {
                    var socketClient = app.Server.CreateWebSocketClient();
                    socketClient.ConfigureRequest = r => r.Headers["CF-Connecting-IP"] = clientIp;
                    return await socketClient.ConnectAsync(context.Uri, cancel);
                };
            })
            .Build();

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(100);
        }
    }

    // ---- Cache-Control ----

    private const string Immutable = "public, max-age=31536000, immutable";

    [Theory]
    [InlineData("/colony/", "no-cache")]
    [InlineData("/colony/index.html", "no-cache")]
    [InlineData("/colony/zoom.js", "no-cache")]
    [InlineData("/colony/css/app.css", "no-cache")]
    [InlineData("/colony/_content/MudBlazor/MudBlazor.min.css", "no-cache")]
    [InlineData("/colony/_content/MudMaps.Shared/mudmaps.css", "no-cache")]
    [InlineData("/colony/city.l5utpqx140.js", Immutable)]
    [InlineData("/colony/_content/MudMaps.Shared/mudmaps.a1b2c3d4e5.css", Immutable)]
    [InlineData("/colony/_framework/blazor.webassembly.w3qd1tpl0e.js", Immutable)]
    [InlineData("/colony/_framework/dotnet.native.wasm", Immutable)]
    public async Task Static_files_get_the_right_cache_control(string url, string expected)
    {
        var site = Directory.CreateTempSubdirectory();
        foreach (var file in new[]
        {
            "index.html", "zoom.js", "css/app.css", "_content/MudBlazor/MudBlazor.min.css", "_content/MudMaps.Shared/mudmaps.css",
            "city.l5utpqx140.js", "_content/MudMaps.Shared/mudmaps.a1b2c3d4e5.css",
            "_framework/blazor.webassembly.w3qd1tpl0e.js", "_framework/dotnet.native.wasm",
        })
        {
            var path = Path.Combine(site.FullName, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
        var client = NewApp(site.FullName).CreateClient();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, response.Headers.CacheControl?.ToString().Replace(", ", ", "));
    }

    [Fact]
    public async Task Missing_fingerprinted_files_are_not_cached_as_immutable()
    {
        var site = Directory.CreateTempSubdirectory();
        var response = await NewApp(site.FullName).CreateClient().GetAsync("/colony/_framework/missing.wasm");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Map_json_and_health_are_never_immutable()
    {
        var client = NewApp().CreateClient();
        Assert.Equal("no-cache", (await client.GetAsync("/api/map")).Headers.CacheControl?.ToString());
        Assert.Equal("no-cache", (await client.GetAsync("/api/health")).Headers.CacheControl?.ToString());
    }
}
