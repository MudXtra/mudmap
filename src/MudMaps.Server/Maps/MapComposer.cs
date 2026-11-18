namespace MudMaps.Server.Maps;

/// <summary>Lays pull request activity over the repository graphs to produce the map JSON.</summary>
public static class MapComposer
{
    public const string MudBlazor = "MudBlazor";
    public const string Extensions = "MudBlazor.Extensions";

    /// <summary>How many of a room's pull requests are listed by number (newest first).</summary>
    public const int MaxRoomPrs = 12;

    /// <summary>Repository facts plus who opened the most pull requests in the window (ties go to more merged).</summary>
    public static RepoHighlightsDto? Highlights(RepoFacts? facts, IReadOnlyList<PullRequestInfo> prs)
    {
        if (facts is null) return null;
        var top = prs.Where(p => p.Author is not null)
            .GroupBy(p => p.Author!)
            .Select(g => (Author: g.Key, Prs: g.Count(), Merged: g.Count(p => p.MergedAt is not null)))
            .OrderByDescending(a => a.Prs).ThenByDescending(a => a.Merged).ThenBy(a => a.Author, StringComparer.Ordinal)
            .FirstOrDefault();
        return new RepoHighlightsDto(
            facts.Description,
            facts.LatestTag ?? facts.LatestRelease,
            facts.LatestRelease,
            facts.LatestReleaseAt,
            facts.LatestReleaseUrl,
            facts.LastPrAt,
            facts.LastCommitAt,
            top.Author,
            top.Prs,
            top.Merged,
            prs.Select(p => p.Author).Where(a => a is not null).Distinct().Count(),
            facts.OpenPrs,
            facts.OpenIssues,
            facts.Forks,
            facts.License);
    }

    public static MapDto Compose(
        IReadOnlyList<RepoGraph> graphs,
        IReadOnlyDictionary<string, RepoActivity> activity,
        int windowDays,
        DateTimeOffset now,
        bool prsLive,
        int maxRoads = 220)
    {
        var since = now.AddDays(-windowDays);
        var hoods = new List<HoodDto>();
        var clusters = new List<ClusterDto>();
        var repos = new List<RepoDto>();
        var roads = new Dictionary<(string, string), int>();
        var typeIndex = graphs.FirstOrDefault(g => g.Name == MudBlazor)?.TypeIndex ?? new Dictionary<string, string>();

        foreach (var g in graphs)
        {
            foreach (var (key, w) in g.Roads) roads[key] = roads.GetValueOrDefault(key) + w;

            var prs = activity.TryGetValue(g.FullName, out var act)
                ? act.PullRequests.Where(p => p.CreatedAt >= since).ToList()
                : [];
            var hoodPrs = new Dictionary<string, int>();
            var roomPrs = new Dictionary<string, int>();
            var roomPrNumbers = new Dictionary<string, List<int>>();
            var roomIds = g.Rooms.Select(r => r.Id).ToHashSet();
            foreach (var pr in prs)
            {
                var hitHoods = new HashSet<string>();
                var hitRooms = new HashSet<string>();
                foreach (var file in pr.Files)
                {
                    if (g.HoodKeyFor(AreaRules.AreaOf(file)) is { } hk) hitHoods.Add(hk);
                    if (g.FileRooms.TryGetValue(file, out var rs))
                    {
                        foreach (var r in rs) if (roomIds.Contains(r)) hitRooms.Add(r);
                    }
                }
                foreach (var hk in hitHoods) hoodPrs[hk] = hoodPrs.GetValueOrDefault(hk) + 1;
                foreach (var r in hitRooms)
                {
                    roomPrs[r] = roomPrs.GetValueOrDefault(r) + 1;
                    if (!roomPrNumbers.TryGetValue(r, out var nums)) roomPrNumbers[r] = nums = [];
                    nums.Add(pr.Number);
                }
            }

            foreach (var (key, area, size, testSize) in g.Areas)
            {
                hoods.Add(new HoodDto(key, AreaRules.Label(area), g.Name, size, hoodPrs.GetValueOrDefault(key), testSize));
            }
            if (g.HasOther)
            {
                hoods.Add(new HoodDto(g.OtherKey, "Everything else", g.Name, g.OtherSize, hoodPrs.GetValueOrDefault(g.OtherKey), g.OtherTestSize));
            }
            clusters.AddRange(g.Rooms.Select(r => new ClusterDto(r.Id, r.Hood, r.N, r.Dir, r.Top, roomPrs.GetValueOrDefault(r.Id), r.Name, r.TestN, r.Path,
                roomPrNumbers.TryGetValue(r.Id, out var nums) ? nums.OrderByDescending(n => n).Take(MaxRoomPrs).ToList() : null)));

            var url = act?.Url ?? $"https://github.com/{g.FullName}";
            repos.Add(new RepoDto(g.Name, g.FullName, url, act?.Stars ?? 0,
                prs.Count, prs.Count(p => p.MergedAt is not null), g.Nodes, g.BuiltAtCommit, Highlights(act?.Facts, prs),
                g.TestNodes, g.LicenseFile is null ? null : $"https://github.com/{g.FullName}/blob/HEAD/{Uri.EscapeDataString(g.LicenseFile)}"));
        }

        // Extension repos reaching into MudBlazor's types become roads between repos.
        foreach (var g in graphs.Where(g => g.Name != MudBlazor))
        {
            foreach (var (hk, label) in g.ExternalRefs)
            {
                if (!typeIndex.TryGetValue(label, out var target)) continue;
                var key = string.CompareOrdinal(hk, target) < 0 ? (hk, target) : (target, hk);
                roads[key] = roads.GetValueOrDefault(key) + 1;
            }
        }

        var mb = repos.FirstOrDefault(r => r.Name == MudBlazor);
        var ext = repos.FirstOrDefault(r => r.Name == Extensions);
        return new MapDto(
            new HeadlineDto(mb?.Prs ?? 0, mb?.Merged ?? 0),
            ext?.Prs ?? 0,
            repos.Where(r => r.Name is not MudBlazor and not Extensions).Select(r => new OtherRepoDto(r.Name, r.FullName, r.Prs, r.Stars)).ToList(),
            now.ToString("yyyy-MM-ddTHH:mmzzz"),
            windowDays,
            repos,
            hoods,
            roads.OrderByDescending(kv => kv.Value).Take(maxRoads).Select(kv => new RoadDto(kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList(),
            clusters,
            prsLive);
    }
}
