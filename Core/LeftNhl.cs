namespace NHL27Kit.Core;

/// <summary>Where a "left the NHL" player goes, as LeftNhl worked it out from CapWages.</summary>
/// <param name="ToTeam">His new team (exhibitionteams index), or -1 when he's released or nothing changes.</param>
/// <param name="Release">No roster and no contract anywhere: he becomes a free agent.</param>
/// <param name="Settled">CapWages says where he is: ticked and applied without review.</param>
/// <param name="Note">Why, for the row.</param>
/// <param name="Hold">He stays where he is (CapWages has him there, or another row moves him): the row is held and hidden.</param>
public sealed record LeftPlan(int ToTeam, string ToTeamName, bool Release, bool Settled, string Note, string Hold = null);

/// <summary>
/// Players the game has on an NHL team whom no NHL or AHL roster list has (SyncKind.Left), placed from CapWages instead of
/// being left for the user to guess at:
/// - no team on CapWages has him under contract (James Reimer, 2026-11-09) → released to free agency;
/// - on an NHL team's non-roster list in the minors → that team's AHL affiliate in the game;
/// - on a non-roster list anywhere else (status "Junior", "Europe", "Loan"…: Nikita Klepov, back with Saginaw after four
///   games for Anaheim) → his latest club from the stats on his CapWages player page, when the game has that club;
/// - on an AHL contract with a team's affiliate → that affiliate.
/// Players CapWages has on an NHL roster or injured list are held by the page before this (HoldNhlPlayers), and a player
/// CapWages has on another NHL team is left for review (a trade NHL.com doesn't show yet).
/// The same goes for players the game has on an AHL team whom no roster list has (RosterSync.AhlStragglers, FromAhl),
/// except that one CapWages still has with that AHL team (injured, just assigned) is held there, and one CapWages has
/// on an NHL roster is held for the CapWages call-up that moves him.
/// </summary>
public static class LeftNhl
{
    /// <summary>The game's club leagues other than the NHL: AHL 1, the European leagues 2–9, OHL 11, QMJHL 12, WHL 13, ECHL 15, 16.</summary>
    private static readonly HashSet<int> ClubLeagues = new() { 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12, 13, 15, 16 };

    /// <summary>CapWages' league names → the game's league, used only to pick between several loose name matches.</summary>
    private static readonly Dictionary<string, int> LeagueOf = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AHL"] = 1, ["SHL"] = 2, ["Liiga"] = 3, ["DEL"] = 4, ["Czechia"] = 5, ["Extraliga"] = 5, ["NL"] = 6, ["National League"] = 6,
        ["ICEHL"] = 7, ["HockeyAllsvenskan"] = 8, ["Allsvenskan"] = 8, ["OHL"] = 11, ["QMJHL"] = 12, ["LHJMQ"] = 12, ["WHL"] = 13, ["ECHL"] = 15,
    };

    /// <summary>CapWages' entry for each Left player, when it has exactly one (or one with his NHL team).</summary>
    private static Dictionary<int, (CapTeam Team, CapPlayer Player, bool AhlDeal)> Find(T3Db db, IEnumerable<SyncChange> left, IReadOnlyList<CapTeam> cap)
    {
        var byName = new Dictionary<string, List<(CapTeam, CapPlayer, bool)>>();
        foreach (var t in cap)
        {
            foreach (var p in t.Players) Add(t, p, false);
            foreach (var p in t.AhlPlayers) Add(t, p, true);
            foreach (var p in t.Reserves) Add(t, p, false);
        }
        void Add(CapTeam t, CapPlayer p, bool ahl)
        {
            string k = RosterSync.Norm(p.First + p.Last);
            if (!byName.TryGetValue(k, out var l)) byName[k] = l = new();
            if (!l.Any(x => x.Item1 == t && x.Item2.Name == p.Name)) l.Add((t, p, ahl));
        }
        var abbrOf = RosterSync.TeamIndex(db).GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
        var parentOf = RosterSync.AhlAffiliates(db).GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
        // a different first name ("Benjamin" / "Ben", "Thomas" / "Tommy"): the same last name and birth date
        var byLastBorn = byName.Values.SelectMany(l => l).Where(h => h.Item2.Born != null)
            .GroupBy(h => (RosterSync.Norm(h.Item2.Last), h.Item2.Born.Value)).ToDictionary(g => g.Key, g => g.ToList());
        var who = Who(db);
        var found = new Dictionary<int, (CapTeam, CapPlayer, bool)>();
        foreach (var c in left)
        {
            if (!byName.TryGetValue(RosterSync.Norm(c.Name), out var hits))
            {
                if (!who.TryGetValue(c.GameId, out var w) || w.Born is not { } b || !byLastBorn.TryGetValue((w.Last, b), out hits)) continue;
                if (hits.Select(h => h.Item2.Name).Distinct().Count() != 1) continue;
            }
            // a player on a contract and on a retained-salary list is one player: prefer his own team's entry (an AHL
            // player's NHL parent's)
            int mine = c.FromAhl ? parentOf.GetValueOrDefault(c.FromTeam, -1) : c.FromTeam;
            var own = hits.Where(h => abbrOf.TryGetValue(mine, out var a) && h.Item1.Abbr == a).ToList();
            var pick = own.Count == 1 ? own : hits.Select(h => h.Item2.Name).Distinct().Count() == 1 ? hits.Take(1).ToList() : null;
            if (pick?.Count == 1) found[c.GameId] = pick[0];
        }
        return found;
    }

    /// <summary>Each game player's normalised last name and birth date (the bio stores year - 1900, month - 1, day - 1).</summary>
    private static Dictionary<int, (string Last, DateTime? Born)> Who(T3Db db)
    {
        var bio = db.Table(Roster.BioTable);
        var f = bio.Fields.ToDictionary(x => x.Name);
        var map = new Dictionary<int, (string, DateTime?)>();
        for (int r = 0; r < bio.WrittenRecords; r++)
        {
            if (db.IsInvalid(bio, r)) continue;
            DateTime? born = null;
            try { born = new DateTime((int)db.Get(bio, r, f["year"]) + 1900, (int)db.Get(bio, r, f["month"]) + 1, (int)db.Get(bio, r, f["day"]) + 1); }
            catch (ArgumentException) { }
            map.TryAdd((int)db.Get(bio, r, f["game_id"]), (RosterSync.Norm(db.Get(bio, r, f["lastname"]) as string ?? ""), born));
        }
        return map;
    }

    /// <summary>
    /// The CapWages player pages a plan needs: for a Left player on a non-roster list who isn't in the minors, his page
    /// (for his latest club); for one no team page lists, the page his name most likely has (CapWagesApi.GuessSlug: an
    /// unsigned draft pick, a veteran on a tryout, a retired player), checked against his birth date when it's used.
    /// </summary>
    public static List<string> PagesNeeded(T3Db db, IEnumerable<SyncChange> left, IReadOnlyList<CapTeam> cap)
    {
        if (cap == null) return new List<string>();
        var lefts = left.Where(c => c.Kind == SyncKind.Left && c.Hold == null).ToList();
        var found = Find(db, lefts, cap);
        // non-roster players (junior, Europe, loan, and the minors, which can mean the ECHL) and unsigned picks: their clubs
        var slugs = found.Values.Where(x => !x.AhlDeal && x.Player.Section is "non-roster" or "reserve" && x.Player.Slug.Length > 0)
            .Select(x => x.Player.Slug).ToList();
        slugs.AddRange(lefts.Where(c => !found.ContainsKey(c.GameId)).Select(c => CapWagesApi.GuessSlug(c.Name)).Where(s => s.Length > 0));
        return slugs.Distinct().ToList();
    }

    private static bool InMinors(CapPlayer p) => p.Status is "" or "Minor" or "Minors" or "AHL";

    /// <summary>
    /// A plan for each Left change (by game id). <paramref name="all"/>: the list's changes (Left ones are planned; the
    /// rest are looked at for namesakes). <paramref name="pages"/>: the player pages read (slug → page, null when CapWages
    /// has none; a slug missing from it couldn't be read).
    /// </summary>
    public static Dictionary<int, LeftPlan> Plan(T3Db db, IEnumerable<SyncChange> all, IReadOnlyList<CapTeam> cap, IReadOnlyDictionary<string, CapPage> pages)
    {
        var plans = new Dictionary<int, LeftPlan>();
        if (cap == null || cap.Count == 0) return plans;
        var everything = all as IReadOnlyCollection<SyncChange> ?? all.ToList();
        var lefts = everything.Where(c => c.Kind == SyncKind.Left && c.Hold == null).ToList();
        var found = Find(db, lefts, cap);
        var codes = RosterSync.TeamIndex(db);
        var affiliate = RosterSync.AhlAffiliates(db);
        var parentOf = affiliate.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
        var roster = new Roster(db);
        string TeamName(int i) => roster.Teams.TryGetValue(i, out var tm) && !string.IsNullOrEmpty(tm.Name) ? tm.Name : $"team {i}";
        var clubs = Clubs(db);
        var abbrOf = codes.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
        var byId = roster.Players.GroupBy(p => p.GameId).ToDictionary(g => g.Key, g => g.First());
        var bio = db.Table(Roster.BioTable);
        var bf = bio.Fields.ToDictionary(x => x.Name);
        string season = $"{CapWagesApi.CurrentSeason(cap)}-{(CapWagesApi.CurrentSeason(cap) + 1) % 100:00}";
        DateTime? Born(SyncChange c)
        {
            try { return new DateTime((int)db.Get(bio, c.BioRow, bf["year"]) + 1900, (int)db.Get(bio, c.BioRow, bf["month"]) + 1, (int)db.Get(bio, c.BioRow, bf["day"]) + 1); }
            catch (ArgumentException) { return null; }
        }
        // the NHL club the game has his rights with (bio proteam), or -1
        int Rights(SyncChange c) => bf.TryGetValue("proteam", out var pt) && (int)db.Get(bio, c.BioRow, pt) is var t && codes.ContainsValue(t) ? t : -1;

        LeftPlan Hold(string why) => new(-1, null, false, false, null, "Held: " + why);
        // where a player under contract (or whose rights a team holds) goes when his club isn't known or isn't in the game:
        // his team's affiliate, which is where a shared roster parks such players; held when he's already there
        LeftPlan Park(SyncChange c, int nhl, string why)
        {
            int farm = nhl >= 0 ? affiliate.GetValueOrDefault(nhl, -1) : -1;
            if (c.FromAhl && (farm < 0 || farm == c.FromTeam)) return Hold($"{why}, so he stays on {c.FromTeamName}.");
            if (farm >= 0) return new LeftPlan(farm, TeamName(farm), false, true, $"{why}: parked with {(abbrOf.TryGetValue(nhl, out var a) ? a : "his team")}'s affiliate.");
            return new LeftPlan(-1, null, false, false, why + ".");
        }
        // his latest club from his page: there (held when he's already there), or parked when the game lacks it
        LeftPlan ToClub(SyncChange c, CapPage page, int nhl, string intro)
        {
            var (club, seen) = LatestClub(page.Stats, clubs, season);
            if (club >= 0 && club == c.FromTeam) return Hold($"{intro}; his latest club is {seen}, where he is, and its roster list leaves him out.");
            if (club >= 0) return new LeftPlan(club, TeamName(club), false, true, $"{intro}; his latest club is {seen}.");
            return Park(c, nhl, seen != null ? $"{intro}; his latest club, {seen}, isn't in the game" : $"{intro}; his page lists no club this season");
        }

        foreach (var c in lefts)
        {
            if (!found.TryGetValue(c.GameId, out var hit))
            {
                // no team page lists him: his own page, if CapWages has one with his birth date
                var born = Born(c);
                if (pages != null && pages.TryGetValue(CapWagesApi.GuessSlug(c.Name), out var page) && page != null
                    && page.Born != null && born != null && page.Born == born)
                {
                    int team = codes.GetValueOrDefault(page.TeamAbbr ?? "", -1);
                    if (page.Status == "Prospect" || team >= 0)
                    {
                        // an unsigned pick whose rights a team holds (Michael Hage, MTL): to his club if the game has it, else kept
                        int owner = team >= 0 ? team : Rights(c);
                        plans[c.GameId] = ToClub(c, page, owner, $"CapWages: {(page.Status.Length > 0 ? page.Status : "signed")}, {(page.TeamAbbr is { Length: > 0 } ab ? ab + "'s" : "an NHL team's")} rights");
                        continue;
                    }
                    // no NHL team and inactive: his contract ended (James Reimer: terminated 2026-11-08, after a game for Hartford)
                    if (page.Status is "Inactive" or "Retired" or "UFA" or "Unsigned")
                    {
                        plans[c.GameId] = new LeftPlan(-1, null, true, true, $"CapWages: {page.Status}, no contract with any team: released to free agency.");
                        continue;
                    }
                    // no NHL team: playing this season → that club (or held where he is); not playing → released
                    var (club, seen) = LatestClub(page.Stats, clubs, season, currentOnly: true);
                    if (club >= 0 && club == c.FromTeam)
                        plans[c.GameId] = Hold($"CapWages has him with {seen} this season, where he is; its roster list leaves him out.");
                    else if (club >= 0)
                        plans[c.GameId] = new LeftPlan(club, TeamName(club), false, true, $"CapWages: no NHL contract; he plays for {seen} this season.");
                    else
                        plans[c.GameId] = new LeftPlan(-1, null, true, true, seen != null
                            ? $"CapWages: no NHL contract, and his club this season, {seen}, isn't in the game: released to free agency."
                            : "CapWages: no NHL contract and no club this season: released to free agency.");
                    continue;
                }
                // no page: his name may just be spelled differently ("Matt" / "Matthew"): a namesake on his team's CapWages page,
                // or on his AHL team's roster as a player the game doesn't have, is reviewed instead of releasing him
                string last = byId.TryGetValue(c.GameId, out var gp) ? RosterSync.Norm(gp.LastName) : null;
                int parent = c.FromAhl ? parentOf.GetValueOrDefault(c.FromTeam, -1) : c.FromTeam;
                var own = abbrOf.TryGetValue(parent, out var fa) ? cap.FirstOrDefault(t => t.Abbr == fa) : null;
                string namesake = last == null ? null
                    : own?.Players.Concat(own.AhlPlayers).FirstOrDefault(p => RosterSync.Norm(p.Last) == last) is { } np ? $"CapWages has a {np.Name} with {fa}"
                    : everything.FirstOrDefault(m => m.Kind == SyncKind.Missing && m.ToTeam == c.FromTeam && m.Ahl != null && RosterSync.Norm(m.Ahl.Last) == last) is { } mp
                        ? $"{c.FromTeamName}'s AHL roster has a {mp.Name} the game doesn't" : null;
                if (namesake != null)
                {
                    plans[c.GameId] = new LeftPlan(-1, null, false, false, $"No roster list has him, but {namesake}: check whether that's him before moving him.");
                    continue;
                }
                // the game has a team holding his rights: that's not for a guess to throw away
                if (Rights(c) is >= 0 and var r)
                {
                    plans[c.GameId] = new LeftPlan(-1, null, false, false, $"No roster list or CapWages page has him, but the game has his rights with {(abbrOf.TryGetValue(r, out var ra) ? ra : TeamName(r))}: check before releasing him.");
                    continue;
                }
                plans[c.GameId] = new LeftPlan(-1, null, true, true,
                    "No NHL or AHL roster has him, and CapWages has no contract or page for him: released to free agency.");
                continue;
            }
            var (capTeam, cp, ahlDeal) = hit;
            int nhl = codes.GetValueOrDefault(capTeam.Abbr, -1);
            if (ahlDeal || InMinors(cp) && cp.Section == "non-roster")
            {
                string deal = ahlDeal ? $"on an AHL contract with {capTeam.Abbr}'s affiliate" : $"in the minors with {capTeam.Abbr}";
                int farm = nhl >= 0 ? affiliate.GetValueOrDefault(nhl, -1) : -1;
                // "Minor" is the ECHL too (Ruslan Khazheev, CAR, in Greensboro): his ECHL club this season when the game has it
                if (!ahlDeal && pages != null && pages.TryGetValue(cp.Slug, out var mp) && mp != null
                    && LatestClub(mp.Stats, clubs, season, currentOnly: true) is ( >= 0 and var echl, var at)
                    && clubs.FirstOrDefault(k => k.Index == echl).League == 15)
                {
                    plans[c.GameId] = echl == c.FromTeam
                        ? Hold($"CapWages has him {deal}, playing for {at} this season, where he is.")
                        : new LeftPlan(echl, TeamName(echl), false, true, $"CapWages: {deal}; he plays for {at} this season.");
                    continue;
                }
                // still with this AHL team: TheAHL.com's list leaves out injured and just-assigned players
                if (farm >= 0 && farm == c.FromTeam)
                    plans[c.GameId] = Hold($"CapWages has him {deal}. TheAHL.com's roster list leaves him out (injured or just assigned), so he stays on {c.FromTeamName}.");
                else if (farm >= 0) plans[c.GameId] = new LeftPlan(farm, TeamName(farm), false, true, $"CapWages: {deal}.");
                continue;
            }
            if (cp.Section is "roster" or "inactive")
            {
                string st = cp.Status.Length > 0 ? cp.Status : cp.Section;
                // an AHL player CapWages has on an NHL roster: its call-up (LineupCallUps.FromCapWages) moves him
                plans[c.GameId] = c.FromAhl
                    ? Hold($"CapWages has him on {capTeam.Abbr}'s NHL roster ({st}); the CapWages call-up moves him there.")
                    // on another NHL team's roster: a trade NHL.com doesn't show yet (his own team's would have held him)
                    : new LeftPlan(-1, null, false, false, $"CapWages has him on {capTeam.Abbr}'s roster ({st}): check the trade before moving him.");
                continue;
            }
            // junior, Europe, on loan…, or an unsigned pick: his latest club from his player page
            string status = cp.Status.Length > 0 ? cp.Status : "non-roster";
            string intro = cp.Section == "reserve" ? $"CapWages: unsigned draft pick, {capTeam.Abbr}'s rights" : $"CapWages: {status}, under contract with {capTeam.Abbr}";
            plans[c.GameId] = pages != null && pages.TryGetValue(cp.Slug, out var own2) && own2 != null
                ? ToClub(c, own2, nhl, intro)
                : new LeftPlan(-1, null, false, false, $"{intro}; his player page couldn't be read, so his club isn't known.");
        }
        return plans;
    }

    /// <summary>Puts the plans on the changes (the list's, or a fresh compare's at Apply): destination, release, note, tick.</summary>
    public static void Use(IEnumerable<SyncChange> changes, IReadOnlyDictionary<int, LeftPlan> plans)
    {
        if (plans == null) return;
        foreach (var c in changes)
        {
            if (c.Kind != SyncKind.Left || c.Hold != null || !plans.TryGetValue(c.GameId, out var p)) continue;
            if (p.Hold != null) { c.Hold = p.Hold; c.Selected = false; continue; }
            c.Release = p.Release;
            if (!p.Release && p.ToTeam >= 0) { c.ToTeam = p.ToTeam; c.ToTeamName = p.ToTeamName; }
            if (p.Release) { c.ToTeam = -1; c.ToTeamName = null; }
            c.Where = p.Note;
            c.Settled = p.Settled;
            c.Selected = p.Settled && c.CanApply;
        }
    }

    /// <summary>The game's club teams outside the NHL: (index, league, normalised names).</summary>
    private static List<(int Index, int League, string[] Names)> Clubs(T3Db db)
    {
        var t = db.Table(Roster.TeamsTable);
        var f = t.Fields.ToDictionary(x => x.Name);
        string S(int r, string n) => f.ContainsKey(n) ? db.Get(t, r, f[n]) as string ?? "" : "";
        var list = new List<(int, int, string[])>();
        for (int r = 0; r < t.WrittenRecords; r++)
        {
            if (db.IsInvalid(t, r)) continue;
            int league = (int)db.Get(t, r, f["league"]);
            if (!ClubLeagues.Contains(league)) continue;
            var names = new[] { S(r, "teamname"), S(r, "nickname"), (S(r, "cityname") + " " + S(r, "nickname")) }
                .Select(RosterSync.Norm).Where(n => n.Length > 0).Distinct().ToArray();
            list.Add(((int)db.Get(t, r, f["index"]), league, names));
        }
        return list;
    }

    /// <summary>
    /// His latest club the game has: the most recent season with any non-NHL row, and in it the last row (the page lists a
    /// season's teams in the order he played for them) whose team is a club in the game. Older seasons are only looked at
    /// when the latest has nothing but NHL games, so a player now on a club the game lacks isn't sent to an old one.
    /// Returns the team (-1 when none) and the club as CapWages names it ("Saginaw Spirit (OHL)").
    /// <paramref name="currentOnly"/>: only this season (<paramref name="season"/>, "2026-27") counts: a player with no NHL
    /// contract and no games this season has no club.
    /// </summary>
    private static (int Team, string Seen) LatestClub(IReadOnlyList<CapStat> rows, List<(int Index, int League, string[] Names)> clubs, string season, bool currentOnly = false)
    {
        foreach (var group in rows.GroupBy(r => r.Season).OrderByDescending(g => g.Key, StringComparer.Ordinal))
        {
            if (string.CompareOrdinal(group.Key, season) > 0) continue;
            if (currentOnly && group.Key != season) break;
            var season1 = group;
            var other = season1.Where(r =>!r.League.Equals("NHL", StringComparison.OrdinalIgnoreCase)).ToList();
            if (other.Count == 0) continue;
            for (int i = other.Count - 1; i >= 0; i--)
                if (Resolve(other[i], clubs) is >= 0 and var team) return (team, $"{other[i].Team} ({other[i].League})");
            var last = other[^1];
            return (-1, $"{last.Team} ({last.League})");
        }
        return (-1, null);
    }

    private static int Resolve(CapStat s, List<(int Index, int League, string[] Names)> clubs)
    {
        string want = RosterSync.Norm(s.Team);
        if (want.Length == 0) return -1;
        var exact = clubs.Where(c => c.Names.Contains(want)).ToList();
        int? league = LeagueOf.TryGetValue(s.League, out int l) ? l : null;
        if (exact.Count > 1 && league is { } lg) exact = exact.Where(c => c.League == lg).ToList();
        if (exact.Count == 1) return exact[0].Index;
        // loose ("Färjestad BK" / "Farjestads", "Djurgårdens IF" / "Djurgården Hockey"): only within his league, and only
        // when one club matches, by one name holding the other or the two starting with the same 6+ letters
        if (league is not { } lg2) return -1;
        static int Common(string a, string b) { int i = 0; while (i < a.Length && i < b.Length && a[i] == b[i]) i++; return i; }
        var loose = clubs.Where(c => c.League == lg2 && c.Names.Any(n => n.Length >= 4 && (n.Contains(want) || want.Contains(n) || Common(n, want) >= 6))).ToList();
        return loose.Count == 1 ? loose[0].Index : -1;
    }
}
