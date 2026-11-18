using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using MudMaps.Server.Maps;

namespace MudMaps.Server.Tests;

public class HubTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private WebApplicationFactory<Program> App => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("Maps:ReposRoot", "/nonexistent");
        b.ConfigureServices(s => s.Remove(s.Single(d => d.ImplementationType == typeof(MapRefreshService))));
    });

    private static HubConnection Connect(WebApplicationFactory<Program> app) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "/hubs/map"), o => o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler())
            .Build();

    [Fact]
    public async Task Clients_hear_about_new_map_versions()
    {
        var app = App;
        await using var hub = Connect(app);
        var heard = new TaskCompletionSource<string>();
        hub.On<string>("mapUpdated", v => heard.TrySetResult(v));
        await hub.StartAsync();

        await app.Services.GetRequiredService<IHubContext<MapHub>>().Clients.All.SendAsync("mapUpdated", "abc");

        Assert.Equal("abc", await heard.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Clients_cannot_call_the_server()
    {
        var app = App;
        await using var hub = Connect(app);
        await hub.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("Anything"));
    }
}
