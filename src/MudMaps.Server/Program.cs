using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using MudMaps.Server;
using MudMaps.Server.GitHub;
using MudMaps.Server.Maps;
using MudMaps.Server.Research;

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
builder.Services.AddSingleton<IPrCacheSnapshot>(sp => sp.GetRequiredService<MapState>());
builder.Services.AddSingleton<IRepoGraphs>(sp => sp.GetRequiredService<MapState>());
builder.Services.AddSingleton<StatusReport>();
builder.Services.Configure<UsageOptions>(builder.Configuration.GetSection(UsageOptions.Section));
builder.Services.AddSingleton(sp => new UsageCounters(
    sp.GetRequiredService<IOptions<UsageOptions>>().Value,
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<UsageCounters>>(),
    sp.GetRequiredService<IOptions<MapsOptions>>().Value.Sites.Keys));
builder.Services.AddHostedService(sp => new UsageFlushService(
    sp.GetRequiredService<UsageCounters>(),
    sp.GetRequiredService<IOptions<UsageOptions>>().Value,
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddHttpClient<GitHubActivityClient>(c =>
{
    c.BaseAddress = new Uri("https://api.github.com/");
    c.Timeout = TimeSpan.FromSeconds(60);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("MudMaps/1.0");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
});
builder.Services.AddHostedService<MapRefreshService>();
builder.Services.Configure<ResearchOptions>(builder.Configuration.GetSection(ResearchOptions.Section));
builder.Services.AddSingleton<UpstreamGate>();
builder.Services.AddSingleton<GraphifyUpstream>();
builder.Services.AddSingleton<GraphifyHost>();
builder.Services.AddSingleton<IGraphifyHost>(sp => sp.GetRequiredService<GraphifyHost>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<GraphifyHost>());
builder.Services.AddSingleton(sp => new ToolCatalog(sp.GetRequiredService<IOptions<MapsOptions>>().Value.Repos.Select(r => r.FullName)));
builder.Services.AddSingleton<ResearchLimits>();
builder.Services.AddSingleton<RepoGuard>();
builder.Services.AddSingleton(sp =>
{
    var m = sp.GetRequiredService<IOptions<MapsOptions>>().Value;
    // The token is read once and held in memory only to recognise it in graphify's output.
    return new OutputScrubber(m.ReposRoot, sp.GetRequiredService<IOptions<ResearchOptions>>().Value.MergedGraph,
        m.Repos.Select(r => r.FullName), MapRefreshService.ReadToken(m));
});
builder.Services.AddSingleton<PrTools>();
builder.Services.AddSingleton<LinkExtractor>();
builder.Services.AddSingleton<ResearchGateway>();
// The loopback hop: no proxy, no default timeout (each call sets its own), nothing but the headers it is given.
// The no-output propagator stops .NET copying a caller's traceparent/baggage into the graphify request (C6).
builder.Services.AddHttpClient(GraphifyUpstream.ClientName, c => c.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        ActivityHeadersPropagator = System.Diagnostics.DistributedContextPropagator.CreateNoOutputPropagator(),
    })
    .RemoveAllLoggers();
builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 1024;
    o.EnableDetailedErrors = false;
});
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.MimeTypes = ["application/json", "text/json"];
    // Registering a provider replaces the defaults, so gzip is listed explicitly next to Brotli.
    // Optimal (about 5 ms for the map) is far smaller than Fastest; Brotli SmallestSize costs ~0.8 s a request.
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Optimal);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Optimal);
builder.Services.AddSingleton(sp =>
{
    var m = sp.GetRequiredService<IOptions<MapsOptions>>().Value;
    return new SecurityHeaders(m.Sites, m.FrameAncestors);
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // The limiter runs before the middleware that sets the security headers, so a rejection sets them itself, with a
    // Retry-After from the window (or 60 s when the limiter gives none, as a concurrency limit does).
    o.OnRejected = (rejected, _) =>
    {
        var ctx = rejected.HttpContext;
        ctx.RequestServices.GetRequiredService<SecurityHeaders>().Apply(ctx);
        ctx.Response.Headers.RetryAfter = (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after)
            ? Math.Max(1, (int)Math.Ceiling(after.TotalSeconds))
            : 60).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ValueTask.CompletedTask;
    };
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api/map"))
        {
            // Map polling: about 30 map requests a minute per client.
            // Every limited partition key carries its limiter's prefix: partitions are cached by key, so two limiters
            // sharing one key would share whichever limiter was created first.
            return RateLimitPartition.GetFixedWindowLimiter($"map:{ClientKeys.Of(ctx)}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
        }
        if (ctx.Request.Path.StartsWithSegments("/api/status"))
        {
            // Tiny and polled by every open footer once a minute; the limit is generous so several tabs never trip it.
            return RateLimitPartition.GetFixedWindowLimiter($"status:{ClientKeys.Of(ctx)}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
        }
        if (ctx.Request.Path.StartsWithSegments("/api/stats"))
        {
            // The counters: each read builds the document and may write the file. The weekly report reads
            // it once; 30 a minute per client is far above that. Who may reach it at all is a deployment decision, not this limit's.
            return RateLimitPartition.GetFixedWindowLimiter($"stats:{ClientKeys.Of(ctx)}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
        }
        if (ctx.Request.Path.StartsWithSegments("/api/track"))
        {
            // Client-side events (chip clicks, split-button picks): fire-and-forget from the app, so a page can send a
            // burst. 60 a minute per client is well past what one person clicking around produces.
            return RateLimitPartition.GetFixedWindowLimiter($"track:{ClientKeys.Of(ctx)}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
        }
        if (ctx.Request.Path.StartsWithSegments("/api/research/prs") || ctx.Request.Path.StartsWithSegments("/api/research/pr"))
        {
            // The PR board: cache reads only, 60 a minute per research client.
            return RateLimitPartition.GetFixedWindowLimiter($"research-prs:{ClientKeys.Research(ctx)}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = ctx.RequestServices.GetRequiredService<IOptions<ResearchOptions>>().Value.PrReadsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
        }
        if (ctx.Request.Path.StartsWithSegments("/hubs/map"))
        {
            var key = ClientKeys.Of(ctx);
            // A new connection starts with a negotiate POST: about 10 connects a minute per client.
            if (HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path.Value!.EndsWith("/negotiate", StringComparison.OrdinalIgnoreCase))
            {
                return RateLimitPartition.GetFixedWindowLimiter($"hub-connect:{key}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            }
            // The socket (or long-poll) request itself holds a permit for as long as it is open: at most 5 at once per client.
            return RateLimitPartition.GetConcurrencyLimiter($"hub-open:{key}", _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = 5,
                QueueLimit = 0,
            });
        }
        // Static files and everything else are not limited by .NET here; nginx/Cloudflare handle them in production.
        // One constant key: nothing is counted here, so there is nothing to keep apart per client.
        return RateLimitPartition.GetNoLimiter("unlimited");
    });
});

var app = builder.Build();
var maps = app.Services.GetRequiredService<IOptions<MapsOptions>>().Value;
var sites = maps.Sites;
// The apps the UI offers; Colony and Islands are still served, by direct URL only.
var offered = AppPreference.Offered(sites.Keys);
var headers = app.Services.GetRequiredService<SecurityHeaders>();
var researchOptions = app.Services.GetRequiredService<IOptions<ResearchOptions>>().Value;
var usage = app.Services.GetRequiredService<UsageCounters>();
app.Lifetime.ApplicationStopping.Register(() => usage.Flush(force: true));
if (researchOptions.Enabled) app.Services.GetRequiredService<RepoGuard>().CheckAll();

// Behind the proxy at e.g. /mudmap; the apps find the service relative to their own base, so nothing else changes.
if (!string.IsNullOrEmpty(maps.PathBase)) app.UsePathBase(maps.PathBase);
app.UseResponseCompression();
app.UseRateLimiter();
app.Use(async (ctx, next) =>
{
    var mcp = ctx.Request.Path.Equals("/mcp", StringComparison.OrdinalIgnoreCase);
    var researchPath = mcp || ctx.Request.Path.StartsWithSegments("/api/research");
    if (researchPath && !researchOptions.Enabled)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    // Only the two research endpoints take a POST; GET and DELETE /mcp stay 405 (stateless: no stream, no session).
    var researchPost = HttpMethods.IsPost(ctx.Request.Method)
        && (mcp || ctx.Request.Path.Equals("/api/research", StringComparison.OrdinalIgnoreCase));
    // The usage tracker also takes a POST, and works whether or not research is on.
    var trackPost = HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path.Equals("/api/track", StringComparison.OrdinalIgnoreCase);
    if ((mcp && !researchPost)
        || (!researchPost && !trackPost && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !ctx.Request.Path.StartsWithSegments("/hubs")))
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
    // Usage counters: count the request being served, not the redirect that got here first.
    if (UsageCounters.Classify(ctx.Request.Method, ctx.Request.Path.Value ?? "", sites.Keys) is { } bucket)
    {
        usage.Increment(bucket.Kind, bucket.Key);
        // And remember which app this was. The chooser and the Connect page are not apps, so they never write one.
        // Only an offered app is remembered: a direct visit to Colony or Islands leaves a saved choice alone.
        if (bucket is { Kind: "views" } view && offered.Contains(view.Key, StringComparer.Ordinal)) AppPreference.Set(ctx.Response, view.Key);
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

// When the data was last confirmed current. Separate from /api/map so the map's ETag does not move with it.
app.MapGet("/api/status", (HttpContext ctx, StatusReport status) =>
{
    ctx.Response.Headers.CacheControl = "no-cache";
    return Results.Json(status.Build(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
});

// The limits the research gateway actually enforces, so the Connect-via-MCP page shows the deployment instead of a
// copy that can drift (read from config, not hard-coded). 404 while research is off, as the rest is.
app.MapGet("/api/research/limits", (HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    return Results.Json(new
    {
        clientCallsPerMinute = researchOptions.ClientCallsPerMinute,
        clientConcurrency = researchOptions.ClientConcurrency,
        globalCallsPerMinute = researchOptions.GlobalCallsPerMinute,
        upstreamQueue = researchOptions.UpstreamQueue,
        triageCallsPerMinute = researchOptions.TriageCallsPerMinute,
        prReadsPerMinute = researchOptions.PrReadsPerMinute,
        upstreamTimeoutSeconds = researchOptions.UpstreamTimeoutSeconds,
        overallTimeoutSeconds = researchOptions.OverallTimeoutSeconds,
        maxRequestBytes = researchOptions.MaxRequestBytes,
        maxResponseBytes = researchOptions.MaxResponseBytes,
        maxContexts = researchOptions.MaxContexts,
        rssLimitMiB = researchOptions.RssLimitMiB,
        // The MCP server's own version (what initialize reports), not the map build version /api/health serves.
        serverVersion = ResearchGateway.ServerVersion,
    }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
});

app.MapGet("/api/health", (MapState state, TimeProvider clock, IGraphifyHost graphify) =>
{
    var research = graphify.State.ToString().ToLowerInvariant();
    return state.Current is { } m
        ? Results.Ok(new
        {
            ready = true, version = m.Version, builtAt = m.BuiltAt, areas = m.Areas, rooms = m.Rooms, prsLive = m.PrsLive,
            tokenExpiresAt = state.TokenExpiresAt?.ToString("yyyy-MM-dd"), tokenExpiringSoon = state.TokenExpiringSoon(clock.GetUtcNow()),
            research,
        })
        : Results.Ok(new { ready = false, research });
});

if (researchOptions.Enabled)
{
    // Kestrel's 4 KiB body limit is raised for these two endpoints only; the gateway enforces the 16 KiB cap itself too.
    var bodyLimit = new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(researchOptions.MaxRequestBytes);
    app.MapPost("/mcp", (HttpContext ctx, ResearchGateway gateway) => gateway.HandleMcpAsync(ctx)).WithMetadata(bodyLimit);
    app.MapPost("/api/research", (HttpContext ctx, ResearchGateway gateway) => gateway.HandleResearchAsync(ctx)).WithMetadata(bodyLimit);
    app.MapGet("/api/research/prs", (HttpContext ctx, PrTools prs, IOptions<MapsOptions> options) =>
    {
        ctx.Response.Headers.CacheControl = "no-store";
        var repos = ctx.Request.Query["repo"].Select(r => r ?? "").ToList();
        if (repos.Any(r => !options.Value.Repos.Any(o => o.FullName == r))) return Results.BadRequest(new { error = "unknown repo" });
        bool? actionable = ctx.Request.Query["actionable"].ToString() switch { "true" => true, "false" => false, _ => null };
        if (actionable is null && ctx.Request.Query["actionable"].ToString() != "") return Results.BadRequest(new { error = "actionable must be true or false" });
        return Results.Json(prs.List(repos, actionable));
    });
    app.MapGet("/api/research/pr", (HttpContext ctx, PrTools prs, IOptions<MapsOptions> options) =>
    {
        ctx.Response.Headers.CacheControl = "no-store";
        var repo = ctx.Request.Query["repo"].ToString();
        if (!options.Value.Repos.Any(o => o.FullName == repo)) return Results.BadRequest(new { error = "unknown repo" });
        if (!int.TryParse(ctx.Request.Query["number"], out var number) || number < 1) return Results.BadRequest(new { error = "number must be a positive integer" });
        return prs.Detail(repo, number) is { } item ? Results.Json(item) : Results.NotFound(new { error = PrTools.NotCached(number) });
    });
    // Test hook: crash the child on purpose. Registered only in Development; elsewhere the path is a plain 404 (C10).
    if (app.Environment.IsDevelopment())
    {
        app.MapGet("/api/research/_dev/kill", (IGraphifyHost graphify) =>
        {
            graphify.KillForTest();
            return Results.Ok(new { killed = true });
        });
    }
}

// The counters themselves: one small JSON document of counts per day, no addresses, no identities.
// The weekly report reads this; it is never cached so a report never reuses yesterday's snapshot.
app.MapGet("/api/stats", (HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    usage.Flush();
    return Results.Json(usage.Build(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
});

// The events a server request cannot reveal on its own — an MCP chip click or a Space | City pick makes no other
// call. Closed allow-list: an unknown name is a 400, so a client cannot mint new buckets.
app.MapPost("/api/track", async (HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    // The same check as the research gateway: another site can make its visitors POST text/plain, never JSON.
    if (!ResearchGateway.IsJsonContentType(ctx.Request.ContentType))
        return Results.Json(new { error = "Content-Type must be application/json" }, statusCode: StatusCodes.Status415UnsupportedMediaType);
    string? eventName = null;
    try
    {
        using var body = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        if (body.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var prop in body.RootElement.EnumerateObject())
            {
                if (prop.Name.Equals("event", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    eventName = prop.Value.GetString();
                    break;
                }
            }
        }
    }
    catch (System.Text.Json.JsonException)
    {
        // reported as 400 below
    }
    return usage.Track(eventName ?? "")
        ? Results.NoContent()
        : Results.BadRequest(new { error = "unknown event" });
});

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
    // Back to the app they last opened; /apps is the full list whenever they want the other one.
    if (AppPreference.Read(ctx.Request, offered) is { } saved)
        return Results.Redirect(ctx.Request.PathBase + "/" + saved + "/" + ctx.Request.QueryString);
    return Chooser();
});

// The full list, even for someone with a saved choice. Nothing else links here; it is the way out of the redirect.
app.MapGet("/apps", () => Chooser());

// The chooser page, counted here where it is actually rendered: a redirect to a saved app is not a chooser view.
IResult Chooser()
{
    usage.Increment("views", "chooser");
    return Results.Content(LandingPage.Render(offered), "text/html; charset=utf-8");
}

app.Run();

public partial class Program;
