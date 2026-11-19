using System.Text.Json;

namespace NHL27Kit.Core;

/// <summary>
/// Folds a player entered twice (the same person, another spelling, the same birth date: PlayerMatcher.SamePersonPairs)
/// into one. The base roster's player (the lower game id: his ratings, portrait / art id, career rows) stays; he takes
/// the copy's team, roster rows, contract and number when the copy is on a club team, and the copy's ratings and position
/// when the copy came from the roster pack this run (freshRatings: Vasy's update is newer; a copy NHL Sync made has
/// template ratings, so his own stay). The copy is deleted from every player table and anything else pointing at him
/// (career stats, awards…) points at the kept player. The name is the copy's (the source's spelling: Pettersson,
/// Sokolovskii, Hoffmann) unless player-names.json (HMS folder) fixes it (Lofttus, a typo in the pack: Loftus).
///
/// Why (2026-11-19): Vasy's pack has both Liam Petterson (31950) and a Create-a-Player Liam Pettersson (57700); the base
/// import converted the latter to a new real player (32424) before the matcher knew other spellings, and every later
/// import matched Vasy's entry to that copy by its exact name, so the copy stayed. Same for Sokolovskii and Lofttus;
/// Hoffmann came from an old NHL Sync run. Pairs where neither is on a club team (women's Worlds / PWHL, the online
/// circuit's "Super Hero" placeholders) are left alone.
/// </summary>
public static class PlayerMerge
{
    /// <summary>Progress lines (the CLI prints them); null = quiet.</summary>
    public static Action<string> Trace;

    public static string NamesFile => Path.Combine(KitPaths.Root, "player-names.json");

    /// <summary>A chosen spelling: "First Last" for the player born on Born whose name (either copy's) skeleton-matches.</summary>
    public sealed record NameChoice(string Born, string First, string Last);

    public static List<NameChoice> LoadNames(string file = null)
    {
        file ??= NamesFile;
        try { return File.Exists(file) ? JsonSerializer.Deserialize<List<NameChoice>>(File.ReadAllText(file)) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    private static readonly HashSet<string> PlayerRowTables = new(StringComparer.OrdinalIgnoreCase)
    {
        Roster.BioTable, Roster.SkaterTable, Roster.GoalieTable, "exhibitionskaterequipment", "exhibitiongoalieequipment",
        "exhibitionresortskaterextras", "exhibitionresortgoalieextras", "exhibitionresortplayerextras", "stockgoalieequipment",
    };
    private static readonly string[] IndexTables = { Roster.RosterTable, RosterSync.OnlineRosterTable };

    /// <summary>Merges every same-person pair with a club-team player in it. Returns one line per merge.</summary>
    // teams whose lines need building again, per roster table: built once after every merge (a build reads every player)
    [ThreadStatic] private static Dictionary<string, HashSet<int>> Touched;

    public static List<string> Run(T3Db db, IReadOnlyList<NameChoice> names = null, ISet<int> freshRatings = null)
    {
        Touched = new Dictionary<string, HashSet<int>>();
        using var batch = db.Batch();                     // row writes are slow one by one (a table rewrite each)
        var done = RunPairs(db, names, freshRatings);
        foreach (var (tname, teams) in Touched)
            if (teams.Count > 0 && db.Table(tname) is { } t) RosterLines.Build(db, t, teams);
        Touched = null;
        return done;
    }

    private static List<string> RunPairs(T3Db db, IReadOnlyList<NameChoice> names, ISet<int> freshRatings)
    {
        names ??= LoadNames();
        freshRatings ??= new HashSet<int>();
        var done = new List<string>();
        var roster = new Roster(db);
        var league = new Dictionary<int, int>();
        var teams = db.Table(Roster.TeamsTable);
        var tf = teams.Fields.ToDictionary(f => f.Name);
        for (int r = 0; r < teams.WrittenRecords; r++)
            if (!db.IsInvalid(teams, r)) league.TryAdd(Convert.ToInt32(db.Get(teams, r, tf["index"])), Convert.ToInt32(db.Get(teams, r, tf["league"])));
        bool Club(int team) => team >= 0 && RosterSync.IsClubTeamLeague(league.GetValueOrDefault(team, -1));
        int RowTeam(RosterPlayer p) => p.RowTeam;

        var matcher = new PlayerMatcher(db, roster);
        var pairs = matcher.SamePersonPairs();
        Trace?.Invoke($"pairs {pairs.Count} " + DateTime.Now.ToString("HH:mm:ss.fff"));
        foreach (var (a, b) in pairs)
        {
            // a club player in the pair (team or roster row); both nowhere / on event teams: not ours to touch
            if (!new[] { a.Team, b.Team, RowTeam(a), RowTeam(b) }.Any(Club)) continue;
            var keep = a.GameId < b.GameId ? a : b;
            var copy = keep == a ? b : a;
            // a goalie and a skater are two people
            if ((keep.PositionPrimary == 5) != (copy.PositionPrimary == 5)) continue;
            var born = matcher.Born(keep);
            done.Add(MergeOne(db, keep, copy, Club(copy.Team) || Club(copy.RowTeam), born, names, freshRatings.Contains(copy.GameId)));
            // rows moved: the next pair needs a fresh read
            return done.Concat(RunPairs(db, names, freshRatings)).ToList();
        }
        return done;
    }

    /// <summary>The spelling to use for a player born on <paramref name="born"/> whom the source calls First Last:
    /// player-names.json's when it has him (same birth date, a last name that's his by PlayerMatcher), else the source's.</summary>
    public static (string First, string Last) Spelling(DateTime? born, string first, string last, IReadOnlyList<NameChoice> names)
    {
        if (born is { } d && names?.FirstOrDefault(n => n.Born == d.ToString("yyyy-MM-dd") && PlayerMatcher.SameLastName(n.Last, last)) is { } c)
            return (c.First, c.Last);
        return (first ?? "", last ?? "");
    }

    private static string MergeOne(T3Db db, RosterPlayer keep, RosterPlayer copy, bool copyOnClub, DateTime? born, IReadOnlyList<NameChoice> names, bool takeRatings)
    {
        var bio = db.Table(Roster.BioTable);
        var bf = bio.Fields.ToDictionary(f => f.Name);
        int Get(int row, string f) => bf.TryGetValue(f, out var x) ? Convert.ToInt32(db.Get(bio, row, x)) : 0;

        Trace?.Invoke("1. his contract " + DateTime.Now.ToString("HH:mm:ss.fff"));
        // 1. his contract, team and number from the copy when the copy plays for a club (the sync kept the copy current)
        if (copyOnClub)
            foreach (var f in bio.Fields.Where(f => f.Type == T3FieldType.Integer && (f.Name is "team" or "proteam" or "jersey" or "yearufa"
                         || f.Name.StartsWith("contract") || f.Name.StartsWith("extension"))))
                db.Set(bio, keep.BioRow, f, Get(copy.BioRow, f.Name));

        Trace?.Invoke("2. the name " + DateTime.Now.ToString("HH:mm:ss.fff"));
        // 2. the name: the copy's (the source's spelling), unless player-names.json fixes it
        string was = keep.Name;
        var (nf, nl) = Spelling(born, copy.FirstName, copy.LastName, names);
        if (nf.Length > 0 && nl.Length > 0) { db.Set(bio, keep.BioRow, bf["firstname"], nf); db.Set(bio, keep.BioRow, bf["lastname"], nl); }
        string name = $"{nf} {nl}".Trim();

        Trace?.Invoke("2b. the roster pack " + DateTime.Now.ToString("HH:mm:ss.fff"));
        // 2b. the roster pack's ratings and position (the copy is the pack's newer entry of him); his id stays on the row
        if (takeRatings && copy.RatingsTable != null && copy.RatingsTable == keep.RatingsTable)
        {
            var rt = copy.RatingsTable;
            db.WriteRecord(rt, keep.RatingsRow, db.RecordBytes(rt, copy.RatingsRow));
            db.Set(rt, keep.RatingsRow, rt.Fields.First(f => f.Name == "game_id"), keep.GameId);
            foreach (var f in new[] { "position", "positionprimary", "positionsecondary" })
                if (bf.ContainsKey(f)) db.Set(bio, keep.BioRow, bf[f], Get(copy.BioRow, f));
        }

        Trace?.Invoke("3. players index rows " + DateTime.Now.ToString("HH:mm:ss.fff"));
        // 3. players index rows: his and the copy's
        var players = db.Table(Roster.PlayersTable);
        var pf = players.Fields.ToDictionary(f => f.Name);
        var keepIdx = new HashSet<int>(); var copyIdx = new HashSet<int>();
        var copyPlayerRows = new HashSet<int>();
        for (int r = 0; r < players.WrittenRecords; r++)
        {
            if (db.IsInvalid(players, r)) continue;
            int id = Convert.ToInt32(db.Get(players, r, pf["playerid"])), ix = Convert.ToInt32(db.Get(players, r, pf["index"]));
            if (id == keep.GameId) keepIdx.Add(ix);
            else if (id == copy.GameId) { copyIdx.Add(ix); copyPlayerRows.Add(r); }
        }
        int mainIdx = keep.PlayerIndex >= 0 ? keep.PlayerIndex : keepIdx.DefaultIfEmpty(-1).Min();

        Trace?.Invoke("4. roster rows " + DateTime.Now.ToString("HH:mm:ss.fff"));
        // 4. roster rows: the copy's become his (his club rows go when the copy has the club spot; a team he'd be on twice
        //    keeps one row); lines rebuilt for every team touched
        var teamsTable = db.Table(Roster.TeamsTable);
        foreach (var tname in IndexTables)
        {
            var t = db.Table(tname);
            if (t == null) continue;
            var f = t.Fields.ToDictionary(x => x.Name);
            int Pi(int r) => Convert.ToInt32(db.Get(t, r, f["playerindex"]));
            int Tm(int r) => Convert.ToInt32(db.Get(t, r, f["team"]));
            var rows = Enumerable.Range(0, t.WrittenRecords).Where(r => !db.IsInvalid(t, r)).ToList();
            var copyRows = rows.Where(r => copyIdx.Contains(Pi(r))).ToList();
            var copyTeams = copyRows.Select(Tm).ToHashSet();
            var drop = new HashSet<int>();
            var touched = new HashSet<int>(copyTeams);
            foreach (int r in rows.Where(r => keepIdx.Contains(Pi(r))))
                if (copyTeams.Contains(Tm(r)) || copyOnClub && Pi(r) == mainIdx && !copyRows.Any(c => Tm(c) == Tm(r)) && IsClubRow(db, teamsTable, Tm(r)))
                { drop.Add(r); touched.Add(Tm(r)); }
            if (mainIdx >= 0) foreach (int r in copyRows) db.Set(t, r, f["playerindex"], mainIdx);
            else drop.UnionWith(copyRows);
            if (drop.Count > 0) RosterBaseline.RemoveRows(db, t, drop);
            (Touched.TryGetValue(tname, out var ts) ? ts : Touched[tname] = new HashSet<int>()).UnionWith(touched);
        }

        Trace?.Invoke("5. the free-agent list " + DateTime.Now.ToString("HH:mm:ss.fff"));
        // 5. the free-agent list: the copy's entries become his (UnlistSigned then drops a signed or doubled entry)
        if (db.Table(RosterSync.FreeAgentsTable) is { } fa && mainIdx >= 0)
        {
            var ff = fa.Fields.First(x => x.Name == "playerindex");
            for (int r = 0; r < fa.WrittenRecords; r++)
                if (!db.IsInvalid(fa, r) && copyIdx.Contains(Convert.ToInt32(db.Get(fa, r, ff)))) db.Set(fa, r, ff, mainIdx);
        }

        Trace?.Invoke("6. the copy " + DateTime.Now.ToString("HH:mm:ss.fff"));
        // 6. the copy's own rows go; everything else that names him by id now names the kept player
        RosterBaseline.RemoveRows(db, players, copyPlayerRows);
        foreach (var t in db.Tables.Where(t => t.Name.StartsWith("exhibition", StringComparison.OrdinalIgnoreCase) || t.Name.StartsWith("stock", StringComparison.OrdinalIgnoreCase)))
        {
            if (t.Name.Equals(Roster.PlayersTable, StringComparison.OrdinalIgnoreCase)) continue;
            var keys = t.Fields.Where(f => f.Type == T3FieldType.Integer && f.Name is "game_id" or "playerid").ToList();
            if (keys.Count == 0) continue;
            var hits = Enumerable.Range(0, t.WrittenRecords).Where(r => !db.IsInvalid(t, r) && keys.Any(k => Convert.ToInt32(db.Get(t, r, k)) == copy.GameId)).ToHashSet();
            Trace?.Invoke($"   {t.Name} {t.WrittenRecords} rows, {hits.Count} hits " + DateTime.Now.ToString("HH:mm:ss.fff"));
            if (hits.Count == 0) continue;
            if (PlayerRowTables.Contains(t.Name)) RosterBaseline.RemoveRows(db, t, hits);
            else foreach (int r in hits) foreach (var k in keys) if (Convert.ToInt32(db.Get(t, r, k)) == copy.GameId) db.Set(t, r, k, keep.GameId);
        }
        RosterSync.UnlistSigned(db);
        return $"{name} (kept id {keep.GameId}{(was != name ? $", was {was}" : "")}) <- {copy.Name} (id {copy.GameId}, removed){(copyOnClub ? "; team and contract from the copy" : "")}{(takeRatings ? "; ratings from the roster pack" : "")}";
    }

    private static bool IsClubRow(T3Db db, T3Table teams, int team)
    {
        var f = teams.Fields.ToDictionary(x => x.Name);
        for (int r = 0; r < teams.WrittenRecords; r++)
            if (!db.IsInvalid(teams, r) && Convert.ToInt32(db.Get(teams, r, f["index"])) == team)
                return RosterSync.IsClubTeamLeague(Convert.ToInt32(db.Get(teams, r, f["league"])));
        return false;
    }
}
