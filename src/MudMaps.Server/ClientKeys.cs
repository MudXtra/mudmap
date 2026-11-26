using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MudMaps.Server;

/// <summary>Who is asking, for rate limits. Never used for authorization.</summary>
public static class ClientKeys
{
    /// <summary>
    /// Cloudflare's <c>CF-Connecting-IP</c> when there is exactly one short value (the proxy sets it), else the peer.
    /// </summary>
    public static string Of(HttpContext ctx) =>
        ctx.Request.Headers.TryGetValue("CF-Connecting-IP", out var ip) && ip.Count == 1 && ip[0]!.Length <= 64
            ? ip[0]!
            : ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// The research endpoints' key: <see cref="Of"/> with an IPv6 address cut to its /64, since one subscriber holds a
    /// whole /64 and could otherwise rotate addresses past the per-client limits. IPv4 stays as it is.
    /// </summary>
    public static string Research(HttpContext ctx) => Normalize(Of(ctx));

    public static string Normalize(string key)
    {
        if (!IPAddress.TryParse(key, out var ip)) return key;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return ip.ToString();
        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    /// <summary>
    /// The secret behind <see cref="LogHash(string)"/>: 32 random bytes made once per process, held in memory only and
    /// never logged, written or served. A restart makes a new one, so log hashes do not match across restarts.
    /// </summary>
    private static readonly byte[] LogSecret = RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// A short stand-in for a key in logs: the first 8 hex digits of its HMAC-SHA256 under the per-process secret.
    /// The same key gives the same hash until the server restarts. A plain hash of 8 hex digits could be reversed by
    /// hashing every IPv4 address; without the secret, which never leaves memory, there is nothing to try them against.
    /// </summary>
    public static string LogHash(string key) => LogHash(key, LogSecret);

    internal static string LogHash(string key, byte[] secret) =>
        Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(key)))[..8].ToLowerInvariant();
}
