namespace NHL27Kit.Core;

/// <summary>One existing player whose bio differs from his source: name spelling, birthplace, country, shot, draft.</summary>
public sealed class BioChange
{
    public int GameId { get; init; } = -1;
    public int BioRow { get; init; } = -1;
    /// <summary>His name as the game has it now.</summary>
    public string Name { get; init; }
    public string TeamAbbr { get; init; }
    public string Position { get; init; }
    /// <summary>"NHL.com" or "CapWages".</summary>
    public string Source { get; init; }
    /// <summary>The new first and last name, or null when the name stays.</summary>
    public string NewFirst { get; init; }
    public string NewLast { get; init; }
    /// <summary>The new birthplace (city), or null when it stays.</summary>
    public string NewCity { get; init; }
    /// <summary>The new intlcountry and state values, or -1 when they stay.</summary>
    public int NewCountry { get; init; } = -1;
    public int NewState { get; init; } = -1;
    /// <summary>The new handedness (0 left, 1 right), or -1 when it stays.</summary>
    public int NewHand { get; init; } = -1;
    /// <summary>The new draft (AHL players, from CapWages), or null when it stays.</summary>
    public DraftInfo NewDraft { get; init; }
    public int NewDraftTeam { get; init; } = -1;
    /// <summary>What changes, for the row ("birthplace Toronto → Mississauga", "shoots L → R").</summary>
    public List<string> Parts { get; init; } = new();
    /// <summary>Matched by name only, or by a shortened first name on his team: his birth date doesn't confirm it's him.</summary>
    public bool NameOnly { get; init; }
    public bool Selected { get; set; }

    public bool CanApply => BioRow >= 0;
    public bool NameChanges => NewFirst != null;
    public string Description => string.Join("  ·  ", Parts);
}

/// <summary>
/// Existing players' bios checked against the sources a sync already reads, without reading anything more:
/// - NHL players against NHL.com's roster lists: first and last name, birthplace, birth country and province/state,
///   shoots/catches (the roster list has no draft, and reading every player's page for it isn't worth it);
/// - AHL players against their CapWages pages (read for their heights and weights; AhlPlayer.CapBio): the same, plus
///   the draft when CapWages has one.
/// Number, height, weight and birth date are the roster compare's (Info rows); positions and contracts CapWages'.
/// A name only changes when the player was matched by birth date (the same last name and birth date: "Will" → "William"
/// Zellers); spellings that differ only in accents or capitals are left as the game has them, and so are cities.
/// A country the game has no value for (127, "other") never replaces one it has. Name-only matches wait for a tick.
/// </summary>
public static class BioSync
{
    public static List<BioChange> Compare(T3Db db, IReadOnlyList<NhlPlayer> api, IReadOnlyList<(AhlTeam Team, List<AhlPlayer> Players)> ahl)
    {
        var changes = new List<BioChange>();
        var bio = db.Table(Roster.BioTable);
        var bf = bio.Fields.ToDictionary(x => x.Name);
        var roster = new Roster(db);
        var matcher = new PlayerMatcher(db, roster);
        var codes = RosterSync.TeamIndex(db);
        var abbrOf = codes.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
        int Int(int row, string f) => bf.TryGetValue(f, out var x) ? (int)db.Get(bio, row, x) : -1;
        string Str(int row, string f) => bf.TryGetValue(f, out var x) ? db.Get(bio, row, x) as string ?? "" : "";
        var seen = new HashSet<int>();

        void Check(string first, string last, DateTime? born, int team, string teamAbbr, string shoots, string city, string country, string region,
                   DraftInfo draft, string source)
        {
            var (p, nameOnly) = matcher.Find(first, last, born, team);
            if (p == null || !seen.Add(p.GameId)) return;
            int row = p.BioRow;
            var parts = new List<string>();
            string newFirst = null, newLast = null, newCity = null;
            int newCountry = -1, newState = -1, newHand = -1, newDraftTeam = -1;
            DraftInfo newDraft = null;

            // name: only a real spelling difference, on a player his birth date confirms
            string oldFirst = Str(row, "firstname"), oldLast = Str(row, "lastname");
            if (!nameOnly && first.Length > 0 && last.Length > 0
                && RosterSync.Norm(first) + RosterSync.Norm(last) != RosterSync.Norm(oldFirst) + RosterSync.Norm(oldLast))
            {
                newFirst = PlayerCreator.Fit(first, PlayerCreator.NameBytes);
                newLast = PlayerCreator.Fit(last, PlayerCreator.NameBytes);
                parts.Add($"name {oldFirst} {oldLast} → {newFirst} {newLast}");
            }

            // birthplace (city)
            string oldCity = Str(row, "birthplace");
            string cityCased = PlayerCreator.CityCase(city);
            if (cityCased.Length > 0 && RosterSync.Norm(cityCased) != RosterSync.Norm(oldCity))
            {
                newCity = PlayerCreator.Fit(cityCased, PlayerCreator.BirthplaceBytes);
                parts.Add($"birthplace {(oldCity.Length > 0 ? oldCity : "none")} → {newCity}");
            }

            // country and province/state; a Canadian or American whose province/state isn't known keeps the game's
            var (c, s) = MissingPlayers.GameCountry(country, region);
            int oldC = Int(row, "intlcountry"), oldS = Int(row, "state");
            if (c >= 0 && c != 127)
            {
                // outside North America only the country counts (the game's state value for those players varies)
                bool northAmerica = c is 0 or 14;
                bool regionKnown = northAmerica && s != c;
                int wantS = regionKnown ? s : oldC == c ? oldS : s;
                if (c != oldC || northAmerica && wantS != oldS)
                {
                    (newCountry, newState) = (c, wantS);
                    parts.Add($"born in {MissingPlayers.CountryText(oldC, oldS)} → {MissingPlayers.CountryText(c, wantS)}");
                }
            }

            // shoots / catches
            int hand = shoots == "R" ? 1 : shoots == "L" ? 0 : -1;
            int oldHand = Int(row, "handedness");
            if (hand >= 0 && hand != oldHand)
            {
                newHand = hand;
                string verb = p.PositionPrimary == 5 ? "catches" : "shoots";
                parts.Add($"{verb} {(oldHand == 1 ? "R" : "L")} → {shoots}");
            }

            // draft (only when the source has one: CapWages' "undrafted" can just be a page without it)
            if (draft is { IsUndrafted: false } d)
            {
                int dt = PlayerCreator.DraftTeam(codes, d.Team);
                int oy = Int(row, "draftyear"), or = Int(row, "draftround"), op = Int(row, "draftposition"), ot = Int(row, "draftteam");
                if (d.Year - 1900 != oy || d.Round != or || d.Overall != op || dt >= 0 && dt != ot)
                {
                    (newDraft, newDraftTeam) = (d, dt >= 0 ? dt : ot);
                    string was = or > 0 ? $"{oy + 1900} round {or} (#{op}) {(abbrOf.TryGetValue(ot, out var oa) ? oa : "?")}" : "undrafted";
                    parts.Add($"draft {was} → {d.Year} round {d.Round} (#{d.Overall}) {d.Team}");
                }
            }

            if (parts.Count == 0) return;
            changes.Add(new BioChange
            {
                GameId = p.GameId, BioRow = row, Name = p.Name, TeamAbbr = teamAbbr, Position = p.PositionDisplay, Source = source,
                NewFirst = newFirst, NewLast = newLast, NewCity = newCity, NewCountry = newCountry, NewState = newState, NewHand = newHand,
                // a new name waits for a tick: NHL.com and CapWages often use the short form ("Matt", "JJ", "Bo") where
                // the game has the full one, which is a choice, not an error
                NewDraft = newDraft, NewDraftTeam = newDraftTeam, Parts = parts, NameOnly = nameOnly, Selected = !nameOnly && newFirst == null,
            });
        }

        // NHL first: a player on both lists is checked against NHL.com
        foreach (var a in api ?? Array.Empty<NhlPlayer>())
            Check(a.First, a.Last, a.Born, codes.GetValueOrDefault(a.Team, -1), a.Team, a.Shoots, a.BirthCity, a.BirthCountry, a.BirthRegion, null, "NHL.com");
        foreach (var (team, players) in ahl ?? Array.Empty<(AhlTeam, List<AhlPlayer>)>())
        {
            int t = RosterSync.ResolveAhlTeamIndex(db, team.Name);
            foreach (var a in players)
                if (a.CapBio is { } b)
                    Check(b.First.Length > 0 ? b.First : a.First, b.Last.Length > 0 ? b.Last : a.Last, a.Born, t, team.Name, b.Shoots,
                          b.BirthCity, b.BirthCountry, b.BirthRegion, b.Draft, "CapWages");
        }
        return changes.OrderBy(c => c.TeamAbbr).ThenBy(c => c.Name).ToList();
    }

    /// <summary>Writes the bios: name, birthplace, country and province/state, handedness, draft.</summary>
    public static string Apply(T3Db db, IEnumerable<BioChange> changes)
    {
        using var batch = db.Batch();
        var bio = db.Table(Roster.BioTable);
        var bf = bio.Fields.ToDictionary(x => x.Name);
        void Set(int row, string name, int v)
        {
            if (!bf.TryGetValue(name, out var f)) return;
            long max = f.RangeLow + (1L << f.Depth) - 1;
            if (v >= f.RangeLow && v <= max) db.Set(bio, row, f, v);
        }
        void SetText(int row, string name, string v) { if (bf.TryGetValue(name, out var f)) db.Set(bio, row, f, v); }
        int n = 0, names = 0;
        foreach (var c in changes.Where(c => c.CanApply))
        {
            if (c.NameChanges) { SetText(c.BioRow, "firstname", c.NewFirst); SetText(c.BioRow, "lastname", c.NewLast); names++; }
            if (c.NewCity != null) SetText(c.BioRow, "birthplace", c.NewCity);
            if (c.NewCountry >= 0) { Set(c.BioRow, "intlcountry", c.NewCountry); Set(c.BioRow, "state", c.NewState); }
            if (c.NewHand >= 0) Set(c.BioRow, "handedness", c.NewHand);
            if (c.NewDraft is { } d)
            {
                Set(c.BioRow, "draftyear", d.Year - 1900); Set(c.BioRow, "draftround", d.Round); Set(c.BioRow, "draftposition", d.Overall);
                Set(c.BioRow, "draftteam", c.NewDraftTeam);
            }
            n++;
        }
        return $"Bios: {n} player{(n == 1 ? "" : "s")} updated" + (names > 0 ? $" ({names} name{(names == 1 ? "" : "s")})" : "");
    }
}
