using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MudMaps.Server;

/// <summary>Where the usage counters live and how often they are written out.</summary>
public sealed class UsageOptions
{
    public const string Section = "Usage";

    /// <summary>The JSON file the counters are flushed to. The container mounts a writable path here; elsewhere a
    /// non-writable path simply keeps the counters in memory.</summary>
    public string StateFile { get; set; } = "/var/lib/mudmaps/stats.json";

    /// <summary>Minimum seconds between writes, so a busy day costs one small write a minute at most.</summary>
    public int FlushSeconds { get; set; } = 60;
}

/// <summary>
/// Usage counters: whether each feature is actually used.
/// Counts only, never identities: buckets are day (UTC) → kind → key. In memory, written to a small JSON file at most
/// once a minute and on shutdown. No database, no analytics package, no IP address is ever stored or reported.
/// </summary>
public sealed partial class UsageCounters
{
    /// <summary>The event names <c>POST /api/track</c> accepts. Anything else is a 400 — a closed list keeps the file
    /// small and stops a client inventing a new bucket per click. Each one has a caller in the hero nav
    /// (<c>MudMaps.Shared.UsageEvents</c>); a test keeps the two lists equal. <c>nav_colony</c> and <c>nav_islands</c>
    /// left with the Colony and Islands buttons: their old counts in a state file are dropped on load,
    /// like any other bucket this server can no longer write.</summary>
    public static readonly IReadOnlySet<string> TrackEvents = new HashSet<string>(StringComparer.Ordinal)
    {
        "mcp_chip_click", "nav_space", "nav_city",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, int>>> _days = new(StringComparer.Ordinal);
    private readonly UsageOptions _options;
    private readonly HashSet<string> _sites;
    private readonly TimeProvider _clock;
    private readonly ILogger<UsageCounters> _log;
    private DateTimeOffset _lastFlush = DateTimeOffset.MinValue;
    private bool _dirty;
    private bool _writeFailed;

    public UsageCounters(UsageOptions options, TimeProvider clock, ILogger<UsageCounters> log, IReadOnlyCollection<string> sites)
    {
        _sites = new HashSet<string>(sites, StringComparer.Ordinal);
        _options = options;
        _clock = clock;
        _log = log;
        Load();
    }

    /// <summary>Adds one to a bucket for today (UTC).</summary>
    public void Increment(string kind, string key, int by = 1)
    {
        if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(key) || by <= 0) return;
        lock (_gate)
        {
            var day = _clock.GetUtcNow().ToString("yyyy-MM-dd");
            if (!_days.TryGetValue(day, out var kinds)) _days[day] = kinds = new(StringComparer.Ordinal);
            if (!kinds.TryGetValue(kind, out var keys)) kinds[kind] = keys = new(StringComparer.Ordinal);
            keys[key] = keys.TryGetValue(key, out var n) ? n + by : by;
            _dirty = true;
        }
    }

    /// <summary>Counts a client-side event from <c>POST /api/track</c>; false when the name is not on the allow-list.</summary>
    public bool Track(string eventName)
    {
        if (!TrackEvents.Contains(eventName ?? "")) return false;
        Increment("ui", eventName!);
        return true;
    }

    /// <summary>The counters as they are served by <c>GET /api/stats</c>: <c>generatedAt</c> plus each day's buckets.</summary>
    public JsonObject Build()
    {
        lock (_gate)
        {
            var days = new JsonObject();
            foreach (var day in _days.Keys.OrderBy(d => d, StringComparer.Ordinal))
            {
                var kinds = new JsonObject();
                foreach (var kind in _days[day].OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    var keys = new JsonObject();
                    foreach (var key in kind.Value.OrderBy(k => k.Key, StringComparer.Ordinal)) keys[key.Key] = key.Value;
                    kinds[kind.Key] = keys;
                }
                days[day] = kinds;
            }
            return new JsonObject { ["generatedAt"] = _clock.GetUtcNow().ToString("O"), ["days"] = days };
        }
    }

    /// <summary>Writes the counters out, at most once every <see cref="UsageOptions.FlushSeconds"/> seconds unless forced.
    /// The snapshot is taken under the counter lock and written outside it, so a slow disk never blocks
    /// <see cref="Increment"/>. A path that cannot be written is logged once and the counters stay in memory.</summary>
    public bool Flush(bool force = false)
    {
        // One writer at a time, so two flushes never race on the temp file.
        lock (_writeGate)
        {
            string snapshot;
            DateTimeOffset now;
            lock (_gate)
            {
                if (!_dirty) return false;
                now = _clock.GetUtcNow();
                if (!force && now - _lastFlush < TimeSpan.FromSeconds(Math.Max(0, _options.FlushSeconds))) return false;
                if (string.IsNullOrWhiteSpace(_options.StateFile)) return false;
                TrimDays();
                snapshot = Build().ToJsonString(Json);
                _dirty = false;
            }
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(_options.StateFile));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var temp = _options.StateFile + ".tmp";
                File.WriteAllText(temp, snapshot);
                File.Move(temp, _options.StateFile, overwrite: true);
                lock (_gate) _lastFlush = now;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still unwritten: the next tick tries again, and the warning is logged only once.
                lock (_gate) _dirty = true;
                if (!_writeFailed) _log.LogWarning("Usage counters are not persisting to {File}: {Reason}", _options.StateFile, ex.Message);
                _writeFailed = true;
                return false;
            }
        }
    }

    /// <summary>The largest state file read back; a year of counts is a few tens of KiB.</summary>
    public const int MaxStateBytes = 1024 * 1024;

    /// <summary>Days kept, in memory and on disk: a little over a year.</summary>
    public const int MaxDays = 400;

    /// <summary>The <c>api</c> keys <see cref="Classify"/> can produce.</summary>
    public static readonly IReadOnlySet<string> ApiKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "track", "research_prs", "research_pr", "research_limits", "research", "map", "status", "health", "stats",
    };

    /// <summary>The non-app <c>views</c> keys: the chooser and the Connect-via-MCP page.</summary>
    private static readonly IReadOnlySet<string> PageViews = new HashSet<string>(StringComparer.Ordinal) { "chooser", "connect" };

    /// <summary>Whether this server could have written the bucket itself — the only buckets a state file may bring back
    /// (the file's folder is writable by the graphify child, and its keys are republished on /api/stats).</summary>
    private bool IsKnownBucket(string kind, string key) => kind switch
    {
        "views" => _sites.Contains(key) || PageViews.Contains(key),
        "api" => ApiKeys.Contains(key),
        "ui" => TrackEvents.Contains(key),
        "mcp" => Research.ToolCatalog.GraphTools.Contains(key) || Research.ToolCatalog.PrTools.Contains(key),
        _ => false,
    };

    private static bool IsDay(string day) =>
        DateOnly.TryParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);

    /// <summary>Drops all but the newest <see cref="MaxDays"/> days. Called under <c>_gate</c>.</summary>
    private void TrimDays()
    {
        if (_days.Count <= MaxDays) return;
        foreach (var old in _days.Keys.OrderBy(d => d, StringComparer.Ordinal).Take(_days.Count - MaxDays).ToList()) _days.Remove(old);
    }

    /// <summary>Reads a previous snapshot back in on start, so counts survive a restart. A damaged or oversized file is
    /// ignored; from a readable one only known buckets, real days and positive whole counts are kept.</summary>
    private void Load()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_options.StateFile) || !File.Exists(_options.StateFile)) return;
            if (!IsRegularFile(_options.StateFile))
            {
                _log.LogWarning("Ignoring usage state at {File}: not a regular file", _options.StateFile);
                return;
            }
            byte[] bytes;
            using (var stream = new FileStream(_options.StateFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // Size first, before reading anything; and the read itself stops one byte past the cap, in case the
                // file grows in between.
                if (stream.Length > MaxStateBytes) { IgnoreOversized(); return; }
                bytes = new byte[MaxStateBytes + 1];
                var read = 0;
                while (read < bytes.Length && stream.Read(bytes, read, bytes.Length - read) is var n and > 0) read += n;
                if (read > MaxStateBytes) { IgnoreOversized(); return; }
                Array.Resize(ref bytes, read);
            }
            var root = JsonNode.Parse(bytes) as JsonObject;
            if (root?["days"] is not JsonObject days) return;
            foreach (var day in days)
            {
                if (!IsDay(day.Key) || day.Value is not JsonObject kinds) continue;
                var perDay = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
                foreach (var kind in kinds)
                {
                    if (kind.Value is not JsonObject keys) continue;
                    var perKind = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var key in keys)
                    {
                        // A JSON number that is a whole, positive int: no strings, fractions, negatives or overflow.
                        if (IsKnownBucket(kind.Key, key.Key) && key.Value is JsonValue v && v.GetValueKind() == JsonValueKind.Number
                            && v.TryGetValue<int>(out var n) && n > 0)
                            perKind[key.Key] = n;
                    }
                    if (perKind.Count > 0) perDay[kind.Key] = perKind;
                }
                if (perDay.Count > 0) _days[day.Key] = perDay;
            }
            TrimDays();
        }
        catch (Exception ex)
        {
            // Anything a bad file can throw while it is parsed or walked (a duplicate name is an ArgumentException
            // during enumeration) means start empty; counters are not worth failing startup over.
            _log.LogWarning("Ignoring unreadable usage state at {File}: {Reason}", _options.StateFile, ex.Message);
            _days.Clear();
        }
    }

    /// <summary>The state path must be a plain file, checked before it is opened. A symlink could point
    /// the read elsewhere; a FIFO would block startup in the open. File.Exists says yes to both.</summary>
    private static bool IsRegularFile(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return false;
        // Managed APIs cannot tell a FIFO, socket or device from a file without opening it; on Linux statx can.
        return !OperatingSystem.IsLinux() || LinuxFileType(path) == SIfReg;
    }

    private const int AtFdCwd = -100, AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x1;
    private const int SIfMt = 0xF000, SIfReg = 0x8000;

    /// <summary>The S_IFMT bits of <paramref name="path"/> without following a link, or -1 if statx fails.
    /// struct statx is the same on every Linux architecture: stx_mode is the u16 at offset 28 of 256 bytes.</summary>
    private static unsafe int LinuxFileType(string path)
    {
        var buffer = stackalloc byte[256];
        if (Statx(AtFdCwd, path, AtSymlinkNoFollow, StatxType, buffer) != 0) return -1;
        return *(ushort*)(buffer + 28) & SIfMt;
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int Statx(int dirfd, string path, int flags, uint mask, byte* buffer);

    private void IgnoreOversized() =>
        _log.LogWarning("Ignoring usage state at {File}: larger than {Max} bytes", _options.StateFile, MaxStateBytes);

    /// <summary>
    /// Which bucket a request belongs in, or null when it is not counted. A page view is a <em>document</em> request —
    /// the segment root or its index.html — not the dozen assets a WASM app fetches after it. <c>/mcp</c> is not
    /// counted here either: the gateway counts it per tool, which is the number that matters. Nor is the chooser: the
    /// <c>/</c> and <c>/apps</c> endpoints count it only when they render it, since <c>/</c> redirects a returning visitor.
    /// </summary>
    public static (string Kind, string Key)? Classify(string method, string path, IReadOnlyCollection<string> siteSegments)
    {
        var get = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        if (path.Length == 0) return null;

        if (path == "/mcp") return null;

        if (path.StartsWith("/api/", StringComparison.Ordinal))
        {
            if (HttpMethods.IsPost(method) && path.StartsWith("/api/track", StringComparison.Ordinal)) return ("api", "track");
            if (!get) return null;
            if (path.StartsWith("/api/research/prs", StringComparison.Ordinal)) return ("api", "research_prs");
            if (path.StartsWith("/api/research/pr", StringComparison.Ordinal)) return ("api", "research_pr");
            if (path.StartsWith("/api/research/limits", StringComparison.Ordinal)) return ("api", "research_limits");
            if (path.StartsWith("/api/research", StringComparison.Ordinal)) return ("api", "research");
            if (path.StartsWith("/api/map", StringComparison.Ordinal)) return ("api", "map");
            if (path.StartsWith("/api/status", StringComparison.Ordinal)) return ("api", "status");
            if (path.StartsWith("/api/health", StringComparison.Ordinal)) return ("api", "health");
            if (path.StartsWith("/api/stats", StringComparison.Ordinal)) return ("api", "stats");
            return null;
        }

        if (!get) return null;
        var segment = path.Trim('/').Split('/', 2)[0];
        if (!siteSegments.Contains(segment, StringComparer.Ordinal)) return null;
        var rest = path.Length > segment.Length + 2 ? path[(segment.Length + 2)..] : "";
        if (rest is "connect" or "connect/") return ("views", "connect"); // the Connect-via-MCP page, under any app
        return rest is "" or "index.html" ? ("views", segment) : null;
    }
}

/// <summary>Writes the counters out every <see cref="UsageOptions.FlushSeconds"/> seconds, so a hard kill, OOM or reboot
/// loses at most that much — not everything since the last <c>/api/stats</c> read.</summary>
public sealed class UsageFlushService(UsageCounters counters, UsageOptions options, TimeProvider clock) : BackgroundService
{
    private PeriodicTimer? _timer;

    /// <summary>The timer starts here, not in <see cref="ExecuteAsync"/> (which the host runs on the thread pool), so the
    /// first tick is exactly FlushSeconds after start.</summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.FlushSeconds)), clock);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _timer!.WaitForNextTickAsync(stoppingToken)) counters.Flush();
        }
        catch (OperationCanceledException)
        {
            // shutting down; ApplicationStopping does the final forced flush
        }
    }

    public override void Dispose()
    {
        _timer?.Dispose();
        base.Dispose();
    }
}
