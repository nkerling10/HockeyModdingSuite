using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace NHL27Kit.Core.Packages;

/// <summary>
/// Read-only look at what installing a package would do on this game: checks the recipe and its images, and previews
/// the choices an install resolves (team indexes, spare logo art ids, a league logo id, name sets). Nothing is written.
/// </summary>
public static class PackageCheck
{
    public const string GameDb = "gamemodes/database/t3db/nhl_game.db";
    public const string LocDb = "loc/eng_us.db";

    public sealed class Report
    {
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> Lines { get; } = new();
        public Dictionary<string, NameSetChoice> NameSets { get; } = new();
        public Dictionary<string, int> LogoArt { get; } = new();
        public int LeagueArt { get; set; } = -1;
        /// <summary>Spare league logo art id per league id (the package's league and its extra leagues).</summary>
        public Dictionary<int, int> LeagueArts { get; } = new();
        /// <summary>The art id the teams without a logo share (drawn from the league logo), or -1.</summary>
        public int PlaceholderArt { get; set; } = -1;
        public bool Ok => Errors.Count == 0;
    }

    /// <param name="prior">Earlier install's choices (resolved.json): a team keeps its name set and logo art id when
    /// they're still free and fit, and the other teams pick around them, so no two teams share either.</param>
    public static Report Run(AssetIndex index, PackageSpec spec, PackageInstaller.Resolved prior = null)
    {
        var r = new Report();
        var legacy = new Legacy(index);
        var db = legacy.OpenDb(GameDb);
        var loc = legacy.OpenDb(LocDb);

        foreach (var g in spec.Groups) CheckRecipe(g, r);
        foreach (var dup in spec.AllTeams.GroupBy(t => t.Code, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            r.Errors.Add($"Team code {dup.Key} is used {dup.Count()} times.");
        foreach (var dup in spec.AllTeams.GroupBy(t => t.Index).Where(g => g.Count() > 1))
            r.Errors.Add($"Team index {dup.Key} is used {dup.Count()} times.");
        CheckImages(spec, r);
        Recycle(db, spec, r.Lines);                 // on this copy: the room and art ids below see the retired teams gone
        CheckTeams(db, spec, r);
        CheckRoom(db, spec, r);
        PreviewLogoArt(index, db, spec, r, prior);
        PreviewLeagueArt(index, db, spec, r);
        foreach (var g in spec.Groups) PreviewStrings(loc, g, r);
        PreviewNameSets(db, loc, spec, r, prior);
        return r;
    }

    // ---------------------------------------------------------------- retiring teams for room

    /// <summary>
    /// When the team tables can't take the package's new teams, removes as many of spec.RecycleTeams' teams as needed
    /// (its league, name pattern, no player of their own, highest index first): their rows in the team, strategy,
    /// jersey, equipment and roster tables, and their arena row when no other team uses it. Returns their indexes.
    /// </summary>
    public static List<int> Recycle(T3Db db, PackageSpec spec, List<string> lines)
    {
        var done = new List<int>();
        if (spec.RecycleTeams is not { } rc) return done;
        var teams = db.Table("exhibitionteams");
        int Int(T3Table t, int row, string f) => Convert.ToInt32(db.Get(t, row, F(t, f)));
        var have = Valid(db, teams).Select(i => Int(teams, i, "index")).ToHashSet();
        int need = spec.AllTeams.Count(t => !have.Contains(t.Index));
        int Free(string n) => db.Table(n) is { } t ? t.Records - t.WrittenRecords : int.MaxValue;
        int shortBy = need - new[] { "exhibitionteams", "exhibitionteamstrategies", "customonlineteamstrategies", "exhibitionarena" }.Min(Free);
        if (shortBy <= 0) return done;
        var bios = db.Table("exhibitionplayerbiotable");
        var owners = bios == null ? new HashSet<int>() : Valid(db, bios).Select(i => Int(bios, i, "team")).ToHashSet();
        var rx = new Regex(rc.NamePattern ?? ".*");
        var picks = Valid(db, teams)
            .Select(i => (Index: Int(teams, i, "index"), League: Int(teams, i, "league"), Arena: Int(teams, i, "arenaid"),
                          Name: db.Get(teams, i, F(teams, "teamname"))?.ToString() ?? ""))
            .Where(x => x.League == rc.League && rx.IsMatch(x.Name) && !owners.Contains(x.Index) && !spec.AllTeams.Any(t => t.Index == x.Index))
            .OrderBy(x => rc.Indexes is { } li ? (li.IndexOf(x.Index) is var k && k >= 0 ? k : int.MaxValue) : -x.Index)
            .Where(x => rc.Indexes == null || rc.Indexes.Contains(x.Index))
            .Take(shortBy).ToList();
        if (picks.Count < shortBy) lines.Add($"recycle: only {picks.Count} of the {shortBy} teams needed are retirable (league {rc.League}, \"{rc.NamePattern}\"" +
                                              (rc.Indexes != null ? ", the listed indexes" : "") + ", no players of their own)");
        var arenaUsers = Valid(db, teams).GroupBy(i => Int(teams, i, "arenaid")).ToDictionary(g => g.Key, g => g.Count());
        foreach (var p in picks)
        {
            foreach (var (table, key) in new[] { ("exhibitionteams", "index"), ("exhibitionteamstrategies", "index"), ("customonlineteamstrategies", "index"),
                                                 ("stockteamjerseys", "teamid"), ("stockteamequipmenttable", "team"), ("stockgoalieequipment", "team"),
                                                 ("exhibitionrostertable", "team"), ("customonlinerostertable", "team") })
                RemoveWhere(db, table, key, p.Index);
            if (arenaUsers.GetValueOrDefault(p.Arena) == 1) RemoveWhere(db, "exhibitionarena", "index", p.Arena);
            done.Add(p.Index);
        }
        lines.Add($"Retired {done.Count} online event teams for room: {string.Join(", ", picks.Select(p => $"{p.Index} {p.Name}"))}");
        return done;
    }

    private static void RemoveWhere(T3Db db, string table, string key, int value)
    {
        var t = db.Table(table);
        var f = t?.Fields.FirstOrDefault(x => x.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (f == null) return;
        var rows = Enumerable.Range(0, t.WrittenRecords).ToList();
        var gone = rows.Where(i => !db.IsInvalid(t, i) && Convert.ToInt32(db.Get(t, i, f)) == value).ToHashSet();
        if (gone.Count == 0) return;
        db.Reorder(t, rows.Where(i => !gone.Contains(i)).Concat(rows.Where(gone.Contains)).ToList());
        db.SetWritten(t, t.WrittenRecords - gone.Count);
    }

    // ---------------------------------------------------------------- recipe

    private static void CheckRecipe(PackageSpec spec, Report r)
    {
        r.Lines.Add($"Package {spec.Id} \"{spec.Title}\" {spec.Version}: {spec.Teams.Count} teams, league {spec.League?.Id}" +
                    (spec.Players?.File is { } pf ? $", players from {pf} (season {spec.Players.Season ?? "earliest"})" : ""));
        if (spec.Players?.File is { } plf && !File.Exists(spec.PathOf(plf))) r.Errors.Add($"The players file {plf} isn't there.");
        if (spec.League == null) r.Errors.Add("No \"league\".");
        if (spec.League?.NameKey is { Length: > 24 } nk) r.Errors.Add($"league {spec.League.Id}: nameKey {nk} is longer than 24 characters (stockleagues.name).");
        if (spec.TeamBlock == null) { r.Errors.Add("No \"teamBlock\"."); return; }
        var b = spec.TeamBlock;
        if (b.First > b.Last) r.Errors.Add($"teamBlock {b.First}-{b.Last} is empty.");
        if (b.First < 600 && b.Last >= 400 && b.First <= 599) r.Errors.Add($"teamBlock {b.First}-{b.Last} overlaps 400-599 (create-a-team slots: their rosters are read from customrostertable).");
        if (b.Last > 1023) r.Errors.Add("teamBlock goes past 1023.");
        foreach (var t in spec.Teams)
        {
            if (t.Index < b.First || t.Index > b.Last) r.Errors.Add($"{t.Code}: index {t.Index} is outside the teamBlock {b.First}-{b.Last}.");
            foreach (var (what, v) in new[] { ("name", t.Name), ("school", t.School), ("nickname", t.Nickname), ("abbr", t.Abbr) })
                if (string.IsNullOrWhiteSpace(v)) r.Errors.Add($"{t.Code}: no {what}.");
        }
        // code mod 4 picks from first..last of the installed teams: they must be contiguous
        var idx = spec.Teams.Select(t => t.Index).OrderBy(i => i).ToList();
        if (idx.Count > 0 && idx[^1] - idx[0] + 1 != idx.Count)
            r.Errors.Add($"Team indexes {idx[0]}-{idx[^1]} have gaps: generated prospects pick from a contiguous range, so every index in it must be a team.");
        else if (idx.Count > 0)
            r.Lines.Add($"League {spec.League?.Id}: teams {idx[0]}-{idx[^1]}");
    }

    // ---------------------------------------------------------------- images

    private static void CheckImages(PackageSpec spec, Report r)
    {
        foreach (var g in spec.Groups)
            Image(g.LeagueLogoFile() ?? Path.Combine(spec.Folder, "logos", "league", "logo.png"), $"league {g.League?.Id} logo", required: true, r);
        foreach (var t in spec.AllTeams)
        {
            if (spec.TeamLogoFile(t) == null) continue;                // no logo yet: the shared placeholder (PreviewLogoArt)
            Image(spec.TeamLogoFile(t), $"{t.Code} logo", required: true, r);
            foreach (string opt in new[] { "lightbg", "mono", "ice" })
                Image(spec.TeamExtraFile(t, opt), $"{t.Code} {opt}", required: false, r);
        }
    }

    private static void Image(string file, string what, bool required, Report r)
    {
        if (file == null || !File.Exists(file))
        {
            if (required) r.Warnings.Add($"{what}: missing ({file}). Install would leave that art blank.");
            return;
        }
        var (w, h) = PngSize(file);
        if (w <= 0) { r.Errors.Add($"{what}: not a PNG ({file})."); return; }
        string note = w != h ? " (not square: it will be centred on a square)" : "";
        if (Math.Max(w, h) < 512) r.Warnings.Add($"{what}: {w}x{h} is small; 1024x1024 or more looks right at XL.");
        r.Lines.Add($"{what}: {Path.GetFileName(file)} {w}x{h}{note}");
    }

    /// <summary>A PNG's size from its IHDR chunk, or (-1, -1).</summary>
    public static (int W, int H) PngSize(string file)
    {
        Span<byte> b = stackalloc byte[24];
        using var fs = File.OpenRead(file);
        if (fs.Read(b) < 24 || b[0] != 0x89 || b[1] != (byte)'P' || b[2] != (byte)'N' || b[3] != (byte)'G') return (-1, -1);
        return (BinaryPrimitives.ReadInt32BigEndian(b[16..]), BinaryPrimitives.ReadInt32BigEndian(b[20..]));
    }

    // ---------------------------------------------------------------- teams and room

    private static T3Field F(T3Table t, string name) =>
        t.Fields.First(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<int> Valid(T3Db db, T3Table t) => Enumerable.Range(0, t.WrittenRecords).Where(i => !db.IsInvalid(t, i));

    private static void CheckTeams(T3Db db, PackageSpec spec, Report r)
    {
        var teams = db.Table("exhibitionteams");
        var fi = F(teams, "index"); var fn = F(teams, "teamname"); var fl = F(teams, "league");
        var byIndex = Valid(db, teams).ToDictionary(i => Convert.ToInt32(db.Get(teams, i, fi)), i => i);
        foreach (var t in spec.AllTeams)
        {
            if (byIndex.TryGetValue(t.Index, out int row))
            {
                string name = db.Get(teams, row, fn)?.ToString();
                int lg = Convert.ToInt32(db.Get(teams, row, fl));
                if (name == t.Name && lg == t.LeagueId) r.Lines.Add($"{t.Code}: team {t.Index} is already there (installed before).");
                else r.Errors.Add($"{t.Code}: team index {t.Index} is taken by \"{name}\" (league {lg}).");
            }
            if (!byIndex.ContainsKey(t.Template)) r.Errors.Add($"{t.Code}: template team {t.Template} doesn't exist.");
        }
        foreach (var block in spec.Groups.Select(g => g.TeamBlock).Where(b => b != null))
        {
            var foreign = byIndex.Keys.Where(i => i >= block.First && i <= block.Last && !spec.AllTeams.Any(t => t.Index == i)).ToList();
            if (foreign.Count > 0) r.Warnings.Add($"teamBlock {block.First}-{block.Last} already holds other teams: {string.Join(", ", foreign.Take(10))}.");
        }
    }

    private static void CheckRoom(T3Db db, PackageSpec spec, Report r)
    {
        // only the teams not in the game yet need rows (an installed package's teams are taken down and re-added)
        var teams = db.Table("exhibitionteams");
        var have = Valid(db, teams).Select(i => Convert.ToInt32(db.Get(teams, i, F(teams, "index")))).ToHashSet();
        int n = spec.AllTeams.Count(t => !have.Contains(t.Index));
        foreach (var (name, perTeam) in new[] { ("exhibitionteams", 1), ("exhibitionteamstrategies", 1), ("customonlineteamstrategies", 1),
                                                ("exhibitionarena", 1), ("stockteamjerseys", 2), ("stockteamequipmenttable", 37), ("stockgoalieequipment", 4) })
        {
            var t = db.Table(name);
            if (t == null) continue;
            int free = t.Records - t.WrittenRecords;
            string line = $"{name}: {free} free rows, needs ~{n * perTeam}";
            if (free < n * perTeam) r.Errors.Add(line); else r.Lines.Add(line);
        }
    }

    // ---------------------------------------------------------------- art ids

    private static readonly Regex LeagueLogo = new(@"^ui/textures/common/leaguelogos/(.+)/l(\d+)(_fr)?$", RegexOptions.IgnoreCase);

    private static void PreviewLogoArt(AssetIndex index, T3Db db, PackageSpec spec, Report r, PackageInstaller.Resolved prior = null)
    {
        var map = TeamLogos.All(index);
        var teams = db.Table("exhibitionteams");
        var used = Valid(db, teams).Select(i => Convert.ToInt32(db.Get(teams, i, F(teams, "artid")))).ToHashSet();
        int full = map.Menu.Values.Max(l => l.Count);
        var spare = map.Menu.Where(kv => !used.Contains(kv.Key) && map.Ice.ContainsKey(kv.Key))
                            .OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value.Count)).ToList();
        r.Lines.Add($"Spare logo art ids (logo textures + centre ice, no team uses them): {spare.Count}; a full set has {full} menu styles.");
        // more that could serve: unused ids without a centre-ice texture, and ids only inactive teams use
        var noIce = map.Menu.Where(kv => !used.Contains(kv.Key) && !map.Ice.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList();
        var activeArt = Valid(db, teams).Where(i => Convert.ToInt32(db.Get(teams, i, F(teams, "active"))) != 0)
                                        .Select(i => Convert.ToInt32(db.Get(teams, i, F(teams, "artid")))).ToHashSet();
        var inactiveOnly = used.Where(a => !activeArt.Contains(a) && map.Menu.ContainsKey(a)).OrderBy(a => a).ToList();
        r.Lines.Add($"Also: {noIce.Count} unused logo art ids without a centre-ice texture; {inactiveOnly.Count} ids only inactive teams use ({string.Join(", ", inactiveOnly.Take(40))})");
        // teams with a logo get their own id; teams without one share a single placeholder id (the league logo)
        // a team with a logo keeps its earlier id while it's spare and no other team kept it
        var kept = new Dictionary<string, (int, int)>();
        var held = new HashSet<int>();
        var bareTeams = spec.AllTeams.Where(t => spec.TeamLogoFile(t) == null).ToList();
        int priorPh = bareTeams.Select(t => prior?.LogoArt.GetValueOrDefault(t.Code, -1) ?? -1).FirstOrDefault(a => a >= 0, -1);
        foreach (var t in spec.AllTeams.Where(t => spec.TeamLogoFile(t) != null))
            if (prior?.LogoArt.GetValueOrDefault(t.Code, -1) is int pa && pa >= 0 && pa != priorPh && spare.Any(x => x.Key == pa) && held.Add(pa))
                kept[t.Code] = spare.First(x => x.Key == pa);
        var phPrior = spare.FirstOrDefault(x => x.Key == priorPh && !held.Contains(x.Key));    // (0, 0) when gone
        spare = spare.Where(x => !held.Contains(x.Key) && x.Key != priorPh).ToList();
        // ids with menu logos but no centre-ice texture: for teams that aren't played (no rink to show), and for played
        // teams once the ones with centre ice run out (their rink shows the game's default centre ice)
        var plain = map.Menu.Where(kv => !used.Contains(kv.Key) && !map.Ice.ContainsKey(kv.Key) && kv.Value.Count * 2 >= full)
                            .OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value.Count)).ToList();
        foreach (var t in spec.AllTeams.Where(t => spec.TeamLogoFile(t) != null && !kept.ContainsKey(t.Code)))
            if (prior?.LogoArt.GetValueOrDefault(t.Code, -1) is int pa && pa >= 0 && plain.FirstOrDefault(x => x.Key == pa) is { Key: > 0 } pp && held.Add(pa))
                kept[t.Code] = pp;
        plain = plain.Where(x => !held.Contains(x.Key)).ToList();
        bool Played(TeamSpec t) => spec.Groups.FirstOrDefault(g => g.League?.Id == t.LeagueId)?.Playable != false;
        // played teams first (those with a rink before the rest), then the others
        var order = spec.AllTeams.Where(t => spec.TeamLogoFile(t) != null)
                        .OrderBy(t => Played(t) ? 0 : 1).ThenBy(t => spec.RinkFile(t) != null ? 0 : 1).ToList();
        int k = 0, kp2 = 0;
        foreach (var t in order)
        {
            (int art, int styles) pick;
            bool ice;
            if (kept.TryGetValue(t.Code, out var kp)) { pick = kp; ice = map.Ice.ContainsKey(kp.Item1); }
            else if (Played(t) && k < spare.Count) { pick = spare[k++]; ice = true; }
            else if (kp2 < plain.Count) { pick = plain[kp2++]; ice = false; }
            else if (k < spare.Count) { pick = spare[k++]; ice = true; }
            else { r.Errors.Add($"{t.Code}: no spare logo art id left."); continue; }
            var (art, styles) = pick;
            r.LogoArt[t.Code] = art;
            r.Lines.Add($"{t.Code}: logo art id {art} ({styles} menu styles{(ice ? " + centre ice" : ", no centre-ice texture")})");
            if (!ice && Played(t)) r.Warnings.Add($"{t.Code}: its logo art id has no centre-ice texture (all {spare.Count + held.Count} with one are taken): its rink shows the game's default centre ice");
        }
        var bare = spec.AllTeams.Where(t => spec.TeamLogoFile(t) == null).ToList();
        if (bare.Count == 0) return;
        if (phPrior.Item1 == 0 && k >= spare.Count) { r.Errors.Add($"No spare logo art id left for the placeholder logo ({bare.Count} teams without a logo)."); return; }
        var (ph, phStyles) = phPrior.Item1 != 0 ? phPrior : spare[k];            // the earlier placeholder id when still spare
        foreach (var t in bare) r.LogoArt[t.Code] = ph;
        r.PlaceholderArt = ph;
        r.Lines.Add($"{bare.Count} teams without a logo share placeholder art id {ph} ({phStyles} menu styles), drawn from the league logo: {string.Join(", ", bare.Select(t => t.Code))}");
    }

    private static void PreviewLeagueArt(AssetIndex index, T3Db db, PackageSpec spec, Report r)
    {
        var styles = new Dictionary<int, int>();
        foreach (string name in index.Res.Keys)
            if (LeagueLogo.Match(name) is { Success: true } m && !m.Groups[3].Success)
                styles[int.Parse(m.Groups[2].Value)] = styles.GetValueOrDefault(int.Parse(m.Groups[2].Value)) + 1;
        var leagues = db.Table("stockleagues");
        var used = Valid(db, leagues).Select(i => Convert.ToInt32(db.Get(leagues, i, F(leagues, "artid")))).ToHashSet();
        var spare = styles.Where(kv => !used.Contains(kv.Key)).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).ToList();
        int k = 0;
        foreach (var g in spec.Groups.Where(g => g.League != null))
        {
            if (k >= spare.Count) { r.Warnings.Add($"No spare league logo art id for league {g.League.Id}: its logo would replace the league's own."); continue; }
            r.LeagueArts[g.League.Id] = spare[k].Key;
            if (g == spec) r.LeagueArt = spare[k].Key;
            r.Lines.Add($"League {g.League.Id} logo: spare art id {spare[k].Key} ({spare[k].Value} styles; {spare.Count} spare league art ids)");
            k++;
        }
    }

    // ---------------------------------------------------------------- language strings

    public static (T3Table Table, int Row, T3Field Text)? FindString(T3Db loc, string id)
    {
        foreach (var t in loc.Tables.Where(t => t.HasCompressedStrings && t.WrittenRecords > 0))
        {
            var fid = t.Fields.FirstOrDefault(f => f.Name.Equals("stringid", StringComparison.OrdinalIgnoreCase));
            var ftx = t.Fields.FirstOrDefault(f => f.Name.Equals("sourcetext", StringComparison.OrdinalIgnoreCase));
            if (fid == null || ftx == null) continue;
            for (int i = 0; i < t.WrittenRecords; i++)
                if (!loc.IsInvalid(t, i) && string.Equals(loc.Get(t, i, fid)?.ToString(), id, StringComparison.OrdinalIgnoreCase))
                    return (t, i, ftx);
        }
        return null;
    }

    private static void PreviewStrings(T3Db loc, PackageSpec spec, Report r)
    {
        foreach (var (id, text) in spec.League?.Strings ?? new())
        {
            var s = FindString(loc, id);
            if (s == null) { r.Errors.Add($"String {id}: not in the language file."); continue; }
            var (t, row, f) = s.Value;
            string old = loc.Get(t, row, f)?.ToString();
            var (need, have) = loc.Budget(t, row, f, text);
            if (old == text) r.Lines.Add($"String {id}: already \"{text}\"");
            else if (need < 0 || need > have) r.Errors.Add($"String {id}: \"{text}\" needs {need} bytes, room {have} (now \"{old}\").");
            else r.Lines.Add($"String {id}: \"{old}\" -> \"{text}\"");
        }
    }

    // ---------------------------------------------------------------- name sets

    /// <summary>One language string of a name set.</summary>
    public sealed record NameString(string Prefix, T3Table Table, int Row, T3Field Text, string Current);

    /// <summary>The name set a team would take: its artabbr, and the text each string would get.</summary>
    public sealed record NameSetChoice(string ArtAbbr, string OldName, List<(NameString S, string Text, bool Exact, bool Kept)> Strings)
    {
        /// <summary>Strings that can't take any of the team's texts and would keep the old club's text.</summary>
        public int KeptCount => Strings.Count(s => s.Kept);
        public int ExactCount => Strings.Count(s => s.Exact);
        /// <summary>The set's team-name string holds the team's full name (what team lists show).</summary>
        /// A set without a team-name string shows TEAMLINE1 + TEAMLINE2 instead (Denver's DRES: "Denver" + "Pioneers").
        /// <summary>Built for the "school" naming style: the list name must then come from the team-name string.</summary>
        public bool SchoolOnly { get; init; }
        public bool FullName => Strings.Any(s => s.S.Prefix == "NHLTEAMNAME_") || SchoolOnly
            ? Strings.Any(s => s.S.Prefix == "NHLTEAMNAME_" && s.Exact)
            : Strings.Any(s => s.S.Prefix == "TEAMLINE1_" && s.Exact) && Strings.Any(s => s.S.Prefix == "TEAMLINE2_" && s.Exact);
        /// <summary>Strings that would keep the old club's visible text (a blank one is harmless).</summary>
        public int VisibleKept => Strings.Count(s => s.Kept && !string.IsNullOrWhiteSpace(s.Text));
        /// <summary>Lower is better: no old club text showing, the full team name, fewest blanked/kept, most exact.</summary>
        public (int, int, int, int) Score => (VisibleKept, FullName ? 0 : 1, KeptCount, -ExactCount);
    }

    private static readonly string[] Prefixes =
        { "NHLTEAMNAME_ABBR3_", "TXT_NICKNAME_ALT_", "NHLTEAMNAME_", "TEAMLINE1_", "TEAMLINE2_", "NHLCITYNAME_", "TXT_NICKNAME_", "X_XLA_TEAM_", "NICKLINE2_" };

    /// <summary>Every team-name string in the language file, by artabbr (upper case) then prefix.</summary>
    public static Dictionary<string, Dictionary<string, NameString>> NameSets(T3Db loc)
    {
        var sets = new Dictionary<string, Dictionary<string, NameString>>();
        foreach (var t in loc.Tables.Where(t => t.HasCompressedStrings && t.WrittenRecords > 0))
        {
            var fid = t.Fields.FirstOrDefault(f => f.Name.Equals("stringid", StringComparison.OrdinalIgnoreCase));
            var ftx = t.Fields.FirstOrDefault(f => f.Name.Equals("sourcetext", StringComparison.OrdinalIgnoreCase));
            if (fid == null || ftx == null) continue;
            for (int i = 0; i < t.WrittenRecords; i++)
            {
                if (loc.IsInvalid(t, i)) continue;
                string id = loc.Get(t, i, fid)?.ToString()?.ToUpperInvariant();
                if (id == null) continue;
                string p = Prefixes.FirstOrDefault(x => id.StartsWith(x, StringComparison.Ordinal));
                if (p == null) continue;
                string key = id[p.Length..];
                if (!sets.TryGetValue(key, out var set)) sets[key] = set = new Dictionary<string, NameString>();
                set[p] = new NameString(p, t, i, ftx, loc.Get(t, i, ftx)?.ToString() ?? "");
            }
        }
        return sets;
    }

    /// <summary>
    /// Sets that belong to no team and look like a real club's (not numbered tournament/online sets), so a new team can
    /// take them over. <paramref name="taken"/>: artabbrs other packages or this one already hold.
    /// </summary>
    /// <param name="own">Team indexes of the package itself (already installed): their sets count as free.</param>
    public static List<string> Donors(T3Db db, Dictionary<string, Dictionary<string, NameString>> sets, ISet<string> taken, ISet<int> own = null)
    {
        var teams = db.Table("exhibitionteams");
        var live = Valid(db, teams).Where(i => own == null || !own.Contains(Convert.ToInt32(db.Get(teams, i, F(teams, "index")))))
                                   .Select(i => db.Get(teams, i, F(teams, "artabbr"))?.ToString()?.ToUpperInvariant()).ToHashSet();
        string[] notClub = { "Team ", "All-Star", "EASHL", "Free Agent", "Icons", "World", "North America" };
        return sets.Where(kv => !live.Contains(kv.Key) && !taken.Contains(kv.Key) && kv.Key != "LOCI"
                                && !kv.Key.Any(char.IsDigit) && kv.Value.Count >= 4
                                && kv.Value.TryGetValue("NHLTEAMNAME_", out var n) && !notClub.Any(x => n.Current.Contains(x))
                                && !(kv.Value.TryGetValue("NHLTEAMNAME_ABBR3_", out var a) && (a.Current is "CPU" or "USR")))
                   .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    /// <summary>What every string of <paramref name="set"/> would hold for team <paramref name="t"/>; null when the abbreviation doesn't fit.</summary>
    public static NameSetChoice Fit(T3Db loc, string key, Dictionary<string, NameString> set, TeamSpec t, bool schoolOnly = false)
    {
        bool xlaIsAbbr = !set.TryGetValue("X_XLA_TEAM_", out var xla) || xla.Current.Length <= 5;
        var want = new Dictionary<string, string[]>
        {
            ["NHLTEAMNAME_"] = schoolOnly ? new[] { t.School, t.NameShort } : new[] { t.Name, t.NameShort, t.School },
            ["TEAMLINE1_"] = new[] { t.School },
            ["TEAMLINE2_"] = new[] { t.Nickname, t.NicknameShort }, ["NICKLINE2_"] = new[] { t.Nickname, t.NicknameShort },
            ["TXT_NICKNAME_"] = new[] { t.Nickname, t.NicknameShort }, ["TXT_NICKNAME_ALT_"] = new[] { t.Nickname, t.NicknameShort },
            ["NHLCITYNAME_"] = new[] { t.City ?? t.School, t.School, t.Nickname },
            ["NHLTEAMNAME_ABBR3_"] = new[] { t.Abbr },
            ["X_XLA_TEAM_"] = xlaIsAbbr ? new[] { t.Abbr } : schoolOnly ? new[] { t.School, t.NameShort } : new[] { t.Name, t.NameShort, t.School },
        };
        var list = new List<(NameString, string, bool, bool)>();
        foreach (var (p, s) in set)
        {
            var cands = want[p].Where(c => !string.IsNullOrEmpty(c)).ToArray();
            bool isAbbr = p == "NHLTEAMNAME_ABBR3_" || (p == "X_XLA_TEAM_" && xlaIsAbbr);
            string pick = cands.FirstOrDefault(c => c == s.Current || Fits(loc, s, c));
            bool kept = false;
            if (pick == null)
            {
                if (isAbbr) return null;
                // can't take any of our texts: a minor line is blanked rather than showing the old club's text; the
                // team name itself is left as it is (reported)
                pick = p != "NHLTEAMNAME_" && Fits(loc, s, " ") ? " " : s.Current;
                kept = true;
            }
            list.Add((s, pick, pick == cands[0], kept));
        }
        return new NameSetChoice(key, set.TryGetValue("NHLTEAMNAME_", out var nm) ? nm.Current : key, list) { SchoolOnly = schoolOnly };
    }

    private static bool Fits(T3Db loc, NameString s, string text)
    {
        var (need, have) = loc.Budget(s.Table, s.Row, s.Text, text);
        return need >= 0 && have > 0 && need <= have;
    }

    private static void PreviewNameSets(T3Db db, T3Db loc, PackageSpec spec, Report r, PackageInstaller.Resolved prior = null)
    {
        var sets = NameSets(loc);
        var avoid = new HashSet<string>((spec.NameSets?.Avoid ?? new()).Select(a => a.ToUpperInvariant()));
        var donors = Donors(db, sets, avoid, spec.AllTeams.Select(t => t.Index).ToHashSet());
        r.Lines.Add($"Name sets: {sets.Count} in the language file, {donors.Count} usable donors (no team, a real club's).");
        // earlier sets kept first; when keeping them leaves a team without any set, a fresh assignment for every team
        var picks = Assign(loc, sets, donors, spec, prior, out var failed);
        if (failed.Count > 0 && prior?.NameSets.Count > 0)
        {
            var fresh = Assign(loc, sets, donors, spec, null, out var failed2);
            if (failed2.Count < failed.Count)
            {
                r.Lines.Add($"Name sets: the earlier ones leave no room for {string.Join(", ", failed.Select(t => t.Code))}: every team picks afresh.");
                picks = fresh; failed = failed2;
            }
        }
        foreach (var t in failed) r.Errors.Add($"{t.Code}: no name set can hold the abbreviation \"{t.Abbr}\".");
        foreach (var t in spec.AllTeams)
        {
            if (!picks.TryGetValue(t.Code, out var best)) continue;
            r.NameSets[t.Code] = best;
            var shortened = best.Strings.Where(s => !s.Exact && !s.Kept).Select(s => $"{s.S.Prefix.TrimEnd('_')}=\"{s.Text}\"");
            var kept = best.Strings.Where(s => s.Kept).Select(s => $"{s.S.Prefix.TrimEnd('_')}=\"{s.Text}\"");
            string line = $"{t.Code}: name set {best.ArtAbbr} (was \"{best.OldName}\"), {best.ExactCount}/{best.Strings.Count} strings exact" +
                          (shortened.Any() ? "; shortened: " + string.Join(", ", shortened) : "");
            r.Lines.Add(line);
            if (best.KeptCount > 0) r.Warnings.Add($"{t.Code}: {best.KeptCount} string(s) of set {best.ArtAbbr} can't hold the team's text and keep the old club's: " + string.Join(", ", kept));
        }
    }

    /// <summary>One name set per team, no set twice: earlier choices (prior) first while they're donors and fit, then
    /// long names first (few sets have room for them), then long codes (few hold a 4-letter abbreviation).</summary>
    private static Dictionary<string, NameSetChoice> Assign(T3Db loc, Dictionary<string, Dictionary<string, NameString>> sets, List<string> donors,
                                                            PackageSpec spec, PackageInstaller.Resolved prior, out List<TeamSpec> failed)
    {
        var taken = new HashSet<string>();
        var picks = new Dictionary<string, NameSetChoice>();
        failed = new List<TeamSpec>();
        bool school = spec.NameSets?.SchoolOnly == true;
        foreach (var t in spec.AllTeams)
            if (prior?.NameSets.GetValueOrDefault(t.Code) is { } ps && donors.Contains(ps.ToUpperInvariant()) && !taken.Contains(ps.ToUpperInvariant())
                && Fit(loc, ps.ToUpperInvariant(), sets[ps.ToUpperInvariant()], t, school) is { } kf)
            { picks[t.Code] = kf; taken.Add(kf.ArtAbbr); }
        foreach (var t in spec.AllTeams.Where(t => !picks.ContainsKey(t.Code))
                     .OrderByDescending(t => System.Text.Encoding.UTF8.GetByteCount(t.Name ?? "")).ThenByDescending(t => t.Abbr?.Length ?? 0))
        {
            var best = donors.Where(k => !taken.Contains(k)).Select(k => Fit(loc, k, sets[k], t, school)).Where(c => c != null)
                             .OrderBy(c => c.Score).ThenByDescending(c => c.Strings.Count).FirstOrDefault();
            if (best == null) { failed.Add(t); continue; }
            taken.Add(best.ArtAbbr);
            picks[t.Code] = best;
        }
        return picks;
    }
}
