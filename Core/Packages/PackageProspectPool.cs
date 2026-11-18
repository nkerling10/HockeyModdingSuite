namespace NHL27Kit.Core.Packages;

/// <summary>
/// The game's pool of real people (customplayerbiotable, state 2: 346 rows, art 12586/12587, hometown "$BURNABY"):
/// retired stars and, born 2005 and later, real prospects a new franchise generates as its own players (Joey Cullen,
/// Gunnar Conboy, Ben Geiger...). They have their own game ids, so when the package already has that person (created,
/// or moved to a school) the franchise ends up with two: Joey Cullen at Minnesota (the package's, on the free-agent
/// list until his commit year) and Joey Cullen at a random school (the franchise's). A pool row matching one of the
/// package's players by first name, last name and birth date is switched off: active 0. A new franchise generates the
/// pool's active rows only (checked 2026-11-17 on a day-1 save: all 52 young active rows generated, none of the 18
/// inactive ones; state 127 alone changed nothing). The install keeps the original bytes, so removing the package
/// puts the pool back.
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
        for (int i = 0; i < pool.WrittenRecords; i++)
        {
            if (db.IsInvalid(pool, i) || Int(db, pool, i, pf["state"]) != Real) continue;
            var key = Key(db, pool, i, pf);
            if (!ours.TryGetValue(key, out var name)) continue;
            if (Int(db, pool, i, pf["active"]) == 0) continue;
            db.Set(pool, i, pf["active"], 0);
            retired.Add($"{name} ({1900 + key.Item3}, pool id {Int(db, pool, i, pf["game_id"])})");
        }
        if (retired.Count > 0)
            r.Lines.Add($"prospect pool: {retired.Count} real people the package already has switched off in the franchise's pool (active 0: no second copy): {string.Join(", ", retired)}");
    }

    private static (string, string, int, int, int) Key(T3Db db, T3Table t, int row, Dictionary<string, T3Field> f) =>
        (Norm(db.Get(t, row, f["firstname"])), Norm(db.Get(t, row, f["lastname"])),
         Int(db, t, row, f["year"]), Int(db, t, row, f["month"]), Int(db, t, row, f["day"]));

    private static string Norm(object v) => (v?.ToString() ?? "").Trim().ToLowerInvariant();

    private static int Int(T3Db db, T3Table t, int row, T3Field f) => Convert.ToInt32(db.Get(t, row, f));
}
