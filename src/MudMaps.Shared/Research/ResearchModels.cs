using System.Text.Json;

namespace MudMaps.Shared.Research;

/// <summary>A place in the code a research result names, with where it is on the map and on GitHub.</summary>
/// <param name="Repo">The repository's full name, owner/name.</param>
public sealed record ResearchLink(string Label, string Repo, string File, int? Line, string? RoomId, string? Href, string? Github);

/// <summary>A room a result touches, and how many of the result's items sit in it.</summary>
public sealed record LensRoom(string Repo, string RoomId, int Count);

/// <summary>The service's answer to one research tool call (<c>POST api/research</c>).</summary>
/// <param name="Text">Graphify's own output, shown as text and never as markup.</param>
/// <param name="Mcp">The equivalent MCP tools/call request.</param>
public sealed record ResearchResponse(string Tool, string Text, bool IsError, IReadOnlyList<ResearchLink>? Links,
    IReadOnlyList<LensRoom>? Rooms, JsonElement? Mcp);

/// <summary>Another open pull request into the same base branch that changes some of the same files, and those files' rooms.</summary>
public sealed record PrConflict(int Number, IReadOnlyList<string> Rooms, IReadOnlyList<string>? RoomNames, IReadOnlyList<string>? Files = null);

/// <summary>An open pull request as the service caches it (<c>GET api/research/prs</c> and <c>api/research/pr</c>).</summary>
/// <param name="Repo">The repository's full name, owner/name.</param>
/// <param name="Ci">SUCCESS, FAILURE, PENDING or NONE.</param>
/// <param name="Review">APPROVED, CHANGES_REQUESTED, REVIEW_REQUIRED or empty.</param>
/// <param name="Status">READY, APPROVED, PENDING, STALE, DRAFT, CHANGES-REQ, CI-FAIL or WRONG-BASE.</param>
/// <param name="Files">Changed files, only on the single-PR answer.</param>
/// <param name="RoomNames">Names of <paramref name="Rooms"/>, only on the single-PR answer.</param>
public sealed record PrItem(string Repo, int Number, string Title, string Author, bool IsDraft, string Ci, string Review,
    string Status, string? Base, string? DefaultBase, double AgeDays, string? UpdatedAt, IReadOnlyList<string>? Labels,
    string? Mergeable, int FileCount, IReadOnlyList<string>? Rooms, IReadOnlyList<int>? Communities, int Nodes,
    string? BlastRadius, string? Url, IReadOnlyList<PrConflict>? ConflictsWith, IReadOnlyList<string>? Files = null,
    IReadOnlyList<string>? RoomNames = null)
{
    public IReadOnlyList<string> LabelList => Labels ?? [];
    public IReadOnlyList<string> RoomList => Rooms ?? [];
    public IReadOnlyList<PrConflict> Conflicts => ConflictsWith ?? [];
}

public sealed record PrList(string? GeneratedAt, IReadOnlyList<PrItem>? Prs);

/// <summary>
/// What came back from a research call: the response, or why there is none. <see cref="RetryAfter"/> is the number of
/// seconds the service asked to wait (429 and 503).
/// </summary>
public sealed record ResearchOutcome<T>(T? Value, int Status, string? Error, int? RetryAfter = null)
{
    public bool Ok => Value is not null && Error is null;
}
