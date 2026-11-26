using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace MudMaps.Shared;

public static class ServiceCollectionExtensions
{
    /// <summary>MudBlazor plus the shared map data, zoom, scope and motion services.</summary>
    public static IServiceCollection AddMudMaps(this IServiceCollection services, Uri baseAddress)
    {
        services.AddMudServices();
        services.AddScoped(_ => new HttpClient { BaseAddress = baseAddress });
        services.AddScoped<MapDataService>();
        services.AddScoped<UsageTracker>();
        services.AddScoped<ZoomService>();
        services.AddScoped<MapScope>();
        services.AddScoped<MotionSettings>();
        services.AddScoped<Research.ResearchClient>();
        services.AddScoped<Research.ResearchState>();
        return services;
    }
}
