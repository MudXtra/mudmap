using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MudMaps.Server;

/// <summary>Response headers for every request, with a CSP that allows each site's own import map and nothing else inline.</summary>
public sealed partial class SecurityHeaders(IReadOnlyDictionary<string, string> sites, IEnumerable<string>? frameAncestors = null)
{
    // Only https origins with a plain host are accepted, so a config typo cannot open framing to everyone.
    private readonly string BaseCsp =
        "default-src 'self'; base-uri 'self'; object-src 'none'; form-action 'none'; " +
        $"frame-ancestors {FrameAncestorsValue(frameAncestors)}; " +
        "img-src 'self' data: https://avatars.githubusercontent.com; connect-src 'self'; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://api.fontshare.com; " +
        "font-src 'self' https://fonts.gstatic.com https://cdn.fontshare.com; ";

    [GeneratedRegex(@"^https://[a-z0-9.-]+$")]
    private static partial Regex HttpsOrigin();

    private static string FrameAncestorsValue(IEnumerable<string>? origins)
    {
        var allowed = (origins ?? []).Where(o => HttpsOrigin().IsMatch(o)).ToList();
        return string.Join(' ', allowed.Prepend("'self'"));
    }

    private readonly ConcurrentDictionary<string, (DateTime Stamp, string Csp)> _cache = new();

    [GeneratedRegex(@"<script type=""importmap"">(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex ImportMap();

    /// <summary>
    /// The CSP for a site: self, WebAssembly compilation, and the hash of its inline import map.
    /// The import map changes with every publish, so the hash follows index.html's write time.
    /// </summary>
    public string CspFor(string? segment)
    {
        if (segment is null || !sites.TryGetValue(segment, out var folder)) return BaseCsp + "script-src 'none';";
        var index = Path.Combine(folder, "index.html");
        var stamp = File.Exists(index) ? File.GetLastWriteTimeUtc(index) : DateTime.MinValue;
        if (_cache.TryGetValue(segment, out var cached) && cached.Stamp == stamp) return cached.Csp;
        var hashes = new StringBuilder();
        if (File.Exists(index))
        {
            foreach (Match m in ImportMap().Matches(File.ReadAllText(index)))
            {
                hashes.Append($" 'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(m.Groups[1].Value)))}'");
            }
        }
        var csp = BaseCsp + $"script-src 'self' 'wasm-unsafe-eval'{hashes};";
        _cache[segment] = (stamp, csp);
        return csp;
    }

    public void Apply(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        // Framing is governed by the CSP frame-ancestors list; X-Frame-Options cannot name other origins.
        h["Referrer-Policy"] = "no-referrer";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        h.ContentSecurityPolicy = CspFor(ctx.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
        ApplyCacheControl(ctx);
    }

    /// <summary>
    /// Successful static and HTML responses: <c>no-cache</c> (revalidate every time) unless the file is fingerprinted,
    /// which may then be cached for a year. Set when the response starts so errors and redirects are never marked immutable.
    /// The map JSON sets its own <c>no-cache</c> with an ETag.
    /// </summary>
    private static void ApplyCacheControl(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        ctx.Response.OnStarting(() =>
        {
            var response = ctx.Response;
            if (response.Headers.CacheControl.Count == 0)
            {
                response.Headers.CacheControl = IsFingerprinted(path) && response.StatusCode is 200 or 206 or 304
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";
            }
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// True for anything under <c>_framework/</c> and for scripts or stylesheets whose name carries a build fingerprint,
    /// such as <c>city.l5utpqx140.js</c> (ten lower-case letters or digits before the extension).
    /// <c>zoom.js</c>, <c>mudmaps.css</c> and <c>MudBlazor.min.css</c> are not fingerprinted and are revalidated instead.
    /// </summary>
    internal static bool IsFingerprinted(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s.Equals("_framework", StringComparison.OrdinalIgnoreCase))) return true;
        return segments.Length > 0 && FingerprintedAsset().IsMatch(segments[^1]);
    }

    [GeneratedRegex(@"\.[a-z0-9]{10}\.(js|mjs|css)$", RegexOptions.IgnoreCase)]
    private static partial Regex FingerprintedAsset();
}
