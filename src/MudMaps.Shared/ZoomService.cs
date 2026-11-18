using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MudMaps.Shared;

/// <summary>Animates an SVG viewBox through the shared zoom module.</summary>
public sealed class ZoomService(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    private async ValueTask<IJSObjectReference> ModuleAsync() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./_content/MudMaps.Shared/zoom.js");

    public async Task ZoomToAsync(ElementReference svg, double x, double y, double w, double h, int ms = 700)
    {
        var module = await ModuleAsync();
        await module.InvokeVoidAsync("zoomTo", svg, x, y, w, h, ms);
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
