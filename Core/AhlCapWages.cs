namespace NHL27Kit.Core;

/// <summary>
/// AHL players with CapWages' details instead of TheAHL.com's. TheAHL.com's feed is only trusted for who is on each AHL
/// roster and his AHL jersey number; everything about the player himself comes from CapWages, which lists every player
/// under an NHL contract (the "non-roster" players of each NHL team) and the affiliate's AHL contracts:
/// - birth date from the team page (it's what the game's players are matched by, and what a birth-date fix writes);
/// - height and weight from his player page (read only when sizes are compared, and kept a day: CapWagesApi's cache);
/// - a missing player's whole bio from his player page (CapWagesApi.BioAsync).
/// A player CapWages doesn't list (an AHL tryout, an ECHL deal) keeps the feed's details: there is nothing else.
/// </summary>
public static class AhlCapWages
{
    /// <summary>
    /// Each roster player matched to CapWages by name: the entry with his birth date when several share the name, the
    /// only one otherwise (its birth date then corrects the feed's). Matched players get CapWages' birth date and slug,
    /// and no height or weight until <see cref="SizesAsync"/> reads them.
    /// </summary>
    public static List<(AhlTeam Team, List<AhlPlayer> Players)> Enrich(IReadOnlyList<(AhlTeam Team, List<AhlPlayer> Players)> rosters, IReadOnlyList<CapTeam> cap)
    {
        if (rosters == null) return null;
        if (cap == null || cap.Count == 0) return rosters.ToList();
        var byName = new Dictionary<string, List<CapPlayer>>();
        foreach (var p in cap.SelectMany(t => t.Players.Concat(t.AhlPlayers)))
        {
            if (p.Slug.Length == 0) continue;
            string k = RosterSync.Norm(p.First + p.Last);
            if (!byName.TryGetValue(k, out var l)) byName[k] = l = new();
            if (!l.Any(x => x.Slug == p.Slug)) l.Add(p);
        }
        var result = new List<(AhlTeam, List<AhlPlayer>)>();
        foreach (var (team, players) in rosters)
        {
            var list = new List<AhlPlayer>(players.Count);
            foreach (var a in players)
            {
                var cp = Match(byName, a);
                list.Add(cp == null ? a : a with { Born = cp.Born ?? a.Born, HeightIn = 0, WeightLb = 0, BirthPlace = "", CapSlug = cp.Slug });
            }
            result.Add((team, list));
        }
        return result;
    }

    private static CapPlayer Match(Dictionary<string, List<CapPlayer>> byName, AhlPlayer a)
    {
        if (!byName.TryGetValue(RosterSync.Norm(a.First + a.Last), out var hits)) return null;
        var same = hits.Where(h => h.Born != null && h.Born == a.Born).ToList();
        if (same.Count == 1) return same[0];
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>
    /// Height and weight from each matched player's CapWages page (from the cache when read today). Players whose page
    /// can't be read keep none, so no size change is listed for them. <paramref name="progress"/>: (done, total).
    /// </summary>
    public static async Task<List<(AhlTeam Team, List<AhlPlayer> Players)>> SizesAsync(IReadOnlyList<(AhlTeam Team, List<AhlPlayer> Players)> rosters,
        Action<int, int> progress = null, CancellationToken ct = default)
    {
        var slugs = rosters.SelectMany(r => r.Players).Select(p => p.CapSlug).Where(s => s.Length > 0).Distinct().ToList();
        var bios = new Dictionary<string, PlayerBio>();
        int done = 0;
        foreach (var slug in slugs)
        {
            bool cached = CapWagesApi.IsCached(slug);
            try { if (await CapWagesApi.BioAsync(slug, ct) is { } b) bios[slug] = b; }
            catch (Exception e) when (e is not OperationCanceledException) { }
            progress?.Invoke(++done, slugs.Count);
            if (!cached) await Task.Delay(250, ct);
        }
        // his size, and the whole bio for BioSync's check of an existing player
        return rosters.Select(r => (r.Team, r.Players.Select(p => bios.TryGetValue(p.CapSlug, out var b) ? p with { HeightIn = b.HeightIn, WeightLb = b.WeightLb, CapBio = b } : p).ToList())).ToList();
    }
}
