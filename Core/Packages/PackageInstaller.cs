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
        /// <summary>Current-season roster-file players placed by the players step: (team code, name slug) → game id.</summary>
        public Dictionary<(string Code, string Slug), int> Placed { get; } = new();
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

        // 1. back to the game without the package
        var existing = store.Find(Kind, spec.Id);
        if (existing != null && !dry && store.State(existing, game) is ModState.Applied or ModState.Partial)
        {
            log($"Taking down the installed {spec.Id} first");
            store.Restore(existing, game, log);
            index = reopen?.Invoke() ?? throw new InvalidOperationException(
                $"{spec.Id} is installed: reinstalling needs a way to re-read the asset index after taking it down.");
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
        PackageCheck.Recycle(db, spec, r.Lines);
        Resolve(db, loc, spec, check, resolved, r);

        // 3. the recipe, in memory
        var nameSets = PackageCheck.NameSets(loc);
        foreach (var g in spec.Groups) ApplyLeague(db, loc, g, g == spec ? resolved.LeagueArt : resolved.LeagueArts.GetValueOrDefault(g.League.Id, -1), r);
        ApplyTeams(db, loc, spec, resolved, nameSets, r);
        foreach (var g in spec.Groups) PackagePlayers.Apply(db, g, r);
        var portraits = PackagePortraits.Assign(index, legacy, db, spec, r);
        PackagePlayers.EntryYears(db, r);
        foreach (var (id, text) in spec.Strings ?? new()) SetString(loc, id, text, r);

        // 4. bytes
        var tocs = new TocCache(index);
        var locations = LocationIndex.Build(index, tocs);
        var patches = new List<PatchData>();
        foreach (var (name, d) in new[] { (PackageCheck.GameDb, db), (PackageCheck.LocDb, loc) })
        {
            var f = legacy.Find(name);
            var raw = legacy.ReadChunk(f.ChunkId);
            d.Data.CopyTo(raw, f.Offset);
            var plan = ChunkPatcher.Plan(index, f.ChunkId, raw, locations, tocs, log);
            patches.AddRange(plan.Patches);
            r.Lines.Add($"{name}: {plan.Patches.Count} byte ranges");
        }
        PlanLogos(index, spec, resolved, locations, tocs, patches, r, log);
        PlanPortraits(index, portraits, locations, tocs, patches, r);

        // 5. code mods (+ the standalone ones kept next to the patch file, which no package owns)
        var codeMods = BuildCodeMods(game, spec, r);
        var used = codeMods["mods"]!.AsArray().SelectMany(m => m!["memory"]!.AsArray()).Select(e => e!["offset"]!.ToString()).ToHashSet();
        foreach (var m in StandaloneMods(store, r.Lines))
        {
            var clash = m["memory"]!.AsArray().Select(e => e!["offset"]!.ToString()).Where(used.Contains).ToList();
            if (clash.Count > 0) { r.Warnings.Add($"standalone mod \"{m["name"]}\" left out: it patches {string.Join(", ", clash)}, which the package patches too"); continue; }
            codeMods["mods"]!.AsArray().Add(m);
        }

        r.Report = Report(spec, resolved, r);
        if (dry)
        {
            r.Lines.Add($"dry run: {patches.Count} byte ranges planned, nothing written");
            return r;
        }

        // 6. write: one entry, applied; code mods file; resolved choices; report
        string summary = $"{spec.Title} {spec.Version}: " + string.Join("; ", spec.Groups.Select(g => $"league {g.League?.Id}, {g.Teams.Count} teams ({string.Join(", ", g.Teams.Select(t => t.Code))})")) + ", logos, strings";
        r.Entry = store.Add(Kind, spec.Id, spec.Folder, summary, patches, r.Warnings);
        store.Apply(r.Entry, game, log);
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
        if (store.State(e, game) is ModState.Applied or ModState.Partial) store.Restore(e, game, log);
        store.Remove(e);
        lines.Add($"{id}: restored and removed from the mod list (its resolved choices are kept for a reinstall)");
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
    }

    private static void PlanLogos(AssetIndex index, PackageSpec spec, Resolved res, LocationIndex locations, TocCache tocs, List<PatchData> patches, Result r, Action<string> log)
    {
        var map = TeamLogos.All(index);
        var st = new LogoState();
        // every menu/league logo texture's chunk id: a chunk shared by two textures can't be given new data for one of them
        foreach (string name in index.Res.Keys.Where(k => k.StartsWith("ui/textures/common/teamlogos/", StringComparison.OrdinalIgnoreCase)
                                                        || k.StartsWith("ui/textures/common/leaguelogos/", StringComparison.OrdinalIgnoreCase)
                                                        || k.StartsWith("content/worlds/rink/textures/centerlogo/", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            try
            {
                var h = TextureHeader.Parse(index.Read(index.GetRes(name)), ps4: !index.Layout.IsPs5);
                if (!st.ChunkOwners.TryGetValue(h.ChunkId, out var l)) st.ChunkOwners[h.ChunkId] = l = new List<string>();
                l.Add(name);
            }
            catch { /* not a texture header we read */ }
        }
        var drawn = new HashSet<int>();
        foreach (var t in spec.AllTeams)
        {
            string main = spec.TeamLogoFile(t);
            bool placeholder = main == null;
            main ??= spec.LeagueLogoFile();                              // no logo yet: the shared placeholder, the league logo
            if (main == null || !res.LogoArt.TryGetValue(t.Code, out int art)) { r.Warnings.Add($"{t.Code}: no logo file or art id, logos left as they are"); continue; }
            if (!drawn.Add(art)) continue;                               // the placeholder id is drawn once
            string dir = placeholder ? Path.GetDirectoryName(main) : spec.TeamLogoFolder(t);
            var img = Png.Load(main);
            var light = File.Exists(Path.Combine(dir, "logo_lightbg.png")) ? Png.Load(Path.Combine(dir, "logo_lightbg.png")) : img;
            var mono = File.Exists(Path.Combine(dir, "mono.png")) ? Png.Load(Path.Combine(dir, "mono.png")) : (Png.Silhouette(img.Rgba), img.W, img.H);
            var ice = File.Exists(Path.Combine(dir, "ice.png")) ? Png.Load(Path.Combine(dir, "ice.png")) : img;
            int n = 0;
            foreach (var logo in map.Menu.GetValueOrDefault(art) ?? new List<TeamLogo>())
            {
                var src = MonoStyles.Contains(logo.Style) ? mono : logo.Style.Contains("lightbg") ? light : img;
                if (PlanOne(index, logo.Name, src, locations, tocs, patches, r, st)) n++;
            }
            if (map.Ice.TryGetValue(art, out var iceLogo) && PlanOne(index, iceLogo.Name, ice, locations, tocs, patches, r, st)) n++;
            r.Lines.Add(placeholder ? $"placeholder (league logo): {n} logo textures on art id {art}, shared by the teams without a logo"
                                    : $"{t.Code}: {n} logo textures on art id {art}");
        }
        foreach (var g in spec.Groups)
        {
            string league = g.LeagueLogoFile();
            int la = g == spec ? res.LeagueArt : res.LeagueArts.GetValueOrDefault(g.League.Id, -1);
            if (league == null || la < 0) continue;
            var img = Png.Load(league);
            int n = 0;
            foreach (string name in index.Res.Keys.Where(k => LeagueLogo.Match(k) is { Success: true } m && int.Parse(m.Groups[2].Value) == la).ToList())
                if (PlanOne(index, name, img, locations, tocs, patches, r, st)) n++;
            r.Lines.Add($"league {g.League.Id}: {n} logo textures on art id {la}");
        }
    }

    /// <summary>The portraits' textures (slots from PackagePortraits.Assign). A slot whose chunk another portrait
    /// texture also uses is skipped by PlanOne, so no portrait in use changes.</summary>
    private static void PlanPortraits(AssetIndex index, List<PackagePortraits.Planned> plan, LocationIndex locations, TocCache tocs, List<PatchData> patches, Result r)
    {
        if (plan.Count == 0) return;
        var st = new LogoState();
        foreach (string name in index.Res.Keys.Where(k => k.StartsWith(PortraitSync.Prefix, StringComparison.Ordinal)).ToList())
        {
            try
            {
                var h = TextureHeader.Parse(index.Read(index.GetRes(name)), ps4: !index.Layout.IsPs5);
                if (!st.ChunkOwners.TryGetValue(h.ChunkId, out var l)) st.ChunkOwners[h.ChunkId] = l = new List<string>();
                l.Add(name);
            }
            catch { /* not a texture header we read */ }
        }
        int n = 0;
        foreach (var p in plan)
            if (PlanOne(index, PortraitSync.TextureName(p.Slot), Png.Load(p.File), locations, tocs, patches, r, st)) n++;
        r.Lines.Add($"portraits: {n} of {plan.Count} textures written");
    }

    /// <summary>
    /// One logo texture. In place when its stored bytes are its own; when the game stores the same bytes once for other
    /// textures too (spare art ids are often a shared placeholder), the new data goes to the end of the CAS file and only
    /// this texture's chunk is pointed at it (ChunkGrower), so the others keep the placeholder.
    /// </summary>
    private static bool PlanOne(AssetIndex index, string name, (byte[] Rgba, int W, int H) img, LocationIndex locations, TocCache tocs, List<PatchData> patches, Result r, LogoState st)
    {
        try
        {
            var res = index.GetRes(name);
            var h = TextureHeader.Parse(index.Read(res), ps4: !index.Layout.IsPs5);
            var rgba = Png.Fit(img.Rgba, img.W, img.H, h.Width, h.Height, TextureBuilder.IsSrgb(h));
            if (st.ChunkOwners.TryGetValue(h.ChunkId, out var owners) && owners.Count > 1)
            {
                r.Warnings.Add($"{name}: skipped, its chunk is also the texture of {string.Join(", ", owners.Where(o => o != name).Take(3))}");
                return false;
            }
            var notes = new List<string>();
            TextureReplacementPlan plan = null;
            try { plan = Replacements.PlanTexture(index, name, rgba, locations, tocs, notes.Add); }
            catch (Exception ex) when (ex.Message.Contains("stored once") || ex.Message.Contains("can't be replaced in place")) { plan = null; }
            if (plan != null && plan.Chunk.Patches.Count > 0)
            {
                patches.AddRange(plan.Chunk.Patches);
                if (plan.Res != null) patches.AddRange(plan.Res.Patches);
                return true;
            }
            // shared bytes: give this chunk its own copy at the end of its CAS file, framed like the original (the
            // header's mip offsets point into that framing)
            byte[] raw = TextureBuilder.BuildChunk(h, rgba);
            var copies = ChunkCopies.Find(index, h.ChunkId, tocs);
            var full = copies.Where(c => c.Live).OrderByDescending(c => c.Ref.Size).FirstOrDefault() ?? copies.OrderByDescending(c => c.Ref.Size).First();
            byte[] stored = Framing.Reframe(index.ReadStored(full.Ref), raw);
            var grow = ChunkGrower.Plan(index, h.ChunkId, stored, locations, tocs, st.Ends);
            patches.AddRange(grow.Patches);
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
