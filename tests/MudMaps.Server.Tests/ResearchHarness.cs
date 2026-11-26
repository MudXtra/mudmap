using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MudMaps.Server.Maps;
using MudMaps.Server.Research;

namespace MudMaps.Server.Tests;

/// <summary>
/// A test server with research enabled and the fake graphify child (fakes/fake_graphify.py) on a free port. The child
/// appends every start and request to <see cref="LogFile"/>; <see cref="Events"/> reads them back.
/// </summary>
public sealed class ResearchHarness : IAsyncDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("mudmaps-research-").FullName;
    public string LogFile => Path.Combine(Dir, "child.jsonl");
    public string MergedGraph => Path.Combine(Dir, "merged", "graph.json");
    public string ReposRoot => Path.Combine(Dir, "repos");
    public int Port { get; } = FreePort();
    public ListLoggerProvider Logs { get; } = new();
    public WebApplicationFactory<Program> App { get; }

    /// <summary>The default 15 (a configured list would be appended to them by the binder, so the tests use the defaults).</summary>
    public static readonly string[] Repos = new MapsOptions().Repos.Select(r => r.FullName).ToArray();

    public ResearchHarness(Dictionary<string, string?>? settings = null, string[]? childArgs = null, string environment = "Development",
        Action<IServiceCollection>? services = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MergedGraph)!);
        File.WriteAllText(MergedGraph, "{}");
        foreach (var repo in Repos)
        {
            Directory.CreateDirectory(Path.Combine(ReposRoot, repo, "graphify-out"));
            File.WriteAllText(Path.Combine(ReposRoot, repo, "graphify-out", "graph.json"), "{}");
        }
        var all = new Dictionary<string, string?>
        {
            ["Logging:LogLevel:MudMaps.Server.Research"] = "Debug",
            ["Maps:ReposRoot"] = ReposRoot,
            ["Maps:Sites:colony"] = "/nonexistent",
            ["Maps:Research:Enabled"] = "true",
            ["Maps:Research:Python"] = "/usr/bin/python3",
            ["Maps:Research:Launcher"] = Path.Combine(AppContext.BaseDirectory, "fakes", "fake_graphify.py"),
            ["Maps:Research:Port"] = Port.ToString(),
            ["Maps:Research:MergedGraph"] = MergedGraph,
            ["Maps:Research:TempDir"] = Path.Combine(Dir, "tmp"),
            ["Maps:Research:ChildPath"] = "/usr/bin",
            ["Maps:Research:StartupProbeIntervalSeconds"] = "0.1",
            ["Maps:Research:ProbeIntervalSeconds"] = "0.3",
            ["Maps:Research:ProbeTimeoutSeconds"] = "1",
            ["Maps:Research:StopGraceSeconds"] = "2",
            ["Maps:Research:RestartBackoffSeconds:0"] = "0.2",
            ["Maps:Research:RestartBackoffSeconds:1"] = "0.4",
            ["Maps:Research:RestartBackoffSeconds:2"] = "0.8",
            ["Maps:Research:RestartBackoffSeconds:3"] = "1.6",
        };
        string[] args = ["--log", LogFile, .. childArgs ?? []];
        for (var i = 0; i < args.Length; i++) all[$"Maps:Research:ChildArgs:{i}"] = args[i];
        foreach (var (k, v) in settings ?? []) all[k] = v;

        App = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            foreach (var (k, v) in all) b.UseSetting(k, v);
            b.ConfigureLogging(l => l.AddProvider(Logs).SetMinimumLevel(LogLevel.Debug));
            b.ConfigureServices(s =>
            {
                foreach (var d in s.Where(d => d.ImplementationType == typeof(MapRefreshService)).ToList()) s.Remove(d);
                services?.Invoke(s);
            });
        });
    }

    public HttpClient Client(string? clientIp = null)
    {
        var c = App.CreateClient();
        if (clientIp is not null) c.DefaultRequestHeaders.Add("CF-Connecting-IP", clientIp);
        return c;
    }

    public IGraphifyHost Host => App.Services.GetRequiredService<IGraphifyHost>();

    public async Task WaitReadyAsync(int seconds = 20) =>
        await WaitUntilAsync(() => Host.State == ResearchState.Ready, seconds, "child never became ready");

    public static async Task WaitUntilAsync(Func<bool> condition, double seconds, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException(what);
            await Task.Delay(50);
        }
    }

    public List<JsonObject> Events(string? kind = null)
    {
        if (!File.Exists(LogFile)) return [];
        // The child may be mid-write; tolerate a torn last line.
        var list = new List<JsonObject>();
        foreach (var line in File.ReadAllLines(LogFile))
        {
            try { if (JsonNode.Parse(line) is JsonObject o && (kind is null || o["event"]!.GetValue<string>() == kind)) list.Add(o); }
            catch (System.Text.Json.JsonException) { }
        }
        return list;
    }

    /// <summary>tools/call requests the child received, as parsed bodies.</summary>
    public List<JsonObject> ToolCalls() => Events("request")
        .Select(e => JsonNode.Parse(e["body"]!.GetValue<string>())!.AsObject())
        .Where(b => b["method"]?.GetValue<string>() == "tools/call")
        .ToList();

    public static int FreePort()
    {
        while (true)
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            if (port != 8841) return port;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public sealed class ListLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Category, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new ListLogger(this, categoryName);

    public bool Has(LogLevel level, string fragment) => Entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));

    public string All => string.Join('\n', Entries.Select(e => $"{e.Level} {e.Category}: {e.Message}"));

    public void Dispose() { }

    private sealed class ListLogger(ListLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Enqueue((logLevel, category, formatter(state, exception)));
    }
}
