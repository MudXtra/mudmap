using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using MudMaps.Server;
using MudMaps.Server.GitHub;
using MudMaps.Server.Maps;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 4 * 1024;
    k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
    // Live-update sockets stay open; the rate limiter only limits how fast they are opened.
    k.Limits.MaxConcurrentConnections = 2000;
    k.Limits.MaxConcurrentUpgradedConnections = 1000;
});

builder.Services.Configure<MapsOptions>(builder.Configuration.GetSection(MapsOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MapState>();
builder.Services.AddHttpClient<GitHubActivityClient>(c =>
{
    c.BaseAddress = new Uri("https://api.github.com/");
    c.Timeout = TimeSpan.FromSeconds(60);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("MudMaps/1.0");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
});
builder.Services.AddHostedService<MapRefreshService>();
builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 1024;
    o.EnableDetailedErrors = false;
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 600,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

var app = builder.Build();
var maps = app.Services.GetRequiredService<IOptions<MapsOptions>>().Value;
var sites = maps.Sites;
var headers = new SecurityHeaders(sites, maps.FrameAncestors);

// Behind the proxy at e.g. /mudmap; the apps find the service relative to their own base, so nothing else changes.
if (!string.IsNullOrEmpty(maps.PathBase)) app.UsePathBase(maps.PathBase);
app.UseRateLimiter();
app.Use(async (ctx, next) =>
{
    if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !ctx.Request.Path.StartsWithSegments("/hubs"))
    {
        ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        return;
    }
    headers.Apply(ctx);
    // "/colony" -> "/colony/" so the app's relative paths resolve, and old names ("/stars/...") -> new ones.
    // Static files skip any request that matched an endpoint, so this is done here rather than as a mapped route.
    var path = ctx.Request.Path.Value;
    if (path is { Length: > 1 } && sites.ContainsKey(path[1..]))
    {
        ctx.Response.Redirect(ctx.Request.PathBase + path + "/" + ctx.Request.QueryString);
        return;
    }
    var first = path?.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    if (first is not null && maps.Aliases.TryGetValue(first, out var renamed) && sites.ContainsKey(renamed))
    {
        ctx.Response.Redirect(ctx.Request.PathBase + "/" + renamed + "/" + ctx.Request.QueryString);
        return;
    }
    await next();
});

app.MapGet("/api/map", (HttpContext ctx, MapState state) =>
{
    if (state.Current is not { } map)
    {
        ctx.Response.Headers.RetryAfter = "30";
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    ctx.Response.Headers.ETag = map.ETag;
    ctx.Response.Headers.CacheControl = "no-cache";
    if (ctx.Request.Headers.IfNoneMatch == map.ETag) return Results.StatusCode(StatusCodes.Status304NotModified);
    return Results.Bytes(map.Body, "application/json");
});

app.MapGet("/api/health", (MapState state, TimeProvider clock) => state.Current is { } m
    ? Results.Ok(new
    {
        ready = true, version = m.Version, builtAt = m.BuiltAt, areas = m.Areas, rooms = m.Rooms, prsLive = m.PrsLive,
        tokenExpiresAt = state.TokenExpiresAt?.ToString("yyyy-MM-dd"), tokenExpiringSoon = state.TokenExpiringSoon(clock.GetUtcNow()),
    })
    : Results.Ok(new { ready = false }));

app.MapHub<MapHub>("/hubs/map", o => o.Transports = HttpTransportType.WebSockets | HttpTransportType.LongPolling);

// The published map apps, each under its own segment: /colony/, /city/, ...
var types = new FileExtensionContentTypeProvider();
types.Mappings[".dat"] = "application/octet-stream";
types.Mappings[".blat"] = "application/octet-stream";
types.Mappings[".webcil"] = "application/octet-stream";
foreach (var (segment, folder) in sites)
{
    if (!Directory.Exists(folder))
    {
        app.Logger.LogWarning("Site {Segment} folder {Folder} is missing; skipping", segment, folder);
        continue;
    }
    var files = new PhysicalFileProvider(Path.GetFullPath(folder));
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files, RequestPath = $"/{segment}" });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files, RequestPath = $"/{segment}", ContentTypeProvider = types });
}
app.MapGet("/", (HttpContext ctx) =>
{
    // "/mudmap" -> "/mudmap/" so the chooser's relative links resolve.
    if (ctx.Request.PathBase.HasValue && ctx.Request.Path.Value is "" or null)
        return Results.Redirect(ctx.Request.PathBase + "/" + ctx.Request.QueryString);
    return Results.Content(LandingPage.Render(sites.Keys), "text/html; charset=utf-8");
});

app.Run();

static string ClientKey(HttpContext ctx) =>
    ctx.Request.Headers.TryGetValue("CF-Connecting-IP", out var ip) && ip.Count == 1 && ip[0]!.Length <= 64
        ? ip[0]!
        : ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

public partial class Program;
