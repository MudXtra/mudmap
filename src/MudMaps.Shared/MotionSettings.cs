using Microsoft.JSInterop;

namespace MudMaps.Shared;

/// <summary>
/// The reader's choice to skip the rides between map levels ("Skip animations" under the map). Remembered in the
/// browser's local storage, so it holds across visits and across the maps.
/// </summary>
public sealed class MotionSettings(IJSRuntime js)
{
    /// <summary>Local storage key for the choice.</summary>
    public const string StorageKey = "mudmaps.skipAnimations";

    private bool _loaded;

    /// <summary>Raised when the choice changes.</summary>
    public event Action? Changed;

    /// <summary>True when level changes should jump straight to the destination with a short fade.</summary>
    public bool SkipAnimations { get; private set; }

    /// <summary>Reads the saved choice once; animations stay on when nothing is saved or storage is unavailable.</summary>
    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            SkipAnimations = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey) == "true";
        }
        catch (JSException)
        {
            SkipAnimations = false;
        }
    }

    /// <summary>Sets and saves the choice.</summary>
    public async Task SetAsync(bool skip)
    {
        _loaded = true;
        if (skip == SkipAnimations) return;
        SkipAnimations = skip;
        Changed?.Invoke();
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", StorageKey, skip ? "true" : "false");
        }
        catch (JSException)
        {
            // Private windows can refuse storage; the choice still holds for this visit.
        }
    }
}
