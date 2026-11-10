namespace NHL27Kit.Core;

/// <summary>
/// Adds a new exhibition team to nhl_game.db by cloning an existing one (rows are added in the tables' spare room; nothing
/// is replaced). Copied per team:
/// <list type="bullet">
///   <item><c>exhibitionteams</c> (names, abbreviations, art id, league, colours set from the arguments);</item>
///   <item><c>exhibitionteamstrategies</c> and <c>customonlineteamstrategies</c> (one row each, keyed by team index);</item>
///   <item><c>stockteamjerseys</c> (the template's jerseys, same art, so the new team wears them until it has its own);</item>
///   <item><c>stockteamequipmenttable</c> and <c>stockgoalieequipment</c> (new unique keys).</item>
/// </list>
/// The arena is shared with the template (its <c>arenaid</c> is kept). Name text in the game's language file is looked up
/// by <c>artabbr</c>; a new artabbr has no strings there, so whether the game falls back to <c>teamname</c> is what a
/// first test shows. Tables keyed by team index are re-sorted by key afterwards.
/// </summary>
public static class TeamCreator
{
    public sealed record NewTeam(int Index, string TeamName, string Nickname, string City, string Abbr, string ArtAbbr,
                                 int ArtId, int League, int? PrimaryColor = null, int? SecondaryColor = null);

    public static string Create(T3Db db, int fromIndex, NewTeam nt)
    {
        var teams = Need(db, "exhibitionteams");
        if (Rows(db, teams, "index", nt.Index).Any()) throw new ArgumentException($"team index {nt.Index} is already used");
        if (nt.Index is < 0 or > 1023) throw new ArgumentOutOfRangeException(nameof(nt), "team index must be 0-1023");
        int src = Rows(db, teams, "index", fromIndex).DefaultIfEmpty(-1).First();
        if (src < 0) throw new ArgumentException($"no team with index {fromIndex}");

        var log = new List<string>();
        // the team row
        int row = Append(db, teams, src);
        Set(db, teams, row, "index", nt.Index);
        Set(db, teams, row, "teamname", nt.TeamName);
        Set(db, teams, row, "nickname", nt.Nickname);
        Set(db, teams, row, "cityname", nt.City);
        Set(db, teams, row, "abbrname", nt.Abbr);
        Set(db, teams, row, "artabbr", nt.ArtAbbr);
        Set(db, teams, row, "artid", nt.ArtId);
        Set(db, teams, row, "league", nt.League);
        Set(db, teams, row, "rivalteam", -1);
        Set(db, teams, row, "rivalteam2", -1);
        if (nt.PrimaryColor is int pc)
            foreach (var f in new[] { "feprimarycolor", "overlayprimarycolor", "presprimarycolor" }) Set(db, teams, row, f, pc);
        if (nt.SecondaryColor is int sc)
            foreach (var f in new[] { "fesecondarycolor", "overlaysecondarycolor", "pressecondarycolor" }) Set(db, teams, row, f, sc);
        SortBy(db, teams, "index");
        log.Add($"exhibitionteams: team {nt.Index} \"{nt.TeamName}\" ({nt.Abbr}/{nt.ArtAbbr}), league {nt.League}, art {nt.ArtId}, arena {Get(db, teams, Rows(db, teams, "index", nt.Index).First(), "arenaid")} (shared with {fromIndex})");

        // strategies: one row per team, keyed by team index
        foreach (var name in new[] { "exhibitionteamstrategies", "customonlineteamstrategies" })
        {
            var t = db.Table(name);
            if (t == null) continue;
            int s = Rows(db, t, "index", fromIndex).DefaultIfEmpty(-1).First();
            if (s < 0) { log.Add($"{name}: template has no row, skipped"); continue; }
            int r = Append(db, t, s);
            Set(db, t, r, "index", nt.Index);
            SortBy(db, t, "index");
            log.Add($"{name}: 1 row");
        }

        // jerseys: the template's rows with the new team id (key teamid, then jerseyindex)
        var jerseys = db.Table("stockteamjerseys");
        if (jerseys != null)
        {
            var src2 = Rows(db, jerseys, "teamid", fromIndex).ToList();
            foreach (int s in src2) { int r = Append(db, jerseys, s); Set(db, jerseys, r, "teamid", nt.Index); }
            SortBy(db, jerseys, "teamid", "jerseyindex");
            log.Add($"stockteamjerseys: {src2.Count} rows (template's jerseys)");
        }

        // equipment: the template's rows with the new team and fresh unique keys
        foreach (var (name, key) in new[] { ("stockteamequipmenttable", "index"), ("stockgoalieequipment", "key") })
        {
            var t = db.Table(name);
            if (t == null) continue;
            var src3 = Rows(db, t, "team", fromIndex).ToList();
            int next = Enumerable.Range(0, t.WrittenRecords).Where(i => !db.IsInvalid(t, i)).Select(i => Get(db, t, i, key)).DefaultIfEmpty(-1).Max() + 1;
            foreach (int s in src3) { int r = Append(db, t, s); Set(db, t, r, "team", nt.Index); Set(db, t, r, key, next++); }
            SortBy(db, t, key);
            log.Add($"{name}: {src3.Count} rows");
        }
        return string.Join("; ", log);
    }

    /// <summary>
    /// Gives an exhibition team a new index: its own rows (team, strategies, jerseys, equipment) and every reference from
    /// roster rows and player bios (<c>team</c>, <c>juniorteam</c>). Keyed tables are re-sorted. Any other field whose
    /// name contains "team" and still holds the old index is reported, not changed.
    /// Note: the game treats indexes 400-599 as create-a-team teams (their roster is read from customrostertable).
    /// </summary>
    public static string Move(T3Db db, int oldIndex, int newIndex)
    {
        var teams = Need(db, "exhibitionteams");
        if (!Rows(db, teams, "index", oldIndex).Any()) throw new ArgumentException($"no team with index {oldIndex}");
        if (Rows(db, teams, "index", newIndex).Any()) throw new ArgumentException($"team index {newIndex} is already used");
        var log = new List<string>();
        var plan = new (string Table, string Field, string[] Sort)[]
        {
            ("exhibitionteams", "index", new[] { "index" }),
            ("exhibitionteamstrategies", "index", new[] { "index" }),
            ("customonlineteamstrategies", "index", new[] { "index" }),
            ("stockteamjerseys", "teamid", new[] { "teamid", "jerseyindex" }),
            ("stockteamequipmenttable", "team", Array.Empty<string>()),
            ("stockgoalieequipment", "team", Array.Empty<string>()),
            ("exhibitionplayerbiotable", "team", Array.Empty<string>()),
            ("exhibitionplayerbiotable", "juniorteam", Array.Empty<string>()),
        };
        var done = new HashSet<(string, string)>();
        foreach (var (name, field, sort) in plan)
        {
            var t = db.Table(name);
            if (t == null) continue;
            var rows = Rows(db, t, field, oldIndex).ToList();
            foreach (int r in rows) Set(db, t, r, field, newIndex);
            if (sort.Length > 0 && rows.Count > 0) SortBy(db, t, sort);
            done.Add((name, field));
            if (rows.Count > 0) log.Add($"{name}.{field}: {rows.Count}");
        }
        // roster rows: key = team * 40 + slot, tables kept in key order
        foreach (var name in new[] { "exhibitionrostertable", "customonlinerostertable" })
        {
            var t = db.Table(name);
            if (t == null) continue;
            var newKeys = Enumerable.Range(0, t.WrittenRecords).Where(i => !db.IsInvalid(t, i)).Select(i => Get(db, t, i, "key")).Where(k => k / 40 == newIndex).ToList();
            if (newKeys.Count > 0) throw new InvalidOperationException($"{name} already has keys for team {newIndex}");
            var rows = Rows(db, t, "team", oldIndex).ToList();
            foreach (int r in rows)
            {
                Set(db, t, r, "team", newIndex);
                Set(db, t, r, "key", newIndex * 40 + Get(db, t, r, "key") % 40);
            }
            if (rows.Count > 0) SortBy(db, t, "key");
            done.Add((name, "team"));
            if (rows.Count > 0) log.Add($"{name}: {rows.Count} rows (team and key)");
        }
        foreach (var t in db.Tables)
            foreach (var f in t.Fields.Where(f => f.Type == T3FieldType.Integer && f.Name.Contains("team", StringComparison.OrdinalIgnoreCase)))
            {
                if (done.Contains((t.Name, f.Name))) continue;
                int n = Enumerable.Range(0, t.WrittenRecords).Count(i => !db.IsInvalid(t, i) && Convert.ToInt32(db.Get(t, i, f)) == oldIndex);
                if (n > 0) log.Add($"not changed: {t.Name}.{f.Name} = {oldIndex} in {n} rows");
            }
        return $"team {oldIndex} -> {newIndex}: " + string.Join("; ", log);
    }

    private static T3Table Need(T3Db db, string name) => db.Table(name) ?? throw new InvalidOperationException($"no table {name}");

    private static T3Field F(T3Table t, string name) =>
        t.Fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"{t.Name} has no field {name}");

    private static int Get(T3Db db, T3Table t, int row, string field) => Convert.ToInt32(db.Get(t, row, F(t, field)));

    private static void Set(T3Db db, T3Table t, int row, string field, object value) => db.Set(t, row, F(t, field), value);

    private static IEnumerable<int> Rows(T3Db db, T3Table t, string field, int value) =>
        Enumerable.Range(0, t.WrittenRecords).Where(i => !db.IsInvalid(t, i) && Get(db, t, i, field) == value);

    /// <summary>Adds a copy of record <paramref name="src"/> at the end of the table's written records; returns its row.</summary>
    private static int Append(T3Db db, T3Table t, int src)
    {
        if (t.WrittenRecords >= t.Records) throw new InvalidOperationException($"{t.Name} is full ({t.Records} rows)");
        var bytes = db.RecordBytes(t, src);
        int row = t.WrittenRecords;
        db.SetWritten(t, row + 1);
        db.WriteRecord(t, row, bytes);
        return row;
    }

    /// <summary>Re-sorts the written records by the given integer fields (the game looks tables up by key).</summary>
    private static void SortBy(T3Db db, T3Table t, params string[] fields)
    {
        var order = Enumerable.Range(0, t.WrittenRecords)
            .OrderBy(i => db.IsInvalid(t, i) ? 1 : 0)
            .ThenBy(i => db.IsInvalid(t, i) ? 0 : Get(db, t, i, fields[0]));
        foreach (var f in fields.Skip(1)) order = order.ThenBy(i => db.IsInvalid(t, i) ? 0 : Get(db, t, i, f));
        var list = order.ToList();
        if (!list.SequenceEqual(Enumerable.Range(0, t.WrittenRecords))) db.Reorder(t, list);
    }
}
