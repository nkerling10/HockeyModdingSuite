using System.Text.Json;

namespace NHL27Kit.Core.Packages;

/// <summary>
/// The package's career stat lines (recipe "careerStats": one file or a list of files, JSON or CSV): real seasons for
/// the package's players, written to the tables a player card's career stats read: stockcareerseasonplayerstats for
/// skaters, stockcareerseasongoaliestats for goalies (one row per player, season and team: Quinn Hughes has 2019-2026,
/// two rows for 2026 after his trade). Both tables ship full (22,769 / 2,862), so they are grown first (T3Db.Grow) and
/// the installer writes the bigger database through the rebuilt-chunk path.
///
/// JSON: { "&lt;player name as in the roster file&gt;": { "team": "MINN", "seasons": [ { "season": "2024-25", "team": "MINN",
/// "gp": 38, "g": 12, ... } ] } }. CSV (stats_todo/*.csv): a header row, then one row per season with columns school
/// (the player's school in the roster file), player, season, team (that season's school; default the school) and the
/// stat columns below; a row without gp is skipped (not filled in yet), an empty stat is 0.
/// Skater stats: gp g a pim pm shots ppg ppa shg sha gwg gtg blocks hits giveaways takeaways fow fot toi so_shots
/// so_goals. Goalie stats (the player is a goalie in the game): gp w l t otl sa ga shutouts toi g a pim so_wins
/// so_losses. toi: total "594:04" (mm:ss), or a per-game mm:ss under an hour (× gp).
/// "season" "2024-25" is stored by its end year (2025). The tables' league field is one bit (0 NHL, 1 AHL): school rows
/// use "careerStatsLeague" (default 1); the NCAA code mod's league cell shows them as "NCAA".
/// </summary>
public static class PackageCareerStats
{
    public const string SkaterTable = "stockcareerseasonplayerstats", GoalieTable = "stockcareerseasongoaliestats";

    /// <summary>File/CSV key → skater table field.</summary>
    private static readonly (string Key, string Field)[] SkaterFields =
    {
        ("gp", "gamesplayed"), ("g", "goals"), ("a", "assists"), ("pim", "penaltyminutes"), ("pm", "plusminus"),
        ("shots", "shots"), ("ppg", "powerplaygoals"), ("ppa", "powerplayassists"), ("shg", "shorthandedgoals"),
        ("sha", "shorthandedassists"), ("gwg", "gamewinninggoals"), ("gtg", "gametyinggoals"), ("blocks", "blockedshots"),
        ("hits", "hits"), ("giveaways", "giveaways"), ("takeaways", "takeaways"), ("fow", "faceoffswon"),
        ("fot", "faceoffstaken"), ("so_shots", "shootoutshots"), ("so_goals", "shootoutgoals"),
    };

    /// <summary>File/CSV key → goalie table field.</summary>
    private static readonly (string Key, string Field)[] GoalieFields =
    {
        ("gp", "gamesplayed"), ("w", "wins"), ("l", "losses"), ("t", "ties"), ("otl", "otlosses"), ("sa", "shotsagainst"),
        ("ga", "goalsagainst"), ("shutouts", "shutouts"), ("g", "goals"), ("a", "assists"), ("pim", "penaltyminutes"),
        ("so_wins", "sowins"), ("so_losses", "solosses"),
    };

    private sealed record Line(string Player, string School, string Season, string Team, Dictionary<string, string> Values);

    /// <summary>
    /// Adds the files' lines and returns the database to carry on with: <paramref name="db"/> itself, or a grown copy
    /// when a table needed room (the caller must use the returned one from here on). Call outside any Batch.
    /// </summary>
    public static T3Db Apply(T3Db db, string metaXml, PackageSpec spec, PackageInstaller.Result r)
    {
        var files = spec.CareerStatsFiles;
        if (files.Count == 0) return db;
        var read = new List<Line>();
        foreach (var name in files)
        {
            string file = spec.PathOf(name);
            if (!File.Exists(file)) throw new FileNotFoundException($"The career stats file {file} isn't there.");
            int before = read.Count;
            if (file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) ReadCsv(file, read); else ReadJson(file, read);
            r.Lines.Add($"career stats: {read.Count - before} filled-in season(s) in {name}");
        }
        var byCode = spec.AllTeams.ToDictionary(t => t.Code, StringComparer.OrdinalIgnoreCase);
        var roster = new Roster(db);
        var skaters = new List<(int GameId, int Year, int Team, Line L)>();
        var goalies = new List<(int GameId, int Year, int Team, Line L)>();
        foreach (var l in read)
        {
            if (l.School == null || !byCode.TryGetValue(l.School, out var school)) { r.Warnings.Add($"career stats: {l.Player}: school '{l.School}' isn't one of the package's teams; skipped"); continue; }
            if (!r.Placed.TryGetValue((school.Code, PackagePortraits.Slug(l.Player)), out int gid))
            { r.Warnings.Add($"career stats: {l.Player} ({school.Code}) isn't on the roster the players step placed; skipped"); continue; }
            if (PackageCareerStatsYear.EndYear(l.Season) is not int year) { r.Warnings.Add($"career stats: {l.Player}: season '{l.Season}' isn't like 2024-25; skipped"); continue; }
            string code = string.IsNullOrWhiteSpace(l.Team) ? school.Code : l.Team.Trim();
            if (!byCode.TryGetValue(code, out var team)) { r.Warnings.Add($"career stats: {l.Player} {l.Season}: team '{code}' isn't one of the package's teams; skipped"); continue; }
            bool goalie = roster.Find(gid)?.IsGoalie == true;
            (goalie ? goalies : skaters).Add((gid, year, team.Index, l));
        }
        int league = spec.CareerStatsLeague ?? 1;
        var clipped = new List<string>();
        db = Write(db, metaXml, SkaterTable, SkaterFields, skaters, league, goalie: false, clipped, r);
        db = Write(db, metaXml, GoalieTable, GoalieFields, goalies, league, goalie: true, clipped, r);
        if (clipped.Count > 0) r.Warnings.Add($"career stats: values past the field's range, clamped: {string.Join(", ", clipped.Take(10))}");
        return db;
    }

    private static T3Db Write(T3Db db, string metaXml, string table, (string Key, string Field)[] map, List<(int GameId, int Year, int Team, Line L)> lines,
                              int league, bool goalie, List<string> clipped, PackageInstaller.Result r)
    {
        if (lines.Count == 0) return db;
        // room: the tables ship full; grow by what's missing plus some slack for edits
        var t = db.Table(table) ?? throw new InvalidDataException($"No {table} table.");
        if (t.WrittenRecords + lines.Count > t.Records)
        {
            int before = t.Records;
            db = new T3Db(db.Grow(t, t.WrittenRecords + lines.Count - t.Records + 256), metaXml) { Source = db.Source };
            t = db.Table(table);
            r.Lines.Add($"career stats: {table} grown from {before} to {t.Records} records (the database is {db.Data.Length:N0} bytes now)");
        }
        var f = t.Fields.ToDictionary(x => x.Name);
        using (db.Batch())
        {
            var template = db.RecordBytes(t, 0);
            int first = t.WrittenRecords;
            db.SetWritten(t, first + lines.Count);
            for (int i = 0; i < lines.Count; i++)
            {
                var (gid, year, team, l) = lines[i];
                int row = first + i;
                // a copy of a real row, every field then written (none keeps the copied player's value)
                db.WriteRecord(t, row, (byte[])template.Clone());
                foreach (var fl in t.Fields) Set(fl.Name, 0);
                void Set(string name, int v)
                {
                    if (!f.TryGetValue(name, out var fl)) return;
                    int lo = fl.RangeLow, hi = (int)Math.Min(int.MaxValue, fl.RangeLow + (1L << fl.Depth) - 1);
                    if (v < lo || v > hi)
                    {
                        if (v != 0) clipped.Add($"{l.Player} {l.Season} {name} {v}");
                        v = Math.Clamp(v, lo, hi);
                    }
                    db.Set(t, row, fl, v);
                }
                Set("game_id", gid); Set("year", year - 1900); Set("team", team); Set("league", league);
                foreach (var (key, field) in map) Set(field, Int(l.Values.GetValueOrDefault(key)));
                int gp = Int(l.Values.GetValueOrDefault("gp"));
                Set(goalie ? "timeinnetseconds" : "timeoniceseconds", PackageCareerStatsYear.Seconds(l.Values.GetValueOrDefault("toi"), gp));
            }
        }
        // the game ships the tables sorted by player, then season: keep them so (stable: new lines after older ones)
        var gidF = f["game_id"]; var yearF = f["year"];
        var order = Enumerable.Range(0, t.WrittenRecords)
                              .OrderBy(i => Convert.ToInt32(db.Get(t, i, gidF))).ThenBy(i => Convert.ToInt32(db.Get(t, i, yearF))).ThenBy(i => i).ToList();
        db.Reorder(t, order);
        r.Lines.Add($"career stats: {lines.Count} {(goalie ? "goalie" : "skater")} season lines for {lines.Select(x => x.GameId).Distinct().Count()} players (league field {league}); {table} {t.WrittenRecords} / {t.Records}");
        return db;
    }

    private static int Int(string s) => int.TryParse((s ?? "").Trim().Replace(",", ""), out int v) ? v : 0;

    private static void ReadJson(string file, List<Line> into)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            string school = p.Value.TryGetProperty("team", out var tc) ? tc.GetString() : null;
            if (!p.Value.TryGetProperty("seasons", out var ss) || ss.ValueKind != JsonValueKind.Array) continue;
            foreach (var se in ss.EnumerateArray())
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var x in se.EnumerateObject())
                    values[x.Name] = x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() : x.Value.ToString();
                if (!values.ContainsKey("gp")) continue;
                into.Add(new Line(p.Name, school, values.GetValueOrDefault("season"), values.GetValueOrDefault("team"), values));
            }
        }
    }

    private static void ReadCsv(string file, List<Line> into)
    {
        var rows = Csv(File.ReadAllText(file));
        if (rows.Count == 0) return;
        var head = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        foreach (var row in rows.Skip(1))
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < head.Count && i < row.Count; i++) values[head[i]] = row[i];
            if (string.IsNullOrWhiteSpace(values.GetValueOrDefault("gp"))) continue;     // not filled in yet
            into.Add(new Line(values.GetValueOrDefault("player"), values.GetValueOrDefault("school"), values.GetValueOrDefault("season"),
                              values.GetValueOrDefault("team"), values));
        }
    }

    /// <summary>RFC 4180 CSV: quoted fields may hold commas, quotes ("") and line breaks.</summary>
    internal static List<List<string>> Csv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = new List<string>();
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        if (rows.Count > 0 && rows[0].Count > 0) rows[0][0] = rows[0][0].TrimStart('﻿');
        return rows;
    }
}

/// <summary>Season and time helpers for the career lines.</summary>
internal static class PackageCareerStatsYear
{
    /// <summary>"2024-25" or "2024-2025" → 2025 (the tables' year is the season's end); null when it isn't one.</summary>
    internal static int? EndYear(string season)
    {
        var parts = (season ?? "").Trim().Split('-', '/');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int a) || !int.TryParse(parts[1], out int b)) return null;
        int end = b < 100 ? a / 100 * 100 + b : b;
        if (end < a) end += 100;
        return end == a + 1 ? end : null;
    }

    /// <summary>Total seconds from "594:04" (mm:ss total) or "15:38" when it looks like a per-game average (under an
    /// hour, more than one game); 0 when missing.</summary>
    internal static int Seconds(string toi, int gp)
    {
        if (string.IsNullOrWhiteSpace(toi)) return 0;
        var p = toi.Trim().TrimStart('~').Split(':');
        if (p.Length != 2 || !int.TryParse(p[0].Replace(",", ""), out int m) || !int.TryParse(p[1], out int s)) return 0;
        int secs = m * 60 + s;
        return m < 60 && gp > 1 ? secs * gp : secs;
    }
}
