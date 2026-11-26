using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MudMaps.Server.GitHub;

namespace MudMaps.Server.Tests;

public class OpenPrFetchTests
{
    private const string Repository = """
        {"data":{"repository":{"defaultBranchRef":{"name":"dev"},"pullRequests":{"nodes":[{"number":7,"title":"Fix","headRefName":"f",
        "baseRefName":"dev","isDraft":false,"reviewDecision":null,"updatedAt":"2026-11-20T00:00:00Z","mergeable":"MERGEABLE",
        "author":{"__typename":"User","login":"ann"},"labels":{"nodes":[]},"files":{"nodes":[{"path":"src/A.cs"}]},
        "commits":{"nodes":[{"commit":{"statusCheckRollup":{"state":"SUCCESS"}}}]}}]}}}}
        """;

    private sealed class Scripted(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            var status = statuses[Math.Min(Bodies.Count - 1, statuses.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(status == HttpStatusCode.OK ? Repository : "", Encoding.UTF8, "application/json") };
        }
    }

    private static GitHubActivityClient Client(Scripted handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") }, NullLogger<GitHubActivityClient>.Instance);

    /// <summary>GitHub's 502 on the heavy open-PR query is retried once with a lighter page, so a cold start still gets that repository's PRs.</summary>
    [Fact]
    public async Task A_5xx_is_retried_once_with_a_lighter_page()
    {
        var handler = new Scripted(HttpStatusCode.BadGateway, HttpStatusCode.OK);
        var prs = await Client(handler).GetOpenPullRequestsAsync("MudBlazor/MudBlazor", "t", DateTimeOffset.Parse("2026-11-23T00:00:00Z"), default);
        Assert.Equal(7, Assert.Single(prs.Prs).Number);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("\"first\":50", handler.Bodies[0]);
        Assert.Contains("\"first\":25", handler.Bodies[1]);
        Assert.Contains("\"files\":50", handler.Bodies[1]);
    }

    /// <summary>A client error is not retried: it would fail the same way and only spend quota.</summary>
    [Fact]
    public async Task A_4xx_is_not_retried()
    {
        var handler = new Scripted(HttpStatusCode.Unauthorized);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Client(handler).GetOpenPullRequestsAsync("MudBlazor/MudBlazor", "t", DateTimeOffset.UtcNow, default));
        Assert.Single(handler.Bodies);
    }
}
