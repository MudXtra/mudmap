using Microsoft.Extensions.Options;

namespace MudMaps.Server.Research;

/// <summary>
/// Decides which configured repositories graphify may be pointed at. A repository is refused when any
/// step of <c>{ReposRoot}/{owner}/{repo}/graphify-out/graph.json</c> is missing or a symbolic link, or when the full
/// path leaves <c>{ReposRoot}/{owner}/{repo}</c>: graphify then never sees a path this service did not build itself.
/// </summary>
public sealed class RepoGuard(IOptions<MapsOptions> maps, ILogger<RepoGuard> log)
{
    private readonly string _root = Path.GetFullPath(maps.Value.ReposRoot).TrimEnd('/');

    /// <summary>The directory graphify gets as <c>project_path</c> for a configured repository.</summary>
    public string ProjectDir(string fullName) => Path.GetFullPath(Path.Combine(_root, fullName));

    public bool Available(string fullName)
    {
        if (!maps.Value.Repos.Any(r => r.FullName == fullName)) return false;
        var parts = fullName.Split('/');
        if (parts.Length != 2 || parts.Any(p => p is "" or "." or "..")) return Refuse(fullName, "is not owner/repo");
        var dir = ProjectDir(fullName);
        if (!dir.StartsWith(_root + "/", StringComparison.Ordinal) || dir != $"{_root}/{parts[0]}/{parts[1]}")
            return Refuse(fullName, "resolves outside the repos root");
        string[] chain = [$"{_root}/{parts[0]}", dir, $"{dir}/graphify-out", $"{dir}/graphify-out/graph.json"];
        for (var i = 0; i < chain.Length; i++)
        {
            FileSystemInfo info = i == chain.Length - 1 ? new FileInfo(chain[i]) : new DirectoryInfo(chain[i]);
            if (info.LinkTarget is not null) return Refuse(fullName, $"has a symbolic link at {Path.GetFileName(chain[i])}");
            if (!info.Exists) return Refuse(fullName, $"has no {Path.GetFileName(chain[i])}");
        }
        return true;
    }

    /// <summary>Checks every configured repository once at startup so a refused one shows in the log early.</summary>
    public void CheckAll()
    {
        foreach (var repo in maps.Value.Repos) Available(repo.FullName);
    }

    private bool Refuse(string fullName, string why)
    {
        log.LogWarning("Research refuses repository {Repo}: it {Reason}", fullName, why);
        return false;
    }
}
