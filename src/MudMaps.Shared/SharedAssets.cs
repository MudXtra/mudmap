using System.Reflection;

namespace MudMaps.Shared;

/// <summary>
/// Versioned URLs for the shared browser scripts. The version comes from the script's content at build time, so a new
/// script always gets a new URL and a browser's cached copy of an older one is never used with a newer app.
/// </summary>
public static class SharedAssets
{
    private static readonly string Version = typeof(SharedAssets).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "MudMaps.ZoomJsVersion")?.Value ?? "0";

    /// <summary>The shared zoom and label-fitting module, relative to the page's base address.</summary>
    public static string ZoomModule { get; } = $"./_content/MudMaps.Shared/zoom.js?v={Version}";
}
