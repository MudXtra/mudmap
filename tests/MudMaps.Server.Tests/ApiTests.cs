using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MudMaps.Server.Maps;

namespace MudMaps.Server.Tests;

public class ApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    // No background refresh and no real repos or sites: the tests publish maps themselves.
    private WebApplicationFactory<Program> App => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("Maps:ReposRoot", "/nonexistent");
        b.UseSetting("Maps:Sites:colony", "/nonexistent");
        b.ConfigureServices(s =>
        {
            var hosted = s.Where(d => d.ImplementationType == typeof(MapRefreshService)).ToList();
            foreach (var d in hosted) s.Remove(d);
        });
    });

    private static MapDto EmptyMap() =>
        MapComposer.Compose([], new Dictionary<string, RepoActivity>(), 30, DateTimeOffset.UnixEpoch, prsLive: false);

    [Fact]
    public async Task Map_is_503_until_built_then_served_with_etag_and_304()
    {
        var app = App;
        var client = app.CreateClient();

        var before = await client.GetAsync("/api/map");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, before.StatusCode);

        app.Services.GetRequiredService<MapState>().Publish(EmptyMap(), DateTimeOffset.UnixEpoch);
        var after = await client.GetAsync("/api/map");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal("application/json", after.Content.Headers.ContentType?.MediaType);
        var etag = after.Headers.ETag;
        Assert.NotNull(etag);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/map");
        conditional.Headers.IfNoneMatch.Add(etag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(conditional)).StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Only_reads_are_allowed(string method)
    {
        var client = App.CreateClient();
        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/map"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var response = await App.CreateClient().GetAsync("/api/health");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(response.Headers.Contains("X-Frame-Options"));
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("frame-ancestors 'self';", csp);
        Assert.Contains("script-src 'none'", csp);
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Site_root_redirects_once_and_serves_index()
    {
        var site = Directory.CreateTempSubdirectory();
        File.WriteAllText(Path.Combine(site.FullName, "index.html"), "<!doctype html><title>t</title><script type=\"importmap\">{}</script>");
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Maps:ReposRoot", "/nonexistent");
            b.UseSetting("Maps:Sites:colony", site.FullName);
            b.ConfigureServices(s => s.Remove(s.Single(d => d.ImplementationType == typeof(MapRefreshService))));
        });
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var bare = await client.GetAsync("/colony?zoom=x");
        Assert.Equal(HttpStatusCode.Redirect, bare.StatusCode);
        Assert.Equal("/colony/?zoom=x", bare.Headers.Location?.OriginalString);

        var index = await client.GetAsync("/colony/");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        var csp = index.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("'wasm-unsafe-eval'", csp);
        Assert.Contains("'sha256-RBNvo1WzZ4oRRq0W9+hknpT7T8If536DEMBg9hyq/4o='", csp); // hash of "{}"
    }

    [Fact]
    public async Task Missing_site_folder_is_not_served_and_paths_cannot_escape()
    {
        var client = App.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/colony/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/colony/../../etc/passwd")).StatusCode);
    }

    [Fact]
    public async Task Path_base_serves_chooser_sites_and_aliases()
    {
        var site = Directory.CreateTempSubdirectory();
        File.WriteAllText(Path.Combine(site.FullName, "index.html"), "<!doctype html><title>t</title>");
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Maps:ReposRoot", "/nonexistent");
            b.UseSetting("Maps:PathBase", "/mudmap");
            b.UseSetting("Maps:Sites:space", site.FullName);
            b.UseSetting("Maps:Aliases:stars", "space");
            b.ConfigureServices(s => s.Remove(s.Single(d => d.ImplementationType == typeof(MapRefreshService))));
        });
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var bare = await client.GetAsync("/mudmap");
        Assert.Equal(HttpStatusCode.Redirect, bare.StatusCode);
        Assert.Equal("/mudmap/", bare.Headers.Location?.OriginalString);

        var chooser = await (await client.GetAsync("/mudmap/")).Content.ReadAsStringAsync();
        Assert.Contains("href=\"space/\"", chooser);

        var alias = await client.GetAsync("/mudmap/stars/?zoom=x");
        Assert.Equal(HttpStatusCode.Redirect, alias.StatusCode);
        Assert.Equal("/mudmap/space/?zoom=x", alias.Headers.Location?.OriginalString);

        var noSlash = await client.GetAsync("/mudmap/space");
        Assert.Equal("/mudmap/space/", noSlash.Headers.Location?.OriginalString);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/mudmap/space/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/mudmap/api/health")).StatusCode);
    }

    [Fact]
    public async Task Frame_ancestors_accept_only_https_origins()
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Maps:ReposRoot", "/nonexistent");
            b.UseSetting("Maps:FrameAncestors:0", "https://ai.coderproject.org");
            b.UseSetting("Maps:FrameAncestors:1", "*");
            b.UseSetting("Maps:FrameAncestors:2", "http://plain.example");
            b.ConfigureServices(s => s.Remove(s.Single(d => d.ImplementationType == typeof(MapRefreshService))));
        });
        var csp = (await app.CreateClient().GetAsync("/api/health")).Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("frame-ancestors 'self' https://ai.coderproject.org;", csp);
    }
}
