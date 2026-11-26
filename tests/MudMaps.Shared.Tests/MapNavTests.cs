using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace MudMaps.Shared.Tests;

/// <summary>The hero's app switch: a Space | City button group, the current app selected and the other
/// a link to it; no menu, and nothing that points to Colony or Islands.</summary>
// MudBlazor's services are async-disposable only, so the context is disposed with DisposeAsync, not by xUnit's Dispose.
public sealed class MapNavTests : IAsyncLifetime
{
    private readonly BunitContext _ctx = new();
    private const string Switch = ".mm-nav-switch";

    public MapNavTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        _ctx.Services.AddSingleton(new HttpClient { BaseAddress = new Uri("http://localhost/space/") });
        _ctx.Services.AddScoped<UsageTracker>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    private IRenderedComponent<MapNav> Render(string current, string page)
    {
        _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"http://localhost/{page}/");
        return _ctx.Render<MapNav>(p => p.Add(x => x.Current, current));
    }

    [Theory]
    [InlineData("space", "Space", "City", "http://localhost/city/")]
    [InlineData("city", "City", "Space", "http://localhost/space/")]
    public void The_current_app_is_selected_and_the_other_is_a_link_to_it(string current, string label, string other, string href)
    {
        var nav = Render(current, current);
        var segments = nav.FindAll($"{Switch} > *");
        Assert.Equal(["Space", "City"], segments.Select(s => s.TextContent.Trim()));

        var selected = Assert.Single(nav.FindAll($"{Switch} [aria-current='page']"));
        Assert.Equal(label, selected.TextContent.Trim());
        Assert.Equal("BUTTON", selected.TagName);
        Assert.Contains("mm-nav-on", selected.ClassList);
        Assert.Null(selected.GetAttribute("href"));

        var link = Assert.Single(nav.FindAll($"{Switch} a"));
        Assert.Equal(other, link.TextContent.Trim());
        Assert.Equal(href, link.GetAttribute("href"));
        Assert.Null(link.GetAttribute("aria-current"));
        Assert.DoesNotContain("mm-nav-on", link.ClassList);
    }

    [Theory]
    [InlineData("colony")]
    [InlineData("islands")]
    public void Colony_and_Islands_show_both_links_with_neither_selected(string current)
    {
        var nav = Render(current, current);
        Assert.Empty(nav.FindAll($"{Switch} [aria-current]"));
        Assert.Empty(nav.FindAll(".mm-nav-on"));
        var links = nav.FindAll($"{Switch} a");
        Assert.Equal(["http://localhost/space/", "http://localhost/city/"], links.Select(a => a.GetAttribute("href")));
    }

    [Fact]
    public void There_is_no_menu_arrow_and_no_link_to_colony_or_islands()
    {
        var nav = Render("space", "space");
        Assert.Empty(nav.FindAll(".mud-menu"));
        Assert.Empty(nav.FindAll($"{Switch} svg"));
        Assert.DoesNotContain(nav.FindAll("a"), a =>
            (a.GetAttribute("href") ?? "").Contains("colony") || (a.GetAttribute("href") ?? "").Contains("islands"));
    }

    [Fact]
    public void The_MCP_link_is_built_like_the_switch_so_it_is_the_same_size()
    {
        // The MCP link matches the switch. Same group variant and size classes and the same button classes
        // as the switch's link, so MudBlazor gives both the same height, padding and type size.
        var nav = Render("space", "space");
        var mcpGroup = nav.Find(".mm-nav-mcp");
        var switchGroup = nav.Find(Switch);
        Assert.Equal(GroupLook(switchGroup.ClassList), GroupLook(mcpGroup.ClassList));

        var mcp = Assert.Single(mcpGroup.QuerySelectorAll("a"));
        Assert.Equal("MCP", mcp.TextContent.Trim());
        Assert.Equal("connect", mcp.GetAttribute("href"));
        Assert.Equal("Connect to Mud Maps over MCP", mcp.GetAttribute("aria-label"));
        Assert.Equal(nav.Find($"{Switch} a").ClassList.OrderBy(c => c), mcp.ClassList.OrderBy(c => c));
        Assert.Empty(nav.FindAll(".mud-chip"));

        static IEnumerable<string> GroupLook(IEnumerable<string> classes) =>
            classes.Where(c => c.StartsWith("mud-button-group-", StringComparison.Ordinal)).Order();
    }

    [Fact]
    public async Task Clicking_the_MCP_link_counts_an_mcp_chip_click()
    {
        var handler = new RecordingHandler();
        _ctx.Services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/space/") });
        var nav = Render("space", "space");
        await nav.Find(".mm-nav-mcp a").ClickAsync(new());
        var body = await handler.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("\"mcp_chip_click\"", body);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public TaskCompletionSource<string> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            First.TrySetResult(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public void The_switch_sends_only_the_space_and_city_nav_events()
    {
        Assert.Equal(["mcp_chip_click", "nav_space", "nav_city"], UsageEvents.Names);
    }
}
