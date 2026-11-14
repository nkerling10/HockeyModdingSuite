using System.Text;

namespace NHL27Kit.Core;

/// <summary>One player stage 3 looked at: a player on an NHL or AHL team with no portrait, and his photo once found.</summary>
public sealed class StagePortrait
{
    public RosterPlayer Player { get; init; }
    public string TeamName { get; init; }
    /// <summary>"NHL" or "AHL".</summary>
    public string League { get; init; }
    public DateTime? Born { get; init; }
    /// <summary>His NHL.com id (from the roster list), or 0.</summary>
    public int NhlId { get; init; }
    /// <summary>His TheAHL.com id, or 0.</summary>
    public int AhlId { get; init; }
    /// <summary>His CapWages page (capwages.com/players/{CapSlug}), or "".</summary>
    public string CapSlug { get; init; } = "";
    /// <summary>His NHL.com headshot (a transparent mug): from the NHL.com roster list, or for an AHL player from his
    /// CapWages page; null until looked up or when there is none.</summary>
    public string HeadshotUrl { get; set; }
    /// <summary>Why he gets no portrait (logged to the skipped list); null when he can have one.</summary>
    public string Problem { get; set; }
    /// <summary>The fitted 512×340 portrait (RGBA), and the file it's saved in.</summary>
    public byte[] Portrait { get; set; }
    public string PortraitFile { get; set; }
    /// <summary>The slot his bio will point at; -1 until written.</summary>
    public int NewArtId { get; set; } = -1;

    /// <summary>A file name for his photos: his NHL.com id, else his CapWages page, else his game id.</summary>
    public string Key => NhlId > 0 ? $"nhl-{NhlId}" : CapSlug.Length > 0 ? CapSlug : $"player-{Player.GameId}";
}

/// <summary>
/// NHL Sync stage 3 (optional, after stage 2's changes are in): portraits for players on NHL and AHL teams who have none
/// (docs/portraits-stage3-plan.md). Only missing portraits are filled; a player who has one keeps it.
/// - NHL players: the headshot on NHL.com's roster list (read by stage 2).
/// - AHL players: the NHL.com headshot on their CapWages page (the AHL players' pages are read by stage 2 and cached).
///   One with no NHL.com photo is skipped and logged: other photos have backgrounds, which the game's dark portrait
///   backgrounds show up.
/// - Players on those teams whom no roster list has are logged too (nothing to match a photo to).
/// New portraits go into free slots in HMS's band (<see cref="BandFrom"/>–<see cref="BandTo"/>), so other writers
/// (the NCAA package, from 10,000) never land on the same slots.
/// </summary>
public static class PortraitStage
{
    /// <summary>HMS NHL Sync's portrait slots: the free ones among ids 1–9,999 (band A of the plan).</summary>
    public const int BandFrom = 1, BandTo = 9999;

    public static List<StagePortrait> Find(T3Db db, AssetIndex index, IReadOnlyList<NhlPlayer> api,
        IReadOnlyList<(AhlTeam Team, List<AhlPlayer> Players)> ahl, bool nhl, bool ahlOn)
    {
        var roster = new Roster(db);
        var matcher = new PlayerMatcher(db, roster);
        var codes = RosterSync.TeamIndex(db);
        string TeamName(int i) => roster.Teams.TryGetValue(i, out var tm) && !string.IsNullOrEmpty(tm.Name) ? tm.Name : $"team {i}";
        var list = new List<StagePortrait>();
        var seen = new HashSet<int>();
        var nhlTeams = codes.Values.ToHashSet();
        var ahlTeams = new HashSet<int>();

        if (nhl)
            foreach (var a in api ?? Array.Empty<NhlPlayer>())
            {
                int team = codes.GetValueOrDefault(a.Team, -1);
                var (p, nameOnly) = matcher.Find(a.First, a.Last, a.Born, team);
                if (p == null || nameOnly || p.Team != team || !seen.Add(p.GameId) || !PortraitSync.Missing(p, index)) continue;
                list.Add(new StagePortrait
                {
                    Player = p, TeamName = TeamName(team), League = "NHL", Born = a.Born, NhlId = a.Id,
                    HeadshotUrl = string.IsNullOrEmpty(a.Headshot) ? null : a.Headshot,
                    Problem = string.IsNullOrEmpty(a.Headshot) ? "no photo on NHL.com's roster list" : null,
                });
            }
        if (ahlOn)
            foreach (var (t, players) in ahl ?? Array.Empty<(AhlTeam, List<AhlPlayer>)>())
            {
                int team = RosterSync.ResolveAhlTeamIndex(db, t.Name);
                if (team < 0) continue;
                ahlTeams.Add(team);
                foreach (var a in players)
                {
                    var (p, nameOnly) = matcher.Find(a.First, a.Last, a.Born, team);
                    if (p == null || nameOnly || p.Team != team || !seen.Add(p.GameId) || !PortraitSync.Missing(p, index)) continue;
                    list.Add(new StagePortrait
                    {
                        Player = p, TeamName = TeamName(team), League = "AHL", Born = a.Born, AhlId = a.Id, CapSlug = a.CapSlug ?? "",
                        Problem = string.IsNullOrEmpty(a.CapSlug) ? "not on CapWages (no NHL.com photo to find)" : null,
                    });
                }
            }

        // the rest on those teams with no portrait: no roster list has them, so there's no photo to match (logged)
        foreach (var p in roster.Players)
        {
            bool onNhl = nhl && nhlTeams.Contains(p.Team), onAhl = ahlOn && ahlTeams.Contains(p.Team);
            if (!onNhl && !onAhl || seen.Contains(p.GameId) || !PortraitSync.Missing(p, index)) continue;
            seen.Add(p.GameId);
            list.Add(new StagePortrait
            {
                Player = p, TeamName = TeamName(p.Team), League = onNhl ? "NHL" : "AHL", Born = matcher.Born(p),
                Problem = $"on no {(onNhl ? "NHL.com" : "TheAHL.com")} roster list (nothing to match a photo to)",
            });
        }
        return list.OrderBy(c => c.League).ThenBy(c => c.TeamName).ThenBy(c => c.Player.LastName).ToList();
    }

    /// <summary>Free portrait slots in HMS's band, lowest first.</summary>
    public static List<int> FreeSlots(AssetIndex index, T3Db main, IEnumerable<T3Db> others) =>
        PortraitSync.FreeArtIds(index, main, others).Where(n => n >= BandFrom && n <= BandTo).ToList();

    /// <summary>Points each written player's bio at his slot: artid, hasportrait = 1.</summary>
    public static string Apply(T3Db db, IEnumerable<StagePortrait> done)
    {
        using var batch = db.Batch();
        var bio = db.Table(Roster.BioTable);
        var f = bio.Fields.ToDictionary(x => x.Name);
        var written = done.Where(c => c.NewArtId > 0).ToList();
        foreach (var c in written)
        {
            db.Set(bio, c.Player.BioRow, f["artid"], c.NewArtId);
            if (f.TryGetValue("hasportrait", out var hp)) db.Set(bio, c.Player.BioRow, hp, 1);
        }
        return $"Portraits (stage 3): {written.Count} player{(written.Count == 1 ? "" : "s")} given one";
    }

    /// <summary>
    /// The players stage 3 couldn't give a portrait, as CSV (UTF-8, one row each): name, team, league, game id, birth
    /// date, NHL.com and TheAHL.com ids, CapWages page, and why. For tracking down another photo source later.
    /// </summary>
    public static string SkippedCsv(IEnumerable<StagePortrait> all, DateTime when)
    {
        static string Q(string s) => s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new StringBuilder();
        sb.AppendLine($"# NHL Sync stage 3 skipped players, {when:yyyy-MM-dd HH:mm}");
        sb.AppendLine("Name,Team,League,Game id,Born,NHL.com id,TheAHL.com id,CapWages page,Why");
        foreach (var c in all.Where(c => c.Problem != null))
            sb.AppendLine(string.Join(",", Q(c.Player.Name), Q(c.TeamName), c.League, c.Player.GameId.ToString(),
                c.Born is { } b ? b.ToString("yyyy-MM-dd") : "", c.NhlId > 0 ? c.NhlId.ToString() : "", c.AhlId > 0 ? c.AhlId.ToString() : "",
                c.CapSlug.Length > 0 ? CapWagesApi.PlayerBase + c.CapSlug : "", Q(c.Problem)));
        return sb.ToString();
    }
}
