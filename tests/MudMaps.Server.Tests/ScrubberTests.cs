using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MudMaps.Server.Research;

namespace MudMaps.Server.Tests;

public class ScrubberTests
{
    internal const string Root = "/home/user/repos";
    internal const string Merged = "/home/user/repos/_merged/mud/graph.json";
    internal static readonly string[] Repos = ["MudBlazor/MudBlazor", "MudBlazor/ThemeManager"];
    private static readonly Regex Leak = new("/data/|/mnt/|/home/|/root/|/opt/|/tmp/|InternalRepos");

    internal static JsonNode Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fakes", "fixtures", name)))!;

    private readonly OutputScrubber _scrub = new(Root, Merged, Repos, "s3cr3t-token-value");

    [Theory]
    [InlineData("query.json", "Graph: MudBlazor/ThemeManager (316 nodes)")]
    [InlineData("merged-query.json", "Graph: mud (merged) (143439 nodes)")]
    public void Real_graphify_headers_name_the_repo_or_the_merged_graph(string fixture, string header)
    {
        var json = Fixture(fixture);
        Assert.Matches(Leak, json.ToJsonString()); // the raw answer does leak the host path
        Assert.True(_scrub.Scrub(json));
        var text = json["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.StartsWith(header, text);
        Assert.DoesNotMatch(Leak, json.ToJsonString());
        // Repository-relative source paths are kept: they feed the links.
        Assert.Contains("src=src/", text);
    }

    [Fact]
    public void Every_string_is_scrubbed_including_error_message_and_data()
    {
        var json = JsonNode.Parse("""
            {"jsonrpc":"2.0","id":1,"error":{"code":-32603,"message":"graph.json not found: /data/InternalRepos/a/b/graph.json",
             "data":{"trace":["at /usr/lib/python3/x.py line 3","/home/user/x", "keep https://github.com/a/b"],
             "nested":{"p":"/proc/self/status","q":"/etc/passwd /srv/x /run/y /opt/z /tmp/w /root/v"}}}}
            """)!;
        Assert.True(_scrub.Scrub(json));
        var s = json.ToJsonString();
        Assert.True(!Leak.IsMatch(s), s);
        Assert.DoesNotContain("/proc/", s);
        Assert.DoesNotContain("/etc/", s);
        Assert.DoesNotContain("/usr/", s);
        Assert.Contains("keep https://github.com/a/b", s);
        // Relative paths that merely contain a root name are not paths under it.
        Assert.True(_scrub.Clean("src/data/x.cs and a/home/b", out var relative));
        Assert.Equal("src/data/x.cs and a/home/b", relative);
        Assert.Equal("graph.json not found: <path>", json["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void Repos_root_prefix_is_stripped_before_the_generic_redactor()
    {
        Assert.True(_scrub.Clean($"see {Root}/MudBlazor/MudBlazor/src/A.cs", out var cleaned));
        Assert.Equal("see MudBlazor/MudBlazor/src/A.cs", cleaned);
    }

    [Theory]
    [InlineData("contains s3cr3t-token-value inside")]
    [InlineData("ghp_abcdefghijklmnop")]
    [InlineData("github_pat_11AAAA")]
    [InlineData("gho_x")]
    [InlineData("ghs_x")]
    public void Token_shaped_text_is_refused(string text)
    {
        Assert.False(_scrub.Clean(text, out _));
        var json = new JsonObject { ["result"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["text"] = text }) } };
        Assert.False(_scrub.Scrub(json));
    }

    [Theory]
    [InlineData("2001:db8:1:2:aaaa::1", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:3::1", "2001:db8:1:3::/64")]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")]
    [InlineData("unknown", "unknown")]
    public void Research_key_cuts_ipv6_to_its_64(string address, string key) =>
        Assert.Equal(key, ClientKeys.Normalize(address));

    [Fact]
    public void Log_hash_is_stable_within_the_process()
    {
        var hash = ClientKeys.LogHash("203.0.113.9");
        Assert.Matches("^[0-9a-f]{8}$", hash);
        Assert.Equal(hash, ClientKeys.LogHash("203.0.113.9"));
        Assert.NotEqual(hash, ClientKeys.LogHash("203.0.113.10"));
    }

    [Theory]
    [InlineData("203.0.113.9")]
    [InlineData("2001:db8:1:2::/64")]
    public void Log_hash_is_not_a_plain_sha256_prefix(string key)
    {
        var plain = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..8]
            .ToLowerInvariant();
        Assert.NotEqual(plain, ClientKeys.LogHash(key));
    }

    [Fact]
    public void Log_hash_depends_on_the_secret()
    {
        var a = new byte[32];
        var b = new byte[32];
        b[0] = 1;
        Assert.Equal(ClientKeys.LogHash("203.0.113.9", a), ClientKeys.LogHash("203.0.113.9", a));
        Assert.NotEqual(ClientKeys.LogHash("203.0.113.9", a), ClientKeys.LogHash("203.0.113.9", b));
    }
}
