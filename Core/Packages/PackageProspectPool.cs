namespace NHL27Kit.Core.Packages;

/// <summary>
/// The game's pool of real people (customplayerbiotable, state 2: 346 rows, art 12586/12587, hometown "$BURNABY"):
/// retired stars and, born 2005 and later, real prospects a new franchise generates as its own players (Joey Cullen,
/// Gunnar Conboy, Ben Geiger...). They have their own game ids, so when the package already has that person (created,
/// or moved to a school) the franchise ends up with two. A pool row matching one of the package's players by first
/// name, last name and birth date is taken out of the pool: the row and every custom-table row with its game id
/// (customplayers, ratings, gear, roster). Switching it off doesn't work: the franchise generated all 52 young rows
/// that were active in the stock pool although the 16 of them the package had set to active 0 (2026-11-18 sim,
/// BeAGM0), and state 127 didn't either; which rows it takes isn't decided by anything in the rows themselves (the 34
/// it skips, mostly made-up names, match the 52 field for field). The install keeps the original bytes, so removing
/// the package puts the pool back.
/// </summary>
public static class PackageProspectPool
{
    public const string Table = "customplayerbiotable";
    public const int Real = 2;

    /// <summary>Call inside the install's database batch, after the players and commits steps.</summary>
    public static void Apply(T3Db db, PackageSpec spec, PackageInstaller.Result r)
    {
        var pool = db.Table(Table);
        var bios = db.Table("exhibitionplayerbiotable");
        if (pool == null || bios == null) return;
        var pf = pool.Fields.ToDictionary(x => x.Name);
        var bf = bios.Fields.ToDictionary(x => x.Name);
        if (!pf.ContainsKey("state") || !pf.ContainsKey("game_id") || !pf.ContainsKey("active")) return;

        // the package's players: its id blocks, everyone the players step placed, every commit (by art id)
        var blocks = spec.Groups.Select(g => g.Players?.Ids).Where(ids => ids is { Count: 2 }).Select(ids => (From: ids[0], To: ids[1])).ToList();
        var placed = r.Placed.Values.ToHashSet();
        var commitArt = r.Commits.Select(c => c.ArtId).ToHashSet();
        var ours = new Dictionary<(string, string, int, int, int), string>();
        for (int i = 0; i < bios.WrittenRecords; i++)
        {
            if (db.IsInvalid(bios, i)) continue;
            int gid = Int(db, bios, i, bf["game_id"]);
            bool mine = placed.Contains(gid) || blocks.Any(b => gid >= b.From && gid <= b.To) || commitArt.Contains(Int(db, bios, i, bf["artid"]));
            if (mine) ours.TryAdd(Key(db, bios, i, bf), $"{db.Get(bios, i, bf["firstname"])} {db.Get(bios, i, bf["lastname"])}");
        }

        var retired = new List<string>();
        var rows = new HashSet<int>();
        var ids = new HashSet<int>();
        for (int i = 0; i < pool.WrittenRecords; i++)
        {
            if (db.IsInvalid(pool, i) || Int(db, pool, i, pf["state"]) != Real) continue;
            var key = Key(db, pool, i, pf);
            if (!ours.TryGetValue(key, out var name)) continue;
            int gid = Int(db, pool, i, pf["game_id"]);
            rows.Add(i);
            ids.Add(gid);
            retired.Add($"{name} ({1900 + key.Item3}, pool id {gid})");
        }
        if (rows.Count == 0) return;
        RosterBaseline.RemoveRows(db, pool, rows);
        // their rows in the other custom tables, by game id (customplayers: index / playerid; rosters: playerindex)
        int linked = 0;
        foreach (var t in db.Tables.Where(t => t != pool && t.Name.StartsWith("custom", StringComparison.Ordinal) && !t.Name.StartsWith("customonline", StringComparison.Ordinal)))
        {
            var keys = t.Fields.Where(f => f.Name is "game_id" or "playerid" or "playerindex" || (t.Name == "customplayers" && f.Name == "index")).ToList();
            if (keys.Count == 0) continue;
            var drop = new HashSet<int>();
            for (int i = 0; i < t.WrittenRecords; i++)
                if (!db.IsInvalid(t, i) && keys.Any(f => ids.Contains(Int(db, t, i, f)))) drop.Add(i);
            RosterBaseline.RemoveRows(db, t, drop);
            linked += drop.Count;
        }
        r.Lines.Add($"prospect pool: {retired.Count} real people the package already has taken out of the franchise's pool " +
                    $"(their pool rows and {linked} linked rows; no second copy): {string.Join(", ", retired)}");
    }

    private static (string, string, int, int, int) Key(T3Db db, T3Table t, int row, Dictionary<string, T3Field> f) =>
        (Norm(db.Get(t, row, f["firstname"])), Norm(db.Get(t, row, f["lastname"])),
         Int(db, t, row, f["year"]), Int(db, t, row, f["month"]), Int(db, t, row, f["day"]));

    private static string Norm(object v) => (v?.ToString() ?? "").Trim().ToLowerInvariant();

    private static int Int(T3Db db, T3Table t, int row, T3Field f) => Convert.ToInt32(db.Get(t, row, f));
}
