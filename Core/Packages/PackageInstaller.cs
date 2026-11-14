using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NHL27Kit.Core.Packages;

/// <summary>
/// Installs a package (docs/PACKAGES.md): runs its recipe against the game as it is now and records every byte it
/// changes (databases, language strings, logo textures) as one mod-list entry of kind "package". Reinstalling first
/// restores the earlier entry, so it always builds on the game without the package. Choices that must stay put across
/// rebuilds (logo art ids, name sets, the group id) are kept in mods/packages/&lt;id&gt;/resolved.json.
/// </summary>
public static class PackageInstaller
{
    public const string Kind = "package";

    public sealed class Resolved
    {
        public Dictionary<string, int> LogoArt { get; set; } = new();
        public Dictionary<string, string> NameSets { get; set; } = new();
        public int LeagueArt { get; set; } = -1;
        public int Group { get; set; } = -1;
        /// <summary>The extra leagues' logo art ids and team group ids, by league id.</summary>
        public Dictionary<int, int> LeagueArts { get; set; } = new();
        public Dictionary<int, int> Groups { get; set; } = new();
    }

    public sealed class Result
    {
        public ModEntry Entry { get; set; }
        public List<string> Lines { get; } = new();
        public List<string> Warnings { get; } = new();
        public string CodeModsFile { get; set; }
        public string Report { get; set; }
        /// <summary>Commits already in the game (art id, school team index, seasons after the roster season), from the players step.</summary>
        public List<(int ArtId, int Team, int Offset)> Commits { get; } = new();
        /// <summary>NCAA classes (art id, offset 0 Fr .. 4 Gr), from the players step (code mod part 10).</summary>
        public List<(int ArtId, int Offset)> Entries { get; } = new();
        /// <summary>Classed players' class offsets by game id, until EntryYears turns them into <see cref="Entries"/>.</summary>
        public Dictionary<int, int> EntryGames { get; } = new();
        /// <summary>Commits not in the game (name, school team index, seasons after the roster season): matched by name
        /// if the game generates them later (code mod part 11).</summary>
        public List<(string Name, int Team, int Offset)> NameCommits { get; } = new();
        /// <summary>Each players file's commits, from its group's players step until PackagePlayers.ApplyCommits.</summary>
        internal List<PackagePlayers.CommitBatch> PendingCommits { get; } = new();
        /// <summary>Game players already placed for a current-season roster-file entry (one game player per entry).</summary>
        internal HashSet<int> TakenPlayers { get; } = new();
        /// <summary>Current-season roster-file players placed by the players step: (team code, name slug) → game id.</summary>
        public Dictionary<(string Code, string Slug), int> Placed { get; } = new();
        /// <summary>How long each install step took.</summary>
        public List<string> Timings { get; } = new();
    }

    public static string StateDir(ModStore store, string id) => Path.Combine(store.Dir, "packages", id);
    public static string CodeModsFile(ModStore store) => Path.Combine(store.Dir, "codemods", "nhl27_code_mods.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    // ---------------------------------------------------------------- install

    /// <param name="dry">Plan everything and report, but write nothing (no game files, no mod list, no code mods).</param>
    /// <param name="reopen">Opens the asset index again. Needed when the package is installed: taking it down changes
    /// the tocs (logo copies appended to a cas are dropped), so an index read before that points past their end.</param>
    public static Result Install(AssetIndex index, ModStore store, string game, PackageSpec spec, bool dry, Action<string> log,
                                 Func<AssetIndex> reopen = null)
    {
        var r = new Result();
        log ??= _ => { };
        // how long each step takes, in the report
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Took(string step) { r.Timings.Add($"{step}: {clock.Elapsed.TotalSeconds:0.0} s"); log($"[{step}: {clock.Elapsed.TotalSeconds:0.0} s]"); clock.Restart(); }

        // 1. back to the game without the package
        var existing = store.Find(Kind, spec.Id);
        if (existing != null && !dry && store.RestoreIfApplied(existing, game, log))
        {
            log($"Took down the installed {spec.Id} first");
            Took("taking down the installed package");
            index = reopen?.Invoke() ?? throw new InvalidOperationException(
                $"{spec.Id} is installed: reinstalling needs a way to re-read the asset index after taking it down.");
            Took("re-reading the asset index");
        }
        else if (existing != null && dry && store.State(existing, game) is ModState.Applied or ModState.Partial)
            throw new InvalidOperationException($"{spec.Id} is installed: a dry run needs it removed first (package-remove {spec.Id}).");

        // 2. check and resolve
        var resolved = LoadResolved(store, spec.Id);
        var check = PackageCheck.Run(index, spec, resolved);       // keeps earlier choices where it can, all unique
        if (!check.Ok) throw new InvalidOperationException("The package doesn't pass package-check:\n  " + string.Join("\n  ", check.Errors));
        var legacy = new Legacy(index);
        var db = legacy.OpenDb(PackageCheck.GameDb);
        var loc = legacy.OpenDb(PackageCheck.LocDb);
        // every database edit of the recipe in one batch: each table's checksum is fixed once at the end, not after every
        // field (an unbatched Set re-hashes its whole table; thousands of player fields made installs take an hour)
        var dbBatch = db.Batch();
        var locBatch = loc.Batch();
        PackageCheck.Recycle(db, spec, r.Lines);
        Resolve(db, loc, spec, check, resolved, r);

        Took("check and resolve");
        // 3. the recipe, in memory
        var nameSets = PackageCheck.NameSets(loc);
        foreach (var g in spec.Groups) ApplyLeague(db, loc, g, g == spec ? resolved.LeagueArt : resolved.LeagueArts.GetValueOrDefault(g.League.Id, -1), r);
        ApplyTeams(db, loc, spec, resolved, nameSets, r);
        Took("leagues and teams (database)");
        foreach (var g in spec.Groups) PackagePlayers.Apply(db, g, r);
        PackagePlayers.ApplyCommits(db, r);
        Took("players (database)");
        var portraits = PackagePortraits.Assign(index, legacy, db, spec, r);
        Took("portrait slots (database)");
        PackagePlayers.EntryYears(db, r, spec.Teams.Select(t => t.Index).ToHashSet());
        foreach (var (id, text) in spec.Strings ?? new()) SetString(loc, id, text, r);
        Took("classes and strings (database)");
        // the teams' own uniforms (Jersey Studio exports): their jersey menus now, their kit folders' bytes in step 4
        var uniforms = spec.Groups.Any(g => PackageUniforms.Folder(g) != null)
            ? PackageUniforms.ApplySlots(db, Kits.Find(index), spec, r) : new();
        Took("uniform slots (database)");

        dbBatch.Dispose();
        locBatch.Dispose();
        Took("database checksums");
        // 4. bytes
        var tocs = new TocCache(index);
        var locations = LocationIndex.Build(index, tocs);
        var patches = new List<PatchData>();
        // planning reads the same bundle records for thousands of textures: once each (nothing is written until step 6)
        using var records = ChunkCopies.CacheRecords();
        // planning reads the same TOC and CAS files thousands of times: one read handle each (closed before step 6 writes)
        using var held = HeldFiles.Keep();
        // encoded textures (logos, portraits, uniforms) kept between installs: only new or changed images are encoded
        // (a dry run keeps what it encodes but deletes nothing)
        using var encoded = PlanCache.Begin(Path.Combine(StateDir(store, spec.Id), "cache"), prune: !dry);
        foreach (var (name, d) in new[] { (PackageCheck.GameDb, db), (PackageCheck.LocDb, loc) })
        {
            var f = legacy.Find(name);
            var raw = legacy.ReadChunk(f.ChunkId);
            d.Data.CopyTo(raw, f.Offset);
            var plan = ChunkPatcher.Plan(index, f.ChunkId, raw, locations, tocs, log);
            patches.AddRange(plan.Patches);
            r.Lines.Add($"{name}: {plan.Patches.Count} byte ranges");
        }
        Took("database bytes");
        PlanLogos(index, spec, resolved, locations, tocs, patches, r, log);
        Took("logos");
        PlanPortraits(index, portraits, locations, tocs, patches, r);
        Took("portraits");
        PlanUniforms(index, uniforms, locations, tocs, patches, r);

        Took($"uniform textures (encoded textures: {PlanCache.Hits} reused, {PlanCache.Misses} encoded)");
        // 5. code mods (+ the standalone ones kept next to the patch file, which no package owns)
        var codeMods = BuildCodeMods(game, spec, r);
        var used = codeMods["mods"]!.AsArray().SelectMany(m => m!["memory"]!.AsArray()).Select(e => e!["offset"]!.ToString()).ToHashSet();
        foreach (var m in StandaloneMods(store, r.Lines))
        {
            var clash = m["memory"]!.AsArray().Select(e => e!["offset"]!.ToString()).Where(used.Contains).ToList();
            if (clash.Count > 0) { r.Warnings.Add($"standalone mod \"{m["name"]}\" left out: it patches {string.Join(", ", clash)}, which the package patches too"); continue; }
            codeMods["mods"]!.AsArray().Add(m);
        }

        held.Dispose();
        r.Report = Report(spec, resolved, r);
        if (dry)
        {
            r.Lines.Add($"dry run: {patches.Count} byte ranges planned, nothing written");
            return r;
        }

        // 6. write: one entry, applied; code mods file; resolved choices; report
        string summary = $"{spec.Title} {spec.Version}: " + string.Join("; ", spec.Groups.Select(g => $"league {g.League?.Id}, {g.Teams.Count} teams ({string.Join(", ", g.Teams.Select(t => t.Code))})")) + ", logos, strings";
        r.Entry = store.Add(Kind, spec.Id, spec.Folder, summary, patches, r.Warnings);
        Took("saving the mod entry");
        store.Apply(r.Entry, game, log);
        Took("writing into the game");
        SaveResolved(store, spec.Id, resolved);
        string cm = CodeModsFile(store);
        Directory.CreateDirectory(Path.GetDirectoryName(cm));
        File.WriteAllText(cm, codeMods.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        r.CodeModsFile = cm;
        Directory.CreateDirectory(Path.Combine(spec.Folder, "build"));
        File.WriteAllText(Path.Combine(spec.Folder, "build", "install-report.md"), r.Report);
        return r;
    }

    /// <summary>Restores the package's entry and takes its code mods out of the managed patch file.</summary>
    public static List<string> Remove(ModStore store, string game, string id, Action<string> log)
    {
        var lines = new List<string>();
        var e = store.Find(Kind, id) ?? throw new InvalidOperationException($"No package {id} in the mod list.");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        store.RestoreIfApplied(e, game, log);
        double restored = clock.Elapsed.TotalSeconds;
        store.Remove(e);
        lines.Add($"{id}: restored and removed from the mod list (its resolved choices are kept for a reinstall) in {restored:0.0} s + {clock.Elapsed.TotalSeconds - restored:0.0} s");
        string cm = CodeModsFile(store);
        if (File.Exists(cm))
        {
            var doc = JsonNode.Parse(File.ReadAllText(cm)).AsObject();
            var keep = new JsonArray();
            foreach (var m in StandaloneMods(store, lines)) keep.Add(m);
            doc["mods"] = keep;
            File.WriteAllText(cm, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            lines.Add(keep.Count == 0 ? $"{cm}: emptied (no code mods); the emulator still loads it, with nothing in it"
                                      : $"{cm}: package code mods taken out; {keep.Count} standalone mod(s) kept");
        }
        return lines;
    }

    /// <summary>Folder of code mods no package owns (each file: {"mods": [...]}, the patch file's format). They're
    /// written into the patch file with every package install and kept when a package is removed.</summary>
    public static string StandaloneDir(ModStore store) => Path.Combine(store.Dir, "codemods", "standalone");

    private static List<JsonNode> StandaloneMods(ModStore store, List<string> lines)
    {
        var list = new List<JsonNode>();
        string dir = StandaloneDir(store);
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var mods = JsonNode.Parse(File.ReadAllText(f))?["mods"]?.AsArray();
            if (mods == null) continue;
            foreach (var m in mods) list.Add(m!.DeepClone());
            lines.Add($"standalone code mods: {Path.GetFileName(f)} ({mods.Count})");
        }
        return list;
    }

    // ---------------------------------------------------------------- resolve

    private static Resolved LoadResolved(ModStore store, string id)
    {
        string f = Path.Combine(StateDir(store, id), "resolved.json");
        return File.Exists(f) ? JsonSerializer.Deserialize<Resolved>(File.ReadAllText(f), Json) ?? new Resolved() : new Resolved();
    }

    private static void SaveResolved(ModStore store, string id, Resolved res)
    {
        Directory.CreateDirectory(StateDir(store, id));
        File.WriteAllText(Path.Combine(StateDir(store, id), "resolved.json"), JsonSerializer.Serialize(res, Json));
    }

    private static T3Field F(T3Table t, string n) => t.Fields.First(f => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
    private static IEnumerable<int> Valid(T3Db db, T3Table t) => Enumerable.Range(0, t.WrittenRecords).Where(i => !db.IsInvalid(t, i));
    private static int Int(T3Db db, T3Table t, int row, string f) => Convert.ToInt32(db.Get(t, row, F(t, f)));

    /// <summary>Keeps earlier choices that are still free, takes the check's picks for the rest.</summary>
    private static void Resolve(T3Db db, T3Db loc, PackageSpec spec, PackageCheck.Report check, Resolved res, Result r)
    {
        var teams = db.Table("exhibitionteams");
        var usedArt = Valid(db, teams).Select(i => Int(db, teams, i, "artid")).ToHashSet();
        var usedAbbr = Valid(db, teams).Select(i => db.Get(teams, i, F(teams, "artabbr"))?.ToString()?.ToUpperInvariant()).ToHashSet();
        var sets = PackageCheck.NameSets(loc);
        // logo art ids and name sets: the check's picks, which already keep earlier choices where they're still free
        // and fit, and never give two teams the same one (the placeholder id is the one deliberate share)
        res.LogoArt.Clear();
        foreach (var (code, art) in check.LogoArt) res.LogoArt[code] = art;
        res.NameSets.Clear();
        foreach (var (code, ns) in check.NameSets) res.NameSets[code] = ns.ArtAbbr;
        var dup = res.NameSets.GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
        if (dup.Count > 0) throw new InvalidOperationException("Two teams got the same name set: " + string.Join("; ", dup.Select(g => $"{g.Key}: {string.Join(", ", g.Select(kv => kv.Key))}")));
        var leagues = db.Table("stockleagues");
        var usedLeagueArt = Valid(db, leagues).Select(i => Int(db, leagues, i, "artid")).ToHashSet();
        if (res.LeagueArt < 0 || usedLeagueArt.Contains(res.LeagueArt)) res.LeagueArt = check.LeagueArt;
        foreach (var g in spec.Groups.Where(g => g != spec && g.League != null))
            if (!res.LeagueArts.TryGetValue(g.League.Id, out int la) || usedLeagueArt.Contains(la) || la == res.LeagueArt)
                res.LeagueArts[g.League.Id] = check.LeagueArts.GetValueOrDefault(g.League.Id, -1);
        // one group id for the package's teams (league, conference and division), unused by any team
        var usedGroups = Valid(db, teams).SelectMany(i => new[] { Int(db, teams, i, "leaguegroup"), Int(db, teams, i, "conferencegroup"), Int(db, teams, i, "divisiongroup") }).ToHashSet();
        if (res.Group < 0 || usedGroups.Contains(res.Group)) res.Group = Enumerable.Range(100, 28).Concat(Enumerable.Range(1, 99)).First(g => !usedGroups.Contains(g));
        usedGroups.Add(res.Group);
        foreach (var g in spec.Groups.Where(g => g != spec && g.League != null))
        {
            if (!res.Groups.TryGetValue(g.League.Id, out int gid) || usedGroups.Contains(gid))
                res.Groups[g.League.Id] = gid = Enumerable.Range(100, 28).Concat(Enumerable.Range(1, 99)).First(x => !usedGroups.Contains(x));
            usedGroups.Add(gid);
        }
        r.Lines.Add($"resolved: group {res.Group}, league logo art {res.LeagueArt}, " +
                    string.Join(", ", spec.AllTeams.Select(t => $"{t.Code} art {res.LogoArt.GetValueOrDefault(t.Code, -1)} / names {res.NameSets.GetValueOrDefault(t.Code)}")));
    }

    // ---------------------------------------------------------------- recipe

    private static void ApplyLeague(T3Db db, T3Db loc, PackageSpec spec, int leagueArt, Result r)
    {
        var leagues = db.Table("stockleagues");
        int row = Valid(db, leagues).FirstOrDefault(i => Int(db, leagues, i, "leagueid") == spec.League.Id, -1);
        if (row < 0) throw new InvalidOperationException($"League {spec.League.Id} isn't in stockleagues.");
        if (leagueArt >= 0 && spec.LeagueLogoFile() != null)
        {
            db.Set(leagues, row, F(leagues, "artid"), leagueArt);
            r.Lines.Add($"league {spec.League.Id}: logo art id {leagueArt}");
        }
        if (!string.IsNullOrEmpty(spec.League.NameKey))
        {
            db.Set(leagues, row, F(leagues, "name"), spec.League.NameKey);
            r.Lines.Add($"league {spec.League.Id}: name from {spec.League.NameKey}");
        }
        foreach (var (id, text) in spec.League.Strings ?? new())
            SetString(loc, id, text, r);
    }

    private static void SetString(T3Db loc, string id, string text, Result r)
    {
        foreach (var t in loc.Tables.Where(t => t.HasCompressedStrings && t.WrittenRecords > 0))
        {
            var fid = t.Fields.FirstOrDefault(f => f.Name.Equals("stringid", StringComparison.OrdinalIgnoreCase));
            var ftx = t.Fields.FirstOrDefault(f => f.Name.Equals("sourcetext", StringComparison.OrdinalIgnoreCase));
            if (fid == null || ftx == null) continue;
            for (int i = 0; i < t.WrittenRecords; i++)
            {
                if (loc.IsInvalid(t, i) || !string.Equals(loc.Get(t, i, fid)?.ToString(), id, StringComparison.OrdinalIgnoreCase)) continue;
                if (loc.Get(t, i, ftx)?.ToString() != text) loc.Set(t, i, ftx, text);
                return;
            }
        }
        r.Warnings.Add($"string {id} not found");
    }

    private static int Color(string hex) =>
        string.IsNullOrWhiteSpace(hex) ? -1 : int.Parse(hex.Trim().TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static string Cut(string s, int maxBytes)
    {
        if (s == null) return "";
        while (System.Text.Encoding.UTF8.GetByteCount(s) > maxBytes) s = s[..^1];
        return s;
    }

    private static void ApplyTeams(T3Db db, T3Db loc, PackageSpec spec, Resolved res, Dictionary<string, Dictionary<string, PackageCheck.NameString>> sets, Result r)
    {
        var teams = db.Table("exhibitionteams");
        var arenas = db.Table("exhibitionarena");
        var byName = spec.AllTeams.Where(t => t.Name != null).ToDictionary(t => t.Name, t => t.Index, StringComparer.OrdinalIgnoreCase);
        foreach (var t in spec.AllTeams.OrderBy(t => t.Index))
        {
            string artAbbr = res.NameSets.GetValueOrDefault(t.Code) ?? t.Abbr;
            var s = t.Data;
            var nt = new TeamCreator.NewTeam(t.Index, Cut(t.Name, 31), Cut(t.Name, 31), Cut(t.City, 29), Cut(t.Abbr, 11), Cut(artAbbr, 11),
                                              res.LogoArt.GetValueOrDefault(t.Code, 0), t.LeagueId, Color(t.Primary) is var p && p >= 0 ? p : null,
                                              Color(t.Secondary) is var q && q >= 0 ? q : null);
            r.Lines.Add(TeamCreator.Create(db, t.Template, nt));
            int row = Valid(db, teams).First(i => Int(db, teams, i, "index") == t.Index);
            void Set(string f, object v) { if (v != null) db.Set(teams, row, F(teams, f), v); }
            void SetColor(string f, string hex) { int c = Color(hex); if (c >= 0) Set(f, c); }
            if (s != null)
            {
                SetColor("fetertiarycolor", s.TertiaryMenuColor);
                SetColor("presprimarycolor", s.PrimaryBroadcastColor); SetColor("pressecondarycolor", s.SecondaryBroadcastColor); SetColor("prestertiarycolor", s.TertiaryBroadcastColor);
                SetColor("overlayprimarycolor", s.OverlayPrimary); SetColor("overlaysecondarycolor", s.OverlaySecondary); SetColor("overlaytertiarycolor", s.OverlayTertiary);
                if (s.AverageAttendancePct is int pct) Set("avgattendance", Math.Clamp(pct, 0, 127));
                if (s.MainRival != null && byName.TryGetValue(s.MainRival, out int r1)) Set("rivalteam", r1);
                if (s.SecondRival != null && byName.TryGetValue(s.SecondRival, out int r2)) Set("rivalteam2", r2);
            }
            int grp = t.LeagueId == spec.League.Id ? res.Group : res.Groups.GetValueOrDefault(t.LeagueId, res.Group);
            Set("leaguegroup", grp); Set("conferencegroup", grp); Set("divisiongroup", grp);
            Set("active", 1);

            // its own arena row: a copy of the template's arena, at the team's index when that's free
            int templateArena = Int(db, teams, row, "arenaid");
            int arenaSrc = Valid(db, arenas).FirstOrDefault(i => Int(db, arenas, i, "index") == templateArena, -1);
            if (arenaSrc >= 0 && arenas.WrittenRecords < arenas.Records)
            {
                var used = Valid(db, arenas).Select(i => Int(db, arenas, i, "index")).ToHashSet();
                int arenaIndex = !used.Contains(t.Index) ? t.Index : Enumerable.Range(0, 1024).Reverse().First(i => !used.Contains(i));
                int ar = arenas.WrittenRecords;
                db.SetWritten(arenas, ar + 1);
                db.WriteRecord(arenas, ar, db.RecordBytes(arenas, arenaSrc));
                db.Set(arenas, ar, F(arenas, "index"), arenaIndex);
                if (s?.ArenaName != null) db.Set(arenas, ar, F(arenas, "arenaname"), Cut(s.ArenaName, 44));
                if (s?.ArenaCity != null) db.Set(arenas, ar, F(arenas, "cityname"), Cut(s.ArenaCity.Split(',')[0], 29));
                if (s?.Capacity is int cap) db.Set(arenas, ar, F(arenas, "maxattendance"), Math.Clamp(cap, 0, (1 << 17) - 1));
                if (s?.YearOpened is int yo) db.Set(arenas, ar, F(arenas, "yearopened"), Math.Clamp(yo, 0, 4095));
                // the team's arena settings: any exhibitionarena field (goalhorn = an audio id from goalhorns.csv, seat
                // and wall colours, scoreboardid, nettingcolorid, ...)
                foreach (var (field, value) in t.Arena ?? new())
                {
                    var af = arenas.Fields.FirstOrDefault(x => x.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
                    if (af == null) { r.Warnings.Add($"{t.Code}: arena has no field \"{field}\""); continue; }
                    db.Set(arenas, ar, af, value);
                }
                if (t.Arena?.Count > 0) r.Lines.Add($"{t.Code}: arena settings {string.Join(", ", t.Arena.Select(kv => $"{kv.Key}={kv.Value}"))}");
                // crowd lights in the team's colour: the data file's crowd_light_color, else its primary colour (a league
                // with "crowdLight": false keeps the template's)
                var group = spec.Groups.FirstOrDefault(g => g.League?.Id == t.LeagueId) ?? spec;
                int crowd = Color(s?.CrowdLightColor) is var cl && cl >= 0 ? cl : Color(t.Primary);
                if (group.CrowdLight != false && group.Playable != false && crowd >= 0 && arenas.Fields.FirstOrDefault(x => x.Name == "crowdlightcolor") is { } clf)
                    db.Set(arenas, ar, clf, crowd);
                SortByIndex(db, arenas);
                row = Valid(db, teams).First(i => Int(db, teams, i, "index") == t.Index);
                db.Set(teams, row, F(teams, "arenaid"), arenaIndex);
                r.Lines.Add($"{t.Code}: arena {arenaIndex} \"{s?.ArenaName}\"");
            }

            // name strings
            if (sets.TryGetValue(artAbbr.ToUpperInvariant(), out var set))
            {
                var choice = PackageCheck.Fit(loc, artAbbr.ToUpperInvariant(), set, t, spec.NameSets?.SchoolOnly == true);
                if (choice == null) r.Warnings.Add($"{t.Code}: name set {artAbbr} no longer fits");
                else
                    foreach (var (ns, text, exact, kept) in choice.Strings)
                    {
                        if (kept && !string.IsNullOrWhiteSpace(text)) { r.Warnings.Add($"{t.Code}: {ns.Prefix}{artAbbr} keeps \"{text}\" (no room)"); continue; }
                        if (kept) r.Lines.Add($"{t.Code}: {ns.Prefix}{artAbbr} blanked (no room for the team's text)");
                        if (loc.Get(ns.Table, ns.Row, ns.Text)?.ToString() != text) loc.Set(ns.Table, ns.Row, ns.Text, text);
                    }
            }
            else r.Warnings.Add($"{t.Code}: no name set {artAbbr} in the language file: its names won't show");
        }
    }

    private static void SortByIndex(T3Db db, T3Table t)
    {
        var fi = F(t, "index");
        var order = Enumerable.Range(0, t.WrittenRecords)
            .OrderBy(i => db.IsInvalid(t, i) ? 1 : 0).ThenBy(i => db.IsInvalid(t, i) ? 0 : Convert.ToInt32(db.Get(t, i, fi))).ToList();
        if (!order.SequenceEqual(Enumerable.Range(0, t.WrittenRecords))) db.Reorder(t, order);
    }

    // ---------------------------------------------------------------- logos

    private static readonly Regex LeagueLogo = new(@"^ui/textures/common/leaguelogos/(.+)/l(\d+)(_fr)?$", RegexOptions.IgnoreCase);
    private static readonly string[] MonoStyles = { "extralargemono", "largemono", "mediummono", "extrasmallmono", "hutcardbw" };

    /// <summary>Per install: where grown chunks go in each CAS file, and which logo textures use each chunk id.</summary>
    private sealed class LogoState
    {
        public Dictionary<string, long> Ends { get; } = new();
        public Dictionary<Guid, List<string>> ChunkOwners { get; } = new();
        /// <summary>Seconds spent: in-place planning (encode + patch plan), finding copies + reframing, growing; reading
        /// the logo files (and the rink cut) and fitting them to each texture's size.</summary>
        public double InPlace, Copies, Grow, Load, Fit;
    }

    private static void PlanLogos(AssetIndex index, PackageSpec spec, Resolved res, LocationIndex locations, TocCache tocs, List<PatchData> patches, Result r, Action<string> log)
    {
        var scan = System.Diagnostics.Stopwatch.StartNew();
        var map = TeamLogos.All(index);
        var st = new LogoState();
        // every menu/league logo texture's chunk id: a chunk shared by two textures can't be given new data for one of them
        ScanOwners(index, index.Res.Keys.Where(k => k.StartsWith("ui/textures/common/teamlogos/", StringComparison.OrdinalIgnoreCase)
                                                || k.StartsWith("ui/textures/common/leaguelogos/", StringComparison.OrdinalIgnoreCase)
                                                || k.StartsWith("content/worlds/rink/textures/centerlogo/", StringComparison.OrdinalIgnoreCase)), st);
        log($"logo textures and their chunks found in {scan.Elapsed.TotalSeconds:0.0} s ({st.ChunkOwners.Count} chunks)");
        var drawn = new HashSet<int>();
        foreach (var t in spec.AllTeams)
        {
            string main = spec.TeamLogoFile(t);
            bool placeholder = main == null;
            main ??= spec.LeagueLogoFile();                              // no logo yet: the shared placeholder, the league logo
            if (main == null || !res.LogoArt.TryGetValue(t.Code, out int art)) { r.Warnings.Add($"{t.Code}: no logo file or art id, logos left as they are"); continue; }
            if (!drawn.Add(art)) continue;                               // the placeholder id is drawn once
            var loading = System.Diagnostics.Stopwatch.StartNew();
            string Extra(string kind) => placeholder ? null : spec.TeamExtraFile(t, kind);
            // decoded only if a texture isn't in the plan cache (Source)
            var img = Source.File(main);
            var light = Extra("lightbg") is { } lf ? Source.File(lf) : img;
            var mono = Extra("mono") is { } mf ? Source.File(mf) : Source.Derived("silhouette", img, i => (Png.Silhouette(i.Rgba), i.W, i.H));
            var ice = Extra("ice") is { } icf ? Source.File(icf) : img;
            // a rink layout: centre ice cut from it, on this team's own art id only (never the shared placeholder's)
            string rink = spec.RinkFile(t);
            if (rink == null && spec.RinkJpeg(t) is { } jpg) r.Warnings.Add($"{t.Code}: {Path.GetFileName(jpg)} is a JPEG; save it as {t.Code}.png for its centre ice");
            if (rink != null && placeholder) r.Warnings.Add($"{t.Code}: has a rink but no logo of its own (its art id is the shared placeholder's); rink skipped");
            else if (rink != null)
            {
                var file = File.ReadAllBytes(rink);
                var (w, h) = Png.Size(file, rink);
                if (!CenterIce.IsRinkShape(w, h) || w < CenterIce.LeastWidth)
                    r.Warnings.Add($"{t.Code}: {Path.GetFileName(rink)} is {w}x{h}, not a top-down rink layout (5000x2236); rink skipped");
                else
                {
                    var (logo, measured) = CenterIceCut(file, rink);
                    ice = Source.Of((logo, CenterIce.LogoSize, CenterIce.LogoSize));
                    r.Lines.Add($"{t.Code}: centre ice from {Path.GetFileName(rink)} (blue lines {(measured ? "measured" : "not found, template scale")})");
                }
            }
            st.Load += loading.Elapsed.TotalSeconds;
            var items = new List<(string, Source)>();
            foreach (var logo in map.Menu.GetValueOrDefault(art) ?? new List<TeamLogo>())
                items.Add((logo.Name, MonoStyles.Contains(logo.Style) ? mono : logo.Style.Contains("lightbg") ? light : img));
            if (map.Ice.TryGetValue(art, out var iceLogo)) items.Add((iceLogo.Name, ice));
            int n = PlanMany(index, items, locations, tocs, patches, r, st);
            r.Lines.Add(placeholder ? $"placeholder (league logo): {n} logo textures on art id {art}, shared by the teams without a logo"
                                    : $"{t.Code}: {n} logo textures on art id {art}");
            log($"{t.Code}: logos planned (so far: files {st.Load:0} s, fit {st.Fit:0} s, in place {st.InPlace:0} s, copies {st.Copies:0} s, grow {st.Grow:0} s)");
        }
        foreach (var g in spec.Groups)
        {
            string league = g.LeagueLogoFile();
            int la = g == spec ? res.LeagueArt : res.LeagueArts.GetValueOrDefault(g.League.Id, -1);
            if (league == null || la < 0) continue;
            var img = Source.File(league);
            int n = PlanMany(index, index.Res.Keys.Where(k => LeagueLogo.Match(k) is { Success: true } m && int.Parse(m.Groups[2].Value) == la)
                                                 .Select(k => (k, img)).ToList(),
                             locations, tocs, patches, r, st);
            r.Lines.Add($"league {g.League.Id}: {n} logo textures on art id {la}");
        }
    }

    /// <summary>
    /// The centre-ice logo cut from a rink layout, and whether its blue lines were found. Decoding a 5000×2236 layout and
    /// cutting it takes about half a second, so the cut is kept in the plan cache by the layout's bytes (deflated: it is
    /// mostly clear ice) and only a new or changed layout is cut again.
    /// </summary>
    private static (byte[] Logo, bool Measured) CenterIceCut(byte[] file, string what)
    {
        var kept = PlanCache.Get("centerice", () =>
        {
            var pic = Png.Decode(file, what);
            var g = CenterIce.Measure(pic.Rgba, pic.W, pic.H);
            var logo = CenterIce.Logo(pic.Rgba, pic.W, pic.H, g, new CenterIceLook());
            using var ms = new MemoryStream();
            ms.WriteByte(g.Measured ? (byte)1 : (byte)0);
            using (var z = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) z.Write(logo);
            return ms.ToArray();
        }, $"{CenterIce.Version}|{CenterIce.LogoSize}", file);
        var rgba = new byte[CenterIce.LogoSize * CenterIce.LogoSize * 4];
        using (var z = new System.IO.Compression.DeflateStream(new MemoryStream(kept, 1, kept.Length - 1), System.IO.Compression.CompressionMode.Decompress))
            z.ReadExactly(rgba);
        return (rgba, kept[0] == 1);
    }

    /// <summary>The portraits' textures (slots from PackagePortraits.Assign). A slot whose chunk another portrait
    /// texture also uses is skipped by PlanOne, so no portrait in use changes.</summary>
    private static void PlanPortraits(AssetIndex index, List<PackagePortraits.Planned> plan, LocationIndex locations, TocCache tocs, List<PatchData> patches, Result r)
    {
        if (plan.Count == 0) return;
        var st = new LogoState();
        ScanOwners(index, index.Res.Keys.Where(k => k.StartsWith(PortraitSync.Prefix, StringComparison.Ordinal)), st);
        int n = PlanMany(index, plan.Select(p => (PortraitSync.TextureName(p.Slot), Source.File(p.File))).ToList(), locations, tocs, patches, r, st);
        r.Lines.Add($"portraits: {n} of {plan.Count} textures written");
    }

    /// <summary>
    /// One logo texture. In place when its stored bytes are its own; when the game stores the same bytes once for other
    /// textures too (spare art ids are often a shared placeholder), the new data goes to the end of the CAS file and only
    /// this texture's chunk is pointed at it (ChunkGrower), so the others keep the placeholder.
    /// </summary>
    /// <summary>Read only (package-uniforms): the uniforms step on an in-memory database, and the bytes it would write.</summary>
    public static Result CheckUniforms(AssetIndex index, PackageSpec spec, T3Db db)
    {
        var r = new Result();
        var uniforms = PackageUniforms.ApplySlots(db, Kits.Find(index), spec, r);
        var tocs = new TocCache(index);
        var locations = LocationIndex.Build(index, tocs);
        var patches = new List<PatchData>();
        PlanUniforms(index, uniforms, locations, tocs, patches, r);
        r.Lines.Add($"uniforms: {uniforms.Count} planned, {patches.Count} byte ranges, {patches.Where(p => p.Append).Sum(p => (long)p.New.Length) / 1048576.0:0.0} MB appended");
        return r;
    }

    /// <summary>
    /// The exported uniforms' bytes: each texture into its kit folder's texture (in place, at that texture's size), each
    /// stamp EBX in place. A texture whose stored image other textures share is left alone (it would change them too).
    /// </summary>
    private static void PlanUniforms(AssetIndex index, List<(TeamSpec Team, PackageUniform Uniform, Kit Kit)> uniforms, LocationIndex locations, TocCache tocs, List<PatchData> patches, Result r)
    {
        if (uniforms.Count == 0) return;
        // every kit texture's chunk id: a chunk two textures use can't take new data for one of them; stored bytes several
        // chunks share (identical data stored once) get a copy of their own (PlanOne)
        var st = new LogoState();
        ScanOwners(index, index.Res.Keys.Where(k => k.StartsWith(Kits.Root, StringComparison.OrdinalIgnoreCase)), st);
        // every uniform's textures encoded together (in parallel, cached: Prepare), then planned in order as before
        var items = new List<(string Name, Source Src)>();
        var itemOf = new Dictionary<(int Uniform, int Texture), int>();
        for (int ui = 0; ui < uniforms.Count; ui++)
        {
            var (_, u, kit) = uniforms[ui];
            int ti = 0;
            foreach (var (slot, file) in u.Textures)
            {
                string path = Path.Combine(u.Dir, file);
                if (kit.Slot(slot) is { } kt && File.Exists(path)) { itemOf[(ui, ti)] = items.Count; items.Add((kt.Name, Source.File(path))); }
                ti++;
            }
        }
        var prepared = Prepare(index, items, st);
        for (int ui = 0; ui < uniforms.Count; ui++)
        {
            var (t, u, kit) = uniforms[ui];
            int textures = 0, stamps = 0, ti = -1;
            var skipped = new List<string>();
            foreach (var (slot, file) in u.Textures)
            {
                ti++;
                string path = Path.Combine(u.Dir, file);
                if (kit.Slot(slot) is not { } kt) { skipped.Add($"{slot} (the folder has no such texture)"); continue; }
                if (!File.Exists(path)) { skipped.Add($"{slot} ({file} missing)"); continue; }
                int warned = r.Warnings.Count;
                int k = itemOf[(ui, ti)];
                if (PlanOne(index, kt.Name, prepared[k] == null ? items[k].Src.Image : default, locations, tocs, patches, r, st, prepared[k])) textures++;
                else
                {
                    // PlanOne's own warning, under this uniform's name
                    skipped.Add($"{slot} ({(r.Warnings.Count > warned ? r.Warnings[^1] : "not planned")})");
                    if (r.Warnings.Count > warned) r.Warnings.RemoveAt(r.Warnings.Count - 1);
                }
            }
            foreach (var (garment, file) in u.Stamps)
            {
                string path = Path.Combine(u.Dir, file);
                if (!kit.Stamps.TryGetValue(garment, out var stampName) || !File.Exists(path)) { skipped.Add($"{garment} stamp"); continue; }
                try
                {
                    var plan = ResPatcher.PlanEbx(index, stampName, File.ReadAllBytes(path), locations, tocs);
                    patches.AddRange(plan.Patches);
                    stamps++;
                }
                catch (Exception ex) { skipped.Add($"{garment} stamp ({ex.Message})"); }
            }
            r.Lines.Add($"{t.Code} {u.Name}: {textures} textures, {stamps} stamps into {kit}");
            if (skipped.Count > 0) r.Warnings.Add($"uniforms: {t.Code} {u.Name}: left out {string.Join("; ", skipped)}");
        }
    }

    /// <summary>
    /// An image textures are drawn from: a digest of what decides its pixels (a file's bytes, or how it was made from
    /// another source), and the pixels, decoded only when needed. A texture is kept in the plan cache by its source's
    /// digest and its layout (PlanMany), so a reinstall whose logos haven't changed reads and hashes each file but decodes
    /// and resizes nothing.
    /// </summary>
    private sealed class Source
    {
        private readonly Lazy<(byte[] Rgba, int W, int H)> _image;
        public byte[] Key { get; }
        public (byte[] Rgba, int W, int H) Image => _image.Value;

        private Source(byte[] key, Func<(byte[] Rgba, int W, int H)> make)
        {
            Key = key;
            _image = new Lazy<(byte[] Rgba, int W, int H)>(make, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public static Source File(string path)
        {
            var bytes = System.IO.File.ReadAllBytes(path);
            return new Source(Digest("png", bytes), () => Png.Decode(bytes, path));
        }

        public static Source Of((byte[] Rgba, int W, int H) img) => new(Digest($"rgba {img.W}x{img.H}", img.Rgba), () => img);

        public static Source Derived(string how, Source from, Func<(byte[] Rgba, int W, int H), (byte[] Rgba, int W, int H)> make) =>
            new(Digest(how, from.Key), () => make(from.Image));

        private static byte[] Digest(string kind, byte[] data)
        {
            using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            h.AppendData(System.Text.Encoding.UTF8.GetBytes(kind + "|"));
            h.AppendData(data);
            return h.GetHashAndReset();
        }
    }

    /// <summary>
    /// Which textures use each chunk id, from every named texture's header (a chunk two textures use can't take new data
    /// for one of them: PlanOne). The headers are read in parallel (each read opens its own files) and recorded in the
    /// names' order; a name that isn't a texture we read is left out.
    /// </summary>
    private static void ScanOwners(AssetIndex index, IEnumerable<string> names, LogoState st)
    {
        var list = names.ToList();
        var ids = new Guid?[list.Count];
        Parallel.For(0, list.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            try { ids[i] = TextureHeader.Parse(index.Read(index.GetRes(list[i])), ps4: !index.Layout.IsPs5).ChunkId; }
            catch { /* not a texture header we read */ }
        });
        for (int i = 0; i < list.Count; i++)
        {
            if (ids[i] is not { } id) continue;
            if (!st.ChunkOwners.TryGetValue(id, out var l)) st.ChunkOwners[id] = l = new List<string>();
            l.Add(list[i]);
        }
    }

    /// <summary>A texture's header and its encoded chunk, made ahead of PlanOne.</summary>
    private sealed record Prepared(TextureHeader Header, byte[] Raw);

    /// <summary>
    /// PlanOne for many textures, in order. What doesn't depend on the plan so far (decoding the image, fitting it to the
    /// texture's size, encoding it) is done for all of them in parallel first, and kept in the plan cache by the image's
    /// source and the texture's layout ("texfit"; bump the version in its key when Png.Fit changes); the planning itself
    /// (in place, copies, growing the CAS files) then runs one by one as before, so the patches and warnings come out the
    /// same. A texture whose preparation failed is planned the old way and fails with the same warning.
    /// </summary>
    private static int PlanMany(AssetIndex index, IReadOnlyList<(string Name, Source Src)> items,
                                LocationIndex locations, TocCache tocs, List<PatchData> patches, Result r, LogoState st)
    {
        var prepared = Prepare(index, items, st);
        int n = 0;
        for (int i = 0; i < items.Count; i++)
            if (PlanOne(index, items[i].Name, prepared[i] == null ? items[i].Src.Image : default, locations, tocs, patches, r, st, prepared[i])) n++;
        return n;
    }

    /// <summary>PlanMany's first half: each texture's header (in order) and encoded chunk (in parallel; null where that
    /// failed).</summary>
    private static Prepared[] Prepare(AssetIndex index, IReadOnlyList<(string Name, Source Src)> items, LogoState st)
    {
        var fitting = System.Diagnostics.Stopwatch.StartNew();
        var headers = new TextureHeader[items.Count];
        for (int i = 0; i < items.Count; i++)
            try { headers[i] = TextureHeader.Parse(index.Read(index.GetRes(items[i].Name)), ps4: !index.Layout.IsPs5); }
            catch { /* PlanOne reports it */ }
        var prepared = new Prepared[items.Count];
        Parallel.For(0, items.Count, i =>
        {
            if (headers[i] is not { } h) return;
            var src = items[i].Src;
            try
            {
                var raw = PlanCache.Get("texfit", () =>
                {
                    var img = src.Image;
                    return TextureBuilder.BuildChunk(h, Png.Fit(img.Rgba, img.W, img.H, h.Width, h.Height, TextureBuilder.IsSrgb(h)));
                }, "fit1|" + TextureBuilder.LayoutKey(h), src.Key);
                prepared[i] = new Prepared(h, raw);
            }
            catch { /* PlanOne reports it */ }
        });
        st.Fit += fitting.Elapsed.TotalSeconds;
        return prepared;
    }

    private static bool PlanOne(AssetIndex index, string name, (byte[] Rgba, int W, int H) img, LocationIndex locations, TocCache tocs, List<PatchData> patches, Result r, LogoState st,
                                Prepared prepared = null)
    {
        try
        {
            var fitting = System.Diagnostics.Stopwatch.StartNew();
            var h = prepared?.Header ?? TextureHeader.Parse(index.Read(index.GetRes(name)), ps4: !index.Layout.IsPs5);
            // prepared: already encoded, the image isn't needed
            var rgba = prepared != null ? null : Png.Fit(img.Rgba, img.W, img.H, h.Width, h.Height, TextureBuilder.IsSrgb(h));
            st.Fit += fitting.Elapsed.TotalSeconds;
            if (st.ChunkOwners.TryGetValue(h.ChunkId, out var owners) && owners.Count > 1)
            {
                r.Warnings.Add($"{name}: skipped, its chunk is also the texture of {string.Join(", ", owners.Where(o => o != name).Take(3))}");
                return false;
            }
            var notes = new List<string>();
            TextureReplacementPlan plan = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { plan = Replacements.PlanTexture(index, name, rgba, locations, tocs, notes.Add, built: prepared?.Raw, preview: false); }
            catch (Exception ex) when (ex.Message.Contains("stored once") || ex.Message.Contains("can't be replaced in place")) { plan = null; }
            st.InPlace += sw.Elapsed.TotalSeconds; sw.Restart();
            if (plan != null && plan.Chunk.Patches.Count > 0)
            {
                patches.AddRange(plan.Chunk.Patches);
                if (plan.Res != null) patches.AddRange(plan.Res.Patches);
                return true;
            }
            // shared bytes: give this chunk its own copy at the end of its CAS file, framed like the original (the
            // header's mip offsets point into that framing)
            byte[] raw = prepared?.Raw ?? TextureBuilder.BuildChunk(h, rgba);
            var copies = ChunkCopies.Find(index, h.ChunkId, tocs);
            var full = copies.Where(c => c.Live).OrderByDescending(c => c.Ref.Size).FirstOrDefault() ?? copies.OrderByDescending(c => c.Ref.Size).First();
            byte[] stored = Framing.Reframe(index.ReadStored(full.Ref), raw);
            st.Copies += sw.Elapsed.TotalSeconds; sw.Restart();
            var grow = ChunkGrower.Plan(index, h.ChunkId, stored, locations, tocs, st.Ends);
            patches.AddRange(grow.Patches);
            st.Grow += sw.Elapsed.TotalSeconds;
            return true;
        }
        catch (Exception ex)
        {
            r.Warnings.Add($"{name}: {ex.Message}");
            return false;
        }
    }

    // ---------------------------------------------------------------- code mods

    /// <summary>The package's static code mods (its codemods.json) plus the NCAA rules built for the installed team block.</summary>
    private static JsonObject BuildCodeMods(string game, PackageSpec spec, Result r)
    {
        JsonObject doc;
        string src = spec.PathOf(spec.CodeMods?.File);
        if (src != null && File.Exists(src)) doc = JsonNode.Parse(File.ReadAllText(src)).AsObject();
        else
        {
            doc = new JsonObject { ["name"] = "HMS packages: code mods", ["id"] = "PPSA34063", ["version"] = "01.003.001", ["process"] = "eboot.bin", ["mods"] = new JsonArray() };
            r.Warnings.Add("the package has no codemods.json: only the NCAA rules mod is installed");
        }
        var mods = doc["mods"]?.AsArray() ?? new JsonArray();
        foreach (var m in mods.Where(m => (m?["name"]?.ToString() ?? "").StartsWith("NCAA franchise rules") || (m?["name"]?.ToString() ?? "").StartsWith("Generated US prospects")).ToList())
            mods.Remove(m);
        int first = spec.Teams.Min(t => t.Index), last = spec.Teams.Max(t => t.Index);
        int keep = spec.Rules?.SeasonKeep ?? 9, seasons = spec.Rules?.NcaaSeasons ?? 4, entryAge = spec.Rules?.NcaaEntryAge ?? 19;
        var commits = r.Commits.OrderBy(c => c.Offset).ThenBy(c => c.Team).ThenBy(c => c.ArtId).ToList();
        var entries = r.Entries.OrderBy(e => e.ArtId).ToList();
        // the stats table's TEAM cell: each team index of the block's abbreviation (a gap stays the game's)
        var abbrs = Enumerable.Range(first, last - first + 1).Select(i => spec.Teams.FirstOrDefault(t => t.Index == i) is { } t ? (t.Abbr ?? t.Code) : null).ToList();
        mods.Add(NcaaCodeMods.Build(Path.Combine(game, "eboot.bin"), first, last, keep, seasons, entryAge, commits, entries, abbrs, r.NameCommits,
            Enumerable.Range(first, last - first + 1).Select(i => spec.Teams.FirstOrDefault(t => t.Index == i)?.School).ToList()));
        doc["mods"] = mods;
        r.Lines.Add($"code mods: {mods.Count} (NCAA rules for teams {first}-{last}, {keep}/16 US game days, {seasons} NCAA seasons, entry at birth + {entryAge}, {commits.Count} commits, {r.NameCommits.Count} commits by name, {entries.Count} player classes)");
        return doc;
    }

    /// <summary>
    /// Points the emulator's --game-patch at <paramref name="patchFile"/> in &lt;emulator&gt;/cfgGlobal.json (a backup is
    /// written first). Returns what changed, or null when it already points there.
    /// </summary>
    public static string SetEmulatorPatch(string emulatorDir, string patchFile)
    {
        string cfg = Path.Combine(emulatorDir, "cfgGlobal.json");
        if (!File.Exists(cfg)) throw new FileNotFoundException($"No cfgGlobal.json in {emulatorDir}.");
        var doc = JsonNode.Parse(File.ReadAllText(cfg)).AsObject();
        var emu = doc["emu"]?.AsObject() ?? throw new InvalidDataException("cfgGlobal.json has no \"emu\".");
        string args = emu["extra_args"]?.ToString() ?? "";
        string want = $"--game-patch \"{patchFile}\"";
        string stripped = Regex.Replace(args, @"\s*--game-patch\s+(""[^""]*""|\S+)", "").Trim();
        string updated = (stripped + " " + want).Trim();
        if (updated == args) return null;
        File.Copy(cfg, cfg + $".backup-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: false);
        emu["extra_args"] = updated;
        File.WriteAllText(cfg, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return $"extra_args: \"{args}\" -> \"{updated}\"";
    }

    // ---------------------------------------------------------------- report

    private static string Report(PackageSpec spec, Resolved res, Result r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {spec.Title} {spec.Version}: install report ({DateTime.Now:yyyy-MM-dd HH:mm})");
        sb.AppendLine();
        sb.AppendLine("| Team | Index | Logo art | Name set |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var t in spec.AllTeams)
            sb.AppendLine($"| {t.Name} ({t.Code}) | {t.Index} | {res.LogoArt.GetValueOrDefault(t.Code, -1)} | {res.NameSets.GetValueOrDefault(t.Code)} |");
        sb.AppendLine();
        sb.AppendLine($"League {spec.League.Id}: logo art {res.LeagueArt}; NCAA group id {res.Group}.");
        sb.AppendLine();
        foreach (var l in r.Lines) sb.AppendLine("- " + l);
        if (r.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Warnings");
            foreach (var w in r.Warnings) sb.AppendLine("- " + w);
        }
        return sb.ToString();
    }
}
