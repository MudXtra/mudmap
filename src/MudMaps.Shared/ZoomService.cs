using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MudMaps.Shared;

/// <summary>Animates an SVG viewBox through the shared zoom module.</summary>
public sealed class ZoomService(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    private async ValueTask<IJSObjectReference> ModuleAsync() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", SharedAssets.ZoomModule);

    public async Task ZoomToAsync(ElementReference svg, double x, double y, double w, double h, int ms = 700)
    {
        var module = await ModuleAsync();
        await module.InvokeVoidAsync("zoomTo", svg, x, y, w, h, ms);
    }

    /// <summary>
    /// The "Skip animations" transition: the map fades in over about 150 ms (none with reduced motion). Call it right
    /// before jumping to the new view.
    /// </summary>
    public async Task FadeAsync(ElementReference svg, int ms = 150)
    {
        var module = await ModuleAsync();
        await module.InvokeVoidAsync("fadeIn", svg, ms);
    }

    /// <summary>On narrow screens, scroll the map up so it stays in sight above the bottom sheet.</summary>
    public async Task RevealMapAsync()
    {
        var module = await ModuleAsync();
        await module.InvokeVoidAsync("revealMap");
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
    }
}
