namespace NHL27Kit.Core;

/// <summary>One table a baseline roster replaces: rows before and after.</summary>
public sealed record BaselineTable(string Name, int GameRows, int SaveRows);

/// <summary>A Create-a-Player player in the baseline roster and what converting him does.</summary>
public sealed class BaselineCreated
{
    public int CapId { get; init; }
    public string Name { get; init; }
    public bool IsGoalie { get; init; }
    public DateTime? Born { get; init; }
    public int Team { get; init; }
    public string TeamName { get; init; }
    public int League { get; init; }
    /// <summary>The existing real player he's a copy of (same name and birth date), or -1 when he's new.</summary>
    public int MatchId { get; set; } = -1;
    public string MatchTeam { get; set; }
    /// <summary>Matched by name, birth year and rights rather than the exact birth date (the roster maker's date differs).</summary>
    public bool LooseMatch { get; set; }
    /// <summary>Why he's left as a Create-a-Player player (credit entry, custom team…); null when he's converted.</summary>
    public string Skip { get; set; }
    /// <summary>His real game id once converted (the match's, or a new one).</summary>
    public int NewId { get; set; } = -1;
}

/// <summary>What a baseline roster would change, before anything is written.</summary>
public sealed class BaselinePlan
{
    public List<BaselineTable> Tables { get; } = new();
    /// <summary>Tables in the save that can't be copied (layout differs, or too many rows), with why.</summary>
    public List<string> Problems { get; } = new();
    public int PlayersBoth, PlayersAdded, PlayersDropped;
    public List<string> Added { get; } = new();
    public List<string> Dropped { get; } = new();
    public List<string> TeamsDropped { get; } = new();
    public List<BaselineCreated> Created { get; } = new();
}

/// <summary>
/// A shared roster (a RosterCustomN save, RosterSave) as the game's new base roster, ahead of NHL Sync.
///
/// 1. <see cref="CopyTables"/>: every table the save carries (players, ratings, rosters and lines, teams, gear, AI,
///    jerseys, arenas, draft picks, retained salary, Create-a-Player…) replaces the game's whole, row for row: the
///    tables point at each other by row (a roster row's playerindex, a player's index), so a merge would break them.
///    The game's other tables (schedules, career stats, awards, name calls, franchise tables) are kept. The online
///    copies the game plays from, customonlinerostertable and customonlineteamstrategies, are rebuilt from the copied
///    exhibitionrostertable and exhibitionteamstrategies. None of the copied tables keep strings in a separate area,
///    so rows copy as bytes.
/// 2. <see cref="ConvertCreated"/>: Create-a-Player players on a real team become real players. Franchise mode only
///    loads players on club teams, so roster makers put Create-a-Player copies of national-team-only prospects on
///    clubs (Keaton Verhoeff) and create missing prospects (Landon DuPont). A copy of an existing player (same name
///    and birth date) moves the real one onto the copy's roster row and gives him the copy's ratings, keeping his id,
///    face, portrait, draft, rights and contract; anyone else becomes a new real player (bio, ratings, gear, a players
///    row) on that roster row. Either way the Create-a-Player rows are removed, so he isn't also a free agent. Credit
///    entries ("- Vasy Roster"), national teams and Create-a-Player teams are left as they are.
/// </summary>
public static class RosterBaseline
{
    public const string CapBio = "customplayerbiotable", CapPlayers = "customplayers";
    public const string OnlineStrategies = "customonlineteamstrategies", Strategies = "exhibitionteamstrategies";
    /// <summary>
    /// A roster's player tables: who the players are, their ratings, gear, AI and roster spots (regular and
    /// Create-a-Player). Everything else in a roster is team-level (teams, strategies, jerseys, arenas, team gear, draft
    /// picks, salary extras, coaches). Used to import one side at a time when a game version can't take all of a roster.
    /// </summary>
    public static readonly HashSet<string> PlayerTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "exhibitionplayerbiotable", "exhibitionplayers", "exhibitionskaterai", "exhibitiongoalieai", "exhibitionskaterequipment",
        "exhibitiongoalieequipment", "exhibitionrostertable", "exhibitionfreeagents", "exhibitionresortskaterextras",
        "exhibitionresortgoalieextras", "exhibitionresortplayerextras", "stockgoalieequipment",
        "customplayerbiotable", "customplayers", "customskaterai", "customgoalieai", "customskaterequipment",
        "customgoalieequipment", "customrostertable", "customfreeagents",
    };
    /// <summary>exhibitionteams.league of the national junior teams (franchise doesn't load them).</summary>
    public const int NationalJuniorLeague = 24;
    /// <summary>Create-a-Player tables and their real counterparts, by what they hold.</summary>
    private static readonly (string Cap, string Real)[] Pairs =
    {
        ("customskaterai", Roster.SkaterTable), ("customgoalieai", Roster.GoalieTable),
        ("customskaterequipment", "exhibitionskaterequipment"), ("customgoalieequipment", "exhibitiongoalieequipment"),
    };

    // ---------------------------------------------------------------- planning

    /// <param name="only">The tables the roster replaces (a pack's), when <paramref name="save"/> holds more (a pack applied to a copy of the game's roster).</param>
    public static BaselinePlan Plan(T3Db game, T3Db save, ISet<string> only = null)
    {
        var plan = new BaselinePlan();
        foreach (var s in save.Tables)
        {
            if (only != null && !only.Contains(s.Name)) continue;
            var g = game.Table(s.Name);
            if (g == null) { plan.Problems.Add($"{s.Name}: not in the game"); continue; }
            if (Why(g, s) is { } why) { plan.Problems.Add($"{s.Name}: {why}"); continue; }
            plan.Tables.Add(new BaselineTable(s.Name, g.WrittenRecords, s.WrittenRecords));
        }
        var gb = Bios(game, Roster.BioTable);
        var sb = Bios(save, Roster.BioTable);
        plan.PlayersBoth = sb.Keys.Count(gb.ContainsKey);
        foreach (var (id, b) in sb.Where(kv => !gb.ContainsKey(kv.Key)).OrderBy(kv => kv.Key)) { plan.PlayersAdded++; plan.Added.Add($"{b.Name} ({id})"); }
        foreach (var (id, b) in gb.Where(kv => !sb.ContainsKey(kv.Key)).OrderBy(kv => kv.Key)) { plan.PlayersDropped++; plan.Dropped.Add($"{b.Name} ({id})"); }
        var gt = new Roster(game).Teams;
        var st = new Roster(save).Teams;
        foreach (var (i, t) in gt.Where(kv => !st.ContainsKey(kv.Key))) plan.TeamsDropped.Add($"{t.Name} ({i})");
        plan.Created.AddRange(Created(save));
        return plan;
    }

    /// <summary>Why a save table can't replace the game's (null when it can).</summary>
    private static string Why(T3Table g, T3Table s)
    {
        if (s.RecordSize != g.RecordSize || s.Fields.Count != g.Fields.Count) return $"layout differs ({s.RecordSize}/{s.Fields.Count} vs the game's {g.RecordSize}/{g.Fields.Count})";
        if (s.WrittenRecords > g.Records) return $"{s.WrittenRecords} rows, the game has room for {g.Records}";
        if (s.StringsLength > 0 || g.StringsLength > 0) return "keeps strings in a separate area";
        return null;
    }

    private static Dictionary<int, (int Row, string Name)> Bios(T3Db db, string table)
    {
        var t = db.Table(table);
        var f = t.Fields.ToDictionary(x => x.Name);
        var map = new Dictionary<int, (int, string)>();
        for (int r = 0; r < t.WrittenRecords; r++)
            if (!db.IsInvalid(t, r)) map.TryAdd((int)db.Get(t, r, f["game_id"]), (r, $"{db.Get(t, r, f["firstname"])} {db.Get(t, r, f["lastname"])}".Trim()));
        return map;
    }

    /// <summary>The Create-a-Player players on a team in <paramref name="db"/>'s rosters, each with whom he copies (if
    /// anyone) and whether he'll be converted.</summary>
    public static List<BaselineCreated> Created(T3Db db)
    {
        var list = new List<BaselineCreated>();
        var cbio = db.Table(CapBio);
        var cpl = db.Table(CapPlayers);
        if (cbio == null || cpl == null) return list;
        var bf = cbio.Fields.ToDictionary(x => x.Name);
        var pf = cpl.Fields.ToDictionary(x => x.Name);
        // Create-a-Player players (customplayers.table 1), by their index (= their id)
        var capIndex = new Dictionary<int, int>();
        for (int r = 0; r < cpl.WrittenRecords; r++)
            if (!db.IsInvalid(cpl, r) && (int)db.Get(cpl, r, pf["table"]) == 1)
                capIndex[(int)db.Get(cpl, r, pf["index"])] = (int)db.Get(cpl, r, pf["playerid"]);
        var goalies = Ids(db, "customgoalieai");
        var rt = db.Table(Roster.RosterTable);
        var rf = rt.Fields.ToDictionary(x => x.Name);
        var teamOf = new Dictionary<int, int>();
        for (int r = 0; r < rt.WrittenRecords; r++)
            if (!db.IsInvalid(rt, r) && capIndex.TryGetValue((int)db.Get(rt, r, rf["playerindex"]), out int id)) teamOf.TryAdd(id, (int)db.Get(rt, r, rf["team"]));
        var roster = new Roster(db);
        var matcher = new PlayerMatcher(db, roster);
        var leagues = Leagues(db);
        for (int r = 0; r < cbio.WrittenRecords; r++)
        {
            if (cbio == null || db.IsInvalid(cbio, r)) continue;
            int id = (int)db.Get(cbio, r, bf["game_id"]);
            if (!teamOf.TryGetValue(id, out int team)) continue;
            string first = (string)db.Get(cbio, r, bf["firstname"]), last = (string)db.Get(cbio, r, bf["lastname"]);
            int y = (int)db.Get(cbio, r, bf["year"]) + 1900, m = (int)db.Get(cbio, r, bf["month"]) + 1, d = (int)db.Get(cbio, r, bf["day"]) + 1;
            DateTime? born = y > 1900 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m) ? new DateTime(y, m, d) : null;
            var c = new BaselineCreated
            {
                CapId = id, Name = $"{first} {last}".Trim(), IsGoalie = goalies.Contains(id), Born = born, Team = team,
                TeamName = roster.Teams.TryGetValue(team, out var tm) && !string.IsNullOrEmpty(tm.Name) ? tm.Name : $"team {team}",
                League = leagues.GetValueOrDefault(team, -1),
            };
            if (c.Name.StartsWith('-') || c.Name.Length == 0) c.Skip = "credit entry";
            // only the game's own teams (exhibitionteams), not Create-a-Player teams
            else if (!leagues.ContainsKey(team)) c.Skip = "on a Create-a-Player team";
            else if (c.League == NationalJuniorLeague || c.League == 10) c.Skip = "on a national team";
            // a copy of an existing player: same name and birth date; or, since roster makers mistype dates (Michael Hage
            // entered as Aug 14 for Apr 14), the same name, the same birth year and the same NHL rights holder (or the
            // existing player only on a national team, which is why the copy was made). A shared name alone isn't
            // enough: Ryan Miller the 1980 goalie and a 2007 prospect are two people.
            var (p, nameOnly) = matcher.Find(first, last, born);
            if (p != null && nameOnly) p = null;
            if (p == null && born is { } b)
            {
                int capRights = (int)db.Get(cbio, r, bf["proteam"]);
                p = roster.Players.Where(x => x.GameId < PlayerCreator.SpecialIdStart && RosterSync.Norm(x.FirstName) + RosterSync.Norm(x.LastName) == RosterSync.Norm(first) + RosterSync.Norm(last))
                    .Where(x => matcher.Born(x)?.Year == b.Year)
                    .Where(x => Rights(db, x) is int xr && xr >= 0 && xr == capRights || leagues.GetValueOrDefault(x.Team, -1) is NationalJuniorLeague or 10)
                    .Take(2).ToList() is { Count: 1 } one ? one[0] : null;
                if (p != null) c.LooseMatch = true;
            }
            if (p != null)
            {
                c.MatchId = p.GameId;
                c.MatchTeam = roster.Teams.TryGetValue(p.Team, out var mt) ? mt.Name : $"team {p.Team}";
            }
            list.Add(c);
        }
        return list.OrderBy(c => c.Skip != null).ThenBy(c => c.League).ThenBy(c => c.TeamName).ThenBy(c => c.Name).ToList();
    }

    /// <summary>The NHL club holding a player's rights (bio proteam), or -1.</summary>
    private static int Rights(T3Db db, RosterPlayer p)
    {
        var bio = db.Table(Roster.BioTable);
        var f = bio.Fields.FirstOrDefault(x => x.Name == "proteam");
        return f == null ? -1 : (int)db.Get(bio, p.BioRow, f);
    }

    private static HashSet<int> Ids(T3Db db, string table)
    {
        var set = new HashSet<int>();
        var t = db.Table(table);
        if (t == null) return set;
        var f = t.Fields.First(x => x.Name == "game_id");
        for (int r = 0; r < t.WrittenRecords; r++) if (!db.IsInvalid(t, r)) set.Add((int)db.Get(t, r, f));
        return set;
    }

    private static Dictionary<int, int> Leagues(T3Db db)
    {
        var t = db.Table(Roster.TeamsTable);
        var f = t.Fields.ToDictionary(x => x.Name);
        var map = new Dictionary<int, int>();
        for (int r = 0; r < t.WrittenRecords; r++)
            if (!db.IsInvalid(t, r)) map.TryAdd((int)db.Get(t, r, f["index"]), (int)db.Get(t, r, f["league"]));
        return map;
    }

    // ---------------------------------------------------------------- the layered import (step 1)

    /// <summary>A roster's Create-a-Player tables: the players the conversion works from.</summary>
    public static readonly HashSet<string> CreatedTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "customplayerbiotable", "customplayers", "customskaterai", "customgoalieai", "customskaterequipment",
        "customgoalieequipment", "customfreeagents", "customrostertable",
    };

    /// <summary>What the layered import did (or, on a copy, would do).</summary>
    public sealed class LayeredResult
    {
        public int RatingsAndBios, Added;
        public string Spots = "", Converted = "", Cleaned;
        public List<BaselineCreated> Created = new();
        /// <summary>References left pointing at nothing (none expected); the app refuses to write when there are any.</summary>
        public List<string> Problems = new();
        public string Summary(string pack) =>
            $"Baseline roster (layered) from {pack}: ratings and bios {RatingsAndBios} rows; {Added} players added; {Spots}; {Converted}" + (Cleaned != null ? "; " + Cleaned : "");
    }

    /// <summary>
    /// Step 1: a roster pack onto the game's own roster, in layers, keeping the game's own player indexes. Copying a
    /// roster's tables whole crashes franchise creation in NHL 27 (the game's other tables point at players by index), and
    /// so does the roster loaded as it is on an older game version; built up this way it starts (tested 2026-11-09):
    /// 1. ratings and bios of the players both rosters have, by game_id;
    /// 2. the roster's regular players the game doesn't have, added (bio, ratings, gear, extras, players rows);
    /// 3. its Create-a-Player tables;
    /// 4. its roster spots and lines, each row matched to the same player and version (club, national team, All-Star…)
    ///    in the game, bio teams following the club rows, the online roster rebuilt;
    /// 5. its Create-a-Player players converted (new real players, or copies merged into the real one), orphaned career
    ///    stats removed, and every player and team reference checked.
    /// Team tables (teams, strategies, jerseys, arenas, team gear, picks, salary extras) and gear for existing players
    /// stay the game's own.
    /// </summary>
    public static LayeredResult ImportLayered(T3Db game, string packDir, string metaXml, Action<string, int, int> progress = null)
    {
        var result = new LayeredResult();
        progress?.Invoke("Reading the roster pack", 0, 0);
        // the pack applied to a copy: the rows each layer takes, read before the game changes
        var source = RosterPack.ToDb(packDir, game, metaXml);
        using var batch = game.Batch();
        progress?.Invoke("Ratings and bios", 0, 0);
        result.RatingsAndBios = ApplyLayer(game, source, new[] { Roster.SkaterTable, Roster.GoalieTable, Roster.BioTable });
        progress?.Invoke("Adding the roster's players the game doesn't have", 0, 0);
        result.Added = AddMissingPlayers(game, source);
        progress?.Invoke("Create-a-Player tables", 0, 0);
        RosterPack.Import(packDir, game, null, CreatedTables);
        progress?.Invoke("Roster spots and lines", 0, 0);
        result.Spots = ApplyRosterSpots(game, source, withCreated: true);
        result.Created = Created(game);
        result.Converted = ConvertCreated(game, result.Created, progress);
        result.Cleaned = RemoveOrphanStats(game);
        progress?.Invoke("Checking every player and team reference", 0, 0);
        result.Problems = CheckReferences(game);
        return result;
    }

    // ---------------------------------------------------------------- copying

    /// <summary>Replaces each table the save carries (that fits) with the save's rows, then rebuilds the online copies.</summary>
    public static string CopyTables(T3Db game, T3Db save)
    {
        using var batch = game.Batch();
        int n = 0;
        foreach (var s in save.Tables)
        {
            var g = game.Table(s.Name);
            if (g == null || Why(g, s) != null) continue;
            Replace(game, g, i => save.RecordBytes(s, i), s.WrittenRecords);
            n++;
        }
        var notes = new List<string> { $"{n} tables copied" };
        if (CopyWithin(game, Roster.RosterTable, RosterSync.OnlineRosterTable)) notes.Add("online roster rebuilt");
        if (CopyWithin(game, Strategies, OnlineStrategies)) notes.Add("online team strategies rebuilt");
        return "Baseline roster: " + string.Join(", ", notes);
    }

    /// <summary>
    /// Step 1 from a roster pack (RosterPack): each table the pack holds replaces the game's, field by field, then the
    /// online copies are rebuilt from them. The same result as <see cref="CopyTables"/> from the save it was exported from.
    /// </summary>
    public static string ImportPack(T3Db game, string packDir, Action<string, int, int> progress = null, ISet<string> only = null)
    {
        // one batch: each table's CRC is computed once at the end, not after every row
        using var batch = game.Batch();
        var tables = RosterPack.Import(packDir, game, progress, only);
        progress?.Invoke("Rebuilding the online roster", 0, 0);
        var notes = new List<string> { $"{tables.Count} tables from the roster pack" };
        if (CopyWithin(game, Roster.RosterTable, RosterSync.OnlineRosterTable)) notes.Add("online roster rebuilt");
        if (CopyWithin(game, Strategies, OnlineStrategies)) notes.Add("online team strategies rebuilt");
        return "Baseline roster: " + string.Join(", ", notes);
    }

    /// <summary>One table's rows copied over another of the same layout (the game's online copies).</summary>
    private static bool CopyWithin(T3Db db, string from, string to)
    {
        var f = db.Table(from);
        var t = db.Table(to);
        if (f == null || t == null || Why(t, f) != null) return false;
        Replace(db, t, i => db.RecordBytes(f, i), f.WrittenRecords);
        return true;
    }

    /// <summary>Sets a table to <paramref name="count"/> rows, row i from <paramref name="row"/>.</summary>
    private static void Replace(T3Db db, T3Table t, Func<int, byte[]> row, int count)
    {
        var rows = Enumerable.Range(0, count).Select(row).ToList();
        db.SetWritten(t, count);
        for (int i = 0; i < count; i++) db.WriteRecord(t, i, rows[i]);
    }

    /// <summary>Removes rows from a table, the rows after them moving up (tables keyed by id, not position).</summary>
    private static void RemoveRows(T3Db db, T3Table t, ISet<int> remove)
    {
        if (remove.Count == 0) return;
        var keep = Enumerable.Range(0, t.WrittenRecords).Where(i => !remove.Contains(i)).Select(i => db.RecordBytes(t, i)).ToList();
        Replace(db, t, i => keep[i], keep.Count);
    }

    /// <summary>Appends a row (a copy of <paramref name="bytes"/>) and returns its number.</summary>
    private static int AddRow(T3Db db, T3Table t, byte[] bytes)
    {
        int at = t.WrittenRecords;
        db.SetWritten(t, at + 1);
        db.WriteRecord(t, at, bytes);
        return at;
    }

    /// <summary>Copies row <paramref name="srow"/> of <paramref name="src"/> over row <paramref name="drow"/> of
    /// <paramref name="dst"/>: as bytes when the layouts match, else field by field by name.</summary>
    private static void CopyRow(T3Db db, T3Table src, int srow, T3Table dst, int drow)
    {
        if (src.RecordSize == dst.RecordSize && src.Fields.Select(f => (f.Name, f.BitOffset, f.Depth, f.Type)).SequenceEqual(dst.Fields.Select(f => (f.Name, f.BitOffset, f.Depth, f.Type))))
        {
            db.WriteRecord(dst, drow, db.RecordBytes(src, srow));
            return;
        }
        var sf = src.Fields.ToDictionary(x => x.Name);
        foreach (var f in dst.Fields)
            if (sf.TryGetValue(f.Name, out var from) && from.Type == f.Type)
                try { db.Set(dst, drow, f, db.Get(src, srow, from)); } catch (ArgumentException) { }
    }

    private static int FindRow(T3Db db, T3Table t, string field, int value)
    {
        var f = t.Fields.First(x => x.Name == field);
        for (int r = 0; r < t.WrittenRecords; r++)
            if (!db.IsInvalid(t, r) && (int)db.Get(t, r, f) == value) return r;
        return -1;
    }

    private static List<int> FindRows(T3Db db, T3Table t, string field, int value)
    {
        var f = t.Fields.First(x => x.Name == field);
        var rows = new List<int>();
        for (int r = 0; r < t.WrittenRecords; r++)
            if (!db.IsInvalid(t, r) && (int)db.Get(t, r, f) == value) rows.Add(r);
        return rows;
    }

    // ---------------------------------------------------------------- converting Create-a-Player players

    /// <summary>
    /// Converts the Create-a-Player players <see cref="Created"/> lists without a Skip (after <see cref="CopyTables"/>,
    /// so <paramref name="db"/> holds the save's rows). Returns a summary; each player's NewId is set.
    /// </summary>
    /// <param name="progress">Reports each player as he's converted: (name, number, of).</param>
    public static string ConvertCreated(T3Db db, IReadOnlyList<BaselineCreated> players, Action<string, int, int> progress = null)
    {
        using var batch = db.Batch();
        // value -> rows, per table and field, built once: every lookup below is for a row that existed before converting
        // started (added rows are never looked up), so a snapshot serves instead of scanning the table each time
        var lookups = new Dictionary<(T3Table, string), Dictionary<int, List<int>>>();
        List<int> Rows(T3Table t, string field, int value)
        {
            if (t == null) return new List<int>();
            if (!lookups.TryGetValue((t, field), out var map))
            {
                map = new Dictionary<int, List<int>>();
                var f = t.Fields.First(x => x.Name == field);
                for (int r = 0; r < t.WrittenRecords; r++)
                    if (!db.IsInvalid(t, r)) { int v = (int)db.Get(t, r, f); if (!map.TryGetValue(v, out var l)) map[v] = l = new List<int>(); l.Add(r); }
                lookups[(t, field)] = map;
            }
            return map.TryGetValue(value, out var rows) ? rows : new List<int>();
        }
        int Row(T3Table t, string field, int value) => Rows(t, field, value) is { Count: > 0 } l ? l[0] : -1;
        var bio = db.Table(Roster.BioTable);
        var bf = bio.Fields.ToDictionary(x => x.Name);
        var cbio = db.Table(CapBio);
        var pl = db.Table(Roster.PlayersTable);
        var pf = pl.Fields.ToDictionary(x => x.Name);
        var cpl = db.Table(CapPlayers);
        var cpf = cpl.Fields.ToDictionary(x => x.Name);
        var rt = db.Table(Roster.RosterTable);
        var rf = rt.Fields.ToDictionary(x => x.Name);

        int nextId = 1 + MaxBelow(db, bio, "game_id", PlayerCreator.SpecialIdStart);
        int nextIndex = 1 + MaxBelow(db, pl, "index", PlayerCreator.SpecialIdStart);
        int created = 0, moved = 0;
        var capRowsToRemove = new Dictionary<string, HashSet<int>>();
        void Drop(string table, int row) { if (row >= 0) (capRowsToRemove.TryGetValue(table, out var s) ? s : capRowsToRemove[table] = new HashSet<int>()).Add(row); }
        var realRosterRowsToRemove = new HashSet<int>();

        var todo = players.Where(c => c.Skip == null).ToList();
        int done = 0;
        foreach (var c in todo)
        {
            progress?.Invoke(c.Name, ++done, todo.Count);
            // his Create-a-Player roster row(s): playerindex = his id
            var capRosterRows = Rows(rt, "playerindex", c.CapId);
            if (capRosterRows.Count == 0) { c.Skip = "no roster row after the copy"; continue; }
            int capBioRow = Row(cbio, "game_id", c.CapId);
            int targetIndex;
            if (c.MatchId >= 0)
            {
                // the real player takes over the copy's roster row; his own roster rows go
                // his club version (players table 0), not a national-team version of him
                int idxRow = Rows(pl, "playerid", c.MatchId).Where(r => (int)db.Get(pl, r, pf["table"]) == 0).DefaultIfEmpty(-1).First();
                if (idxRow < 0) { c.Skip = "his real players row is missing"; continue; }
                targetIndex = (int)db.Get(pl, idxRow, pf["index"]);
                foreach (int r in Rows(rt, "playerindex", targetIndex)) realRosterRowsToRemove.Add(r);
                // the copy's ratings over his (Vasy's ratings), keeping his id; his gear and face stay
                foreach (var (cap, real) in Pairs.Where(p => p.Cap.Contains("ai")))
                {
                    var ct = db.Table(cap); var rtb = db.Table(real);
                    int cr = Row(ct, "game_id", c.CapId), rr = Row(rtb, "game_id", c.MatchId);
                    if (cr < 0 || rr < 0) continue;
                    CopyRow(db, ct, cr, rtb, rr);
                    db.Set(rtb, rr, rtb.Fields.First(f => f.Name == "game_id"), c.MatchId);
                }
                int realBio = Row(bio, "game_id", c.MatchId);
                if (realBio >= 0) db.Set(bio, realBio, bf["team"], c.Team);
                c.NewId = c.MatchId;
                moved++;
            }
            else
            {
                // a new real player: bio, ratings, gear and a players row under a new id
                int id = nextId++;
                if (capBioRow < 0) { c.Skip = "no Create-a-Player bio"; continue; }
                int newBio = AddRow(db, bio, db.RecordBytes(bio, 0));
                CopyRow(db, cbio, capBioRow, bio, newBio);
                db.Set(bio, newBio, bf["game_id"], id);
                db.Set(bio, newBio, bf["team"], c.Team);
                // Create-a-Player's default birthplace ("$BURNABY") isn't his: left blank
                if (bf.TryGetValue("birthplace", out var bp) && db.Get(bio, newBio, bp) is string place && place.StartsWith('$')) db.Set(bio, newBio, bp, "");
                foreach (var (cap, real) in Pairs)
                {
                    var ct = db.Table(cap); var rtb = db.Table(real);
                    int cr = ct == null ? -1 : Row(ct, "game_id", c.CapId);
                    if (cr < 0 || rtb == null) continue;
                    int nr = AddRow(db, rtb, db.RecordBytes(rtb, 0));
                    CopyRow(db, ct, cr, rtb, nr);
                    db.Set(rtb, nr, rtb.Fields.First(f => f.Name == "game_id"), id);
                }
                targetIndex = nextIndex++;
                int prow = AddRow(db, pl, db.RecordBytes(pl, 0));
                db.Set(pl, prow, pf["index"], targetIndex);
                db.Set(pl, prow, pf["playerid"], id);
                db.Set(pl, prow, pf["table"], 0);
                c.NewId = id;
                created++;
            }
            foreach (int r in capRosterRows) db.Set(rt, r, rf["playerindex"], targetIndex);
            // his Create-a-Player rows go, so he isn't also a free agent in the Create-a-Player list
            Drop(CapBio, capBioRow);
            // every customplayers row of his (a few have two), or one left behind points at a removed bio
            foreach (int cr in Rows(cpl, "playerid", c.CapId)) Drop(CapPlayers, cr);
            Drop(CapPlayers, Row(cpl, "index", c.CapId));
            foreach (var (cap, _) in Pairs) if (db.Table(cap) is { } ct) Drop(cap, Row(ct, "game_id", c.CapId));
        }
        progress?.Invoke("Removing the Create-a-Player copies", 0, 0);
        RemoveRows(db, rt, realRosterRowsToRemove);
        // the removed customplayers rows' indexes: Create-a-Player teams' rosters (customrostertable) can hold him too
        // (a converted player who was also on one), and a roster row left pointing at a removed index points at nothing
        if (capRowsToRemove.TryGetValue(CapPlayers, out var gonePlayers) && db.Table("customrostertable") is { } crt)
        {
            var goneIndexes = gonePlayers.Select(r => (int)db.Get(cpl, r, cpf["index"])).ToHashSet();
            var cf = crt.Fields.First(f => f.Name == "playerindex");
            var stale = Enumerable.Range(0, crt.WrittenRecords).Where(r => !db.IsInvalid(crt, r) && goneIndexes.Contains((int)db.Get(crt, r, cf))).ToHashSet();
            RemoveRows(db, crt, stale);
        }
        foreach (var (table, rows) in capRowsToRemove) RemoveRows(db, db.Table(table), rows);
        // the online roster again, now that rows moved
        CopyWithin(db, Roster.RosterTable, RosterSync.OnlineRosterTable);
        return $"Create-a-Player: {created} made real players, {moved} copies merged into the real player";
    }

    /// <summary>
    /// One layer of a roster onto the game's, for existing players only: each row of <paramref name="tables"/> in the
    /// game whose game_id is also in <paramref name="source"/> (a pack applied to a copy) takes the source's row. No rows
    /// are added or removed. Returns how many rows changed.
    /// </summary>
    /// <param name="keep">Fields left as the game has them (a bio's team, when roster spots aren't part of the layer).</param>
    public static int ApplyLayer(T3Db game, T3Db source, IEnumerable<string> tables, ICollection<string> keep = null)
    {
        using var batch = game.Batch();
        int changed = 0;
        foreach (var name in tables)
        {
            var g = game.Table(name); var s = source.Table(name);
            if (g == null || s == null || Why(g, s) != null) continue;
            var sf = s.Fields.First(f => f.Name == "game_id"); var gf = g.Fields.First(f => f.Name == "game_id");
            var rowOf = new Dictionary<int, int>();
            for (int r = 0; r < s.WrittenRecords; r++) if (!source.IsInvalid(s, r)) rowOf.TryAdd((int)source.Get(s, r, sf), r);
            for (int r = 0; r < g.WrittenRecords; r++)
            {
                if (game.IsInvalid(g, r) || !rowOf.TryGetValue((int)game.Get(g, r, gf), out int sr)) continue;
                var bytes = source.RecordBytes(s, sr);
                var before = game.RecordBytes(g, r);
                if (bytes.AsSpan().SequenceEqual(before)) continue;
                var kept = (keep ?? Array.Empty<string>()).Select(k => g.Fields.FirstOrDefault(f => f.Name == k)).Where(f => f != null)
                    .Select(f => (f, game.Get(g, r, f))).ToList();
                game.WriteRecord(g, r, bytes);
                foreach (var (f, v) in kept) game.Set(g, r, f, v);
                if (!game.RecordBytes(g, r).AsSpan().SequenceEqual(before)) changed++;
            }
        }
        return changed;
    }

    /// <summary>
    /// The source's regular players the game doesn't have (game id below the special range, no bio in the game): each
    /// one's bio, ratings, gear and extras rows (by game_id) and his players-table rows are copied in. A players-table
    /// index the game already uses for someone else gets the next free one. Their roster spots come with
    /// <see cref="ApplyRosterSpots"/>. Returns how many players were added.
    /// </summary>
    public static int AddMissingPlayers(T3Db game, T3Db source)
    {
        using var batch = game.Batch();
        var have = new HashSet<int>();
        var gb = game.Table(Roster.BioTable); var gbf = gb.Fields.First(f => f.Name == "game_id");
        for (int r = 0; r < gb.WrittenRecords; r++) if (!game.IsInvalid(gb, r)) have.Add((int)game.Get(gb, r, gbf));
        var sb = source.Table(Roster.BioTable); var sbf = sb.Fields.First(f => f.Name == "game_id");
        var add = new HashSet<int>();
        for (int r = 0; r < sb.WrittenRecords; r++)
            if (!source.IsInvalid(sb, r) && (int)source.Get(sb, r, sbf) is int id && id < PlayerCreator.SpecialIdStart && !have.Contains(id)) add.Add(id);
        if (add.Count == 0) return 0;
        foreach (var name in new[] { Roster.BioTable, Roster.SkaterTable, Roster.GoalieTable, "exhibitionskaterequipment", "exhibitiongoalieequipment",
                                     "exhibitionresortskaterextras", "exhibitionresortgoalieextras", "exhibitionresortplayerextras" })
        {
            var g = game.Table(name); var s = source.Table(name);
            if (g == null || s == null || Why(g, s) != null) continue;
            var f = s.Fields.FirstOrDefault(x => x.Name == "game_id");
            if (f == null) continue;
            for (int r = 0; r < s.WrittenRecords; r++)
                if (!source.IsInvalid(s, r) && add.Contains((int)source.Get(s, r, f))) AddRow(game, g, source.RecordBytes(s, r));
        }
        var gp = game.Table(Roster.PlayersTable); var gpf = gp.Fields.ToDictionary(x => x.Name);
        var sp = source.Table(Roster.PlayersTable); var spf = sp.Fields.ToDictionary(x => x.Name);
        var used = new HashSet<int>();
        for (int r = 0; r < gp.WrittenRecords; r++) if (!game.IsInvalid(gp, r)) used.Add((int)game.Get(gp, r, gpf["index"]));
        int next = 1 + MaxBelow(game, gp, "index", PlayerCreator.SpecialIdStart);
        for (int r = 0; r < sp.WrittenRecords; r++)
        {
            if (source.IsInvalid(sp, r) || !add.Contains((int)source.Get(sp, r, spf["playerid"]))) continue;
            int at = AddRow(game, gp, source.RecordBytes(sp, r));
            int index = (int)source.Get(sp, r, spf["index"]);
            if (!used.Add(index)) game.Set(gp, at, gpf["index"], next++);
        }
        return add.Count;
    }

    /// <summary>
    /// A roster's spots and lines for the game's existing players: the source's roster rows (team, number, line spots,
    /// scratches), each mapped from its player index to the same player (by id) in the game, replace the game's roster
    /// table; rows for players the game doesn't have (Create-a-Player, new players) are left out. Each player's bio team
    /// follows his row, a player with no row any more is left without a team, and the online roster is rebuilt.
    /// </summary>
    /// <param name="withCreated">The roster's Create-a-Player tables came in too: rows for its Create-a-Player players are kept.</param>
    public static string ApplyRosterSpots(T3Db game, T3Db source, bool withCreated = false)
    {
        using var batch = game.Batch();
        // A player has one players-table entry per version of him (table 0: his club self; 2 and 3: his national-team and
        // other versions), each with its own index and roster row: rows are matched by player and version together.
        // A version can have several entries too (a player on each of several event teams: one index per team), so a row
        // keeps its own index when the game has that index for the same player and version (both rosters come from the
        // same base data), and otherwise takes one of the game's entries for that player and version not yet used.
        (Dictionary<int, (int Id, int Version)> Key, Dictionary<(int, int), List<int>> Indexes) Versions(T3Db db)
        {
            var t = db.Table(Roster.PlayersTable); var f = t.Fields.ToDictionary(x => x.Name);
            var key = new Dictionary<int, (int, int)>();
            var indexes = new Dictionary<(int, int), List<int>>();
            for (int r = 0; r < t.WrittenRecords; r++)
                if (!db.IsInvalid(t, r))
                {
                    int i = (int)db.Get(t, r, f["index"]), id = (int)db.Get(t, r, f["playerid"]), v = (int)db.Get(t, r, f["table"]);
                    if (!key.TryAdd(i, (id, v))) continue;
                    if (!indexes.TryGetValue((id, v), out var l)) indexes[(id, v)] = l = new List<int>();
                    l.Add(i);
                }
            return (key, indexes);
        }
        var (sourceKey, _) = Versions(source);
        var (gameKey, gameIndexes) = Versions(game);
        var usedIndexes = new HashSet<int>();
        int Target(int si, (int Id, int Version) k)
        {
            if (gameKey.TryGetValue(si, out var gk) && gk == k && usedIndexes.Add(si)) return si;
            if (gameIndexes.TryGetValue(k, out var list))
                foreach (int gi in list) if (!gameKey.ContainsKey(gi) || gameKey[gi] == k) if (usedIndexes.Add(gi)) return gi;
            return -1;
        }
        var st = source.Table(Roster.RosterTable); var sf = st.Fields.ToDictionary(x => x.Name);
        var gt = game.Table(Roster.RosterTable); var gf = gt.Fields.ToDictionary(x => x.Name);
        var rows = new List<byte[]>();
        var targets = new List<int>();
        var teamOf = new Dictionary<int, int>();
        var nhlClubs = RosterSync.TeamIndex(game).Values.ToHashSet();
        var leagues = Leagues(game);
        int skipped = 0;
        // Create-a-Player players in the game (customplayers.table 1), when their tables came in with the layer
        var capIndexes = new HashSet<int>();
        if (withCreated && game.Table(CapPlayers) is { } cpt)
        {
            var cf = cpt.Fields.ToDictionary(x => x.Name);
            for (int r = 0; r < cpt.WrittenRecords; r++)
                if (!game.IsInvalid(cpt, r) && (int)game.Get(cpt, r, cf["table"]) == 1) capIndexes.Add((int)game.Get(cpt, r, cf["index"]));
        }
        for (int r = 0; r < st.WrittenRecords; r++)
        {
            if (source.IsInvalid(st, r)) continue;
            int si = (int)source.Get(st, r, sf["playerindex"]);
            // a Create-a-Player player's row (his index is in the game's customplayers, copied from the same roster): kept as is
            if (capIndexes.Contains(si)) { rows.Add(source.RecordBytes(st, r)); targets.Add(si); continue; }
            if (!sourceKey.TryGetValue(si, out var k) || Target(si, k) is not (>= 0 and var gi)) { skipped++; continue; }
            rows.Add(source.RecordBytes(st, r));
            targets.Add(gi);
            // his bio team: his club version's row
            if (k.Version == 0)
            {
                // his club version can be on several teams (his club and an All-Star or event team): the bio takes the
                // club — an NHL team first, then another club league, then anything else
                int team = (int)source.Get(st, r, sf["team"]);
                int Rank(int t) => nhlClubs.Contains(t) ? 0 : leagues.GetValueOrDefault(t, -1) is >= 1 and <= 16 and not 10 ? 1 : 2;
                if (!teamOf.TryGetValue(k.Id, out int had) || Rank(team) < Rank(had)) teamOf[k.Id] = team;
            }
        }
        Replace(game, gt, i => rows[i], rows.Count);
        // the player index each row points at: the game's for that player
        for (int r = 0; r < rows.Count; r++) game.Set(gt, r, gf["playerindex"], targets[r]);
        var bio = game.Table(Roster.BioTable); var bf = bio.Fields.ToDictionary(x => x.Name);
        int noTeam = 0;
        for (int r = 0; r < bio.WrittenRecords; r++)
        {
            if (game.IsInvalid(bio, r)) continue;
            int id = (int)game.Get(bio, r, bf["game_id"]);
            if (id >= PlayerCreator.SpecialIdStart) continue;
            if (teamOf.TryGetValue(id, out int team)) game.Set(bio, r, bf["team"], team);
            else if ((int)game.Get(bio, r, bf["team"]) != -1) { game.Set(bio, r, bf["team"], -1); noTeam++; }
        }
        CopyWithin(game, Roster.RosterTable, RosterSync.OnlineRosterTable);
        return $"Roster spots: {rows.Count} rows for existing players ({skipped} for players the game doesn't have left out), {noTeam} players left without a team";
    }

    /// <summary>
    /// Takes players off their teams: their roster rows are removed, their bio team set to none, and the online roster
    /// rebuilt. They stay in the game as players without a team. Returns how many roster rows went.
    /// </summary>
    public static int RemoveFromRosters(T3Db db, ISet<int> gameIds)
    {
        using var batch = db.Batch();
        var bio = db.Table(Roster.BioTable); var bf = bio.Fields.ToDictionary(x => x.Name);
        var pl = db.Table(Roster.PlayersTable); var pf = pl.Fields.ToDictionary(x => x.Name);
        var rt = db.Table(Roster.RosterTable); var rf = rt.Fields.ToDictionary(x => x.Name);
        var indexes = new HashSet<int>();
        for (int r = 0; r < pl.WrittenRecords; r++)
            if (!db.IsInvalid(pl, r) && gameIds.Contains((int)db.Get(pl, r, pf["playerid"]))) indexes.Add((int)db.Get(pl, r, pf["index"]));
        for (int r = 0; r < bio.WrittenRecords; r++)
            if (!db.IsInvalid(bio, r) && gameIds.Contains((int)db.Get(bio, r, bf["game_id"]))) db.Set(bio, r, bf["team"], -1);
        var gone = Enumerable.Range(0, rt.WrittenRecords).Where(r => !db.IsInvalid(rt, r) && indexes.Contains((int)db.Get(rt, r, rf["playerindex"]))).ToHashSet();
        RemoveRows(db, rt, gone);
        CopyWithin(db, Roster.RosterTable, RosterSync.OnlineRosterTable);
        return gone.Count;
    }

    /// <summary>
    /// Career stats left pointing at players the base roster dropped (the stats tables aren't in a roster): their rows
    /// are removed. The stock data has none of its own, so only rows the drop orphaned go.
    /// </summary>
    public static string RemoveOrphanStats(T3Db db)
    {
        using var batch = db.Batch();
        var ids = new HashSet<int>();
        var bio = db.Table(Roster.BioTable);
        var idf = bio.Fields.First(f => f.Name == "game_id");
        for (int r = 0; r < bio.WrittenRecords; r++) if (!db.IsInvalid(bio, r)) ids.Add((int)db.Get(bio, r, idf));
        int removed = 0;
        foreach (var t in db.Tables.Where(t => t.Name.StartsWith("stockcareer", StringComparison.OrdinalIgnoreCase)))
        {
            var f = t.Fields.FirstOrDefault(x => x.Name == "game_id");
            if (f == null) continue;
            var gone = new HashSet<int>();
            for (int r = 0; r < t.WrittenRecords; r++)
                if (!db.IsInvalid(t, r) && (int)db.Get(t, r, f) is int id && id >= 0 && !ids.Contains(id)) gone.Add(r);
            RemoveRows(db, t, gone);
            removed += gone.Count;
        }
        return removed == 0 ? null : $"{removed} career-stat rows of dropped players removed";
    }

    /// <summary>
    /// References that point at nothing: every playerindex against the players tables, every game_id/playerid outside
    /// the bios against the bios, every team field against the teams. One line per table and field with examples. The
    /// game's own history (awards, records, coaches) names retired players no roster has, so those tables are left out.
    /// </summary>
    public static List<string> CheckReferences(T3Db db)
    {
        HashSet<int> Values(string table, string field)
        {
            var set = new HashSet<int>();
            var t = db.Table(table);
            var f = t?.Fields.FirstOrDefault(x => x.Name == field);
            if (f == null) return set;
            for (int r = 0; r < t.WrittenRecords; r++) if (!db.IsInvalid(t, r)) set.Add((int)db.Get(t, r, f));
            return set;
        }
        var indexes = Values(Roster.PlayersTable, "index"); indexes.UnionWith(Values(CapPlayers, "index"));
        var ids = Values(Roster.BioTable, "game_id"); ids.UnionWith(Values(CapBio, "game_id"));
        var teams = Values(Roster.TeamsTable, "index"); teams.UnionWith(Values("customteams", "index"));
        var history = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "stockawardstable", "stockrecordstable", "stockcoachtable" };
        var found = new List<string>();
        foreach (var t in db.Tables.Where(t => !history.Contains(t.Name)).OrderBy(t => t.Name))
            foreach (var f in t.Fields.Where(f => f.Type == T3FieldType.Integer))
            {
                HashSet<int> valid; string what;
                if (f.Name == "playerindex") { valid = indexes; what = "player index"; }
                else if ((f.Name is "game_id" or "playerid") && !t.Name.EndsWith("biotable")) { valid = ids; what = "player"; }
                else if (f.Name is "team" or "teamid" or "proteam" or "draftteam" or "hometeam" or "awayteam" or "ahlaffiliate") { valid = teams; what = "team"; }
                else continue;
                var bad = new Dictionary<int, int>();
                for (int r = 0; r < t.WrittenRecords; r++)
                    if (!db.IsInvalid(t, r) && (int)db.Get(t, r, f) is int v && v >= 0 && !valid.Contains(v)) bad[v] = bad.GetValueOrDefault(v) + 1;
                if (bad.Count > 0)
                    found.Add($"{t.Name}.{f.Name}: {bad.Values.Sum()} rows point at a missing {what} (e.g. {string.Join(", ", bad.Keys.Take(6))})");
            }
        return found;
    }

    private static int MaxBelow(T3Db db, T3Table t, string field, int limit)
    {
        var f = t.Fields.First(x => x.Name == field);
        int max = 0;
        for (int r = 0; r < t.WrittenRecords; r++)
            if (!db.IsInvalid(t, r) && (int)db.Get(t, r, f) is int v && v < limit && v > max) max = v;
        return max;
    }
}
