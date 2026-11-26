using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MudMaps.Server.Maps;

namespace MudMaps.Server.Tests;

/// <summary>The saved-app cookie: what counts as a saved app, and what the server does with it.</summary>
public class PreferenceTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("city", "city")]
    [InlineData("City", null)] // the app list is matched exactly; a renamed site's stale value is dropped, not followed
    [InlineData("notanapp", null)]
    [InlineData("colony", null)] // served by direct URL, but no longer offered
    [InlineData("islands", null)]
    public void Only_a_real_app_name_counts_as_a_saved_choice(string? value, string? expected)
        => Assert.Equal(expected, AppPreference.Read(value, AppPreference.Offered(["colony", "city", "islands", "space"])));

    [Fact]
    public void The_server_offers_the_same_apps_as_the_switch()
        // The switch's apps are visible through the nav events it sends: one nav_<app> per offered app, in order.
        => Assert.Equal(MudMaps.Shared.UsageEvents.Names.Where(n => n.StartsWith("nav_", StringComparison.Ordinal)),
            AppPreference.Listed.Select(a => "nav_" + a));

    [Fact]
    public void Only_offered_apps_this_deployment_serves_are_offered()
    {
        Assert.Equal(["space", "city"], AppPreference.Offered(["colony", "city", "islands", "space"]));
        Assert.Equal(["city"], AppPreference.Offered(["colony", "city"]));
    }

    [Theory]
    [InlineData("colony")]
    [InlineData("islands")]
    public async Task A_saved_colony_or_islands_shows_the_chooser_instead_of_redirecting(string saved)
    {
        var client = App().CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"mm-app={saved}");

        var response = await client.GetAsync("/");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/html", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task The_chooser_lists_only_space_and_city_in_that_order()
    {
        var html = await App().CreateClient().GetStringAsync("/");
        var links = System.Text.RegularExpressions.Regex.Matches(html, "<a href=\"([^\"]*)\"").Select(m => m.Groups[1].Value);
        Assert.Equal(["space/", "city/"], links);
        Assert.DoesNotContain("colony", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("islands", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Archipelago", html);
    }

    [Theory]
    [InlineData("/colony/")]
    [InlineData("/islands/")]
    public async Task A_direct_visit_to_colony_or_islands_leaves_a_saved_choice_alone(string path)
    {
        var response = await App().CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false })
            .GetAsync(path);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task The_root_sends_them_back_to_the_app_they_last_opened()
    {
        var client = App().CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", "mm-app=city");

        var response = await client.GetAsync("/");

        Assert.Equal(System.Net.HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/city/", response.Headers.Location?.ToString());
        // Reading the choice back must not rewrite it: only opening an app does that.
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task The_root_is_the_chooser_for_anyone_with_nothing_saved()
    {
        var response = await App().CreateClient().GetAsync("/");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/html", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task Apps_is_the_full_list_even_when_a_choice_is_saved()
    {
        var client = App().CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", "mm-app=city");
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/apps")).StatusCode);
    }

    [Fact]
    public async Task Opening_an_app_records_it_for_next_time()
    {
        // The test host has no built site folders, so the page itself is a 404 — the header is still the answer here.
        var response = await App().CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false })
            .GetAsync("/space/");
        var cookie = Assert.Single(response.Headers.TryGetValues("Set-Cookie", out var values) ? values : []);
        Assert.StartsWith("mm-app=space", cookie);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase); // shared by every app on the host
        Assert.Contains("max-age=31536000", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase); // no script needs it
    }

    private static WebApplicationFactory<Program> App() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseSetting("Maps:ReposRoot", "/nonexistent");
        b.UseSetting("Usage:StateFile", Path.Combine(Path.GetTempPath(), $"mudmaps-usage-{Guid.NewGuid():N}.json"));
        b.ConfigureServices(s =>
        {
            foreach (var d in s.Where(d => d.ImplementationType == typeof(MapRefreshService)).ToList()) s.Remove(d);
        });
    });
}
