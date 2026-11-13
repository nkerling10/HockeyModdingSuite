using System.Globalization;
using System.Text.Json;

namespace NHL27Kit.Core.Packages;

/// <summary>
/// The package's players (recipe "players"): puts each school's current roster on its team.
///
/// The roster file (e.g. data/ncaa-rosters.json) is { "&lt;team full name&gt;": { "&lt;season&gt;": { "First Last": PLAYER } } },
/// PLAYER being either "MM-DD-YYYY" or { "birthdate": "MM-DD-YYYY", "position": "C|LW|RW|F|D|LD|RD|G", "shoots": "L|R",
/// "height": inches, "weight": lb, "hometown": "City, ST" (US state / Canadian province code, or a country name),
/// "jersey_number": n, "class": "Fr|So|Jr|Sr|Gr" }. The <see cref="PlayersSpec.Season"/> lists are the current rosters; later seasons are commits.
/// Players already in the game take the file's number, height and weight; the rest of their data stays the game's.
///
/// Current roster players:
/// - already in the game (matched by name + birth date, PlayerMatcher): moved to the school's roster like any roster move
///   (RosterSync.Apply), NHL rights and contract left alone;
/// - not in the game: created (PlayerCreator) when the details file gives at least a position, else listed as needing
///   details;
/// - then each gets the NCAA bio the franchise import understands (franchise-patch-research J3): team -1, juniorteam =
///   school, juniorleague = the package's league. His roster row stays on the school, so he plays for it in exhibition.
/// - skaters get rules.faceProtector (exhibitionskaterequipment.faceprotectortype: 7 cage, 9 bubble).
///
/// Commits (later seasons) already in the game move to their school in the summer of their commit year (code mod part 8).
///
/// NCAA classes: a current-roster player's "class" (Fr, So, Jr, Sr, Gr) goes to the code mod's class table (part 10, by
/// art id) as an offset (0 Fr .. 4 Gr); the game turns it into his entry year against the franchise's own season, so
/// the roster file's season year isn't built in. Players without one are assumed to have started college at birth year
/// + rules.ncaaEntryAge.
/// </summary>
public static class PackagePlayers
{
    public sealed class Details
    {
        public string Pos { get; set; }
        /// <summary>Secondary position (the file's position list, second entry), or null.</summary>
        public string Pos2 { get; set; }
        public string Shoots { get; set; }
        public int? Height { get; set; }
        public int? Weight { get; set; }
        public int? Number { get; set; }
        public string City { get; set; }
        public string Region { get; set; }
        public string Country { get; set; }
    }

    private sealed record Listed(TeamSpec Team, string Season, string Name, string First, string Last, DateTime Born, Details Info)
    {
        /// <summary>Seasons he has been in college before the roster season (0 Fr .. 4 Gr), from "class"; or null.</summary>
        public int? Entry { get; set; }
    }

    private static readonly string[] Classes = { "FR", "SO", "JR", "SR", "GR" };

    /// <summary>"Fr" / "Freshman" → 0, "So" 1, "Jr" 2, "Sr" 3, "Gr" / "Graduate" 4; null otherwise.</summary>
    public static int? ClassOffset(string c)
    {
        string t = (c ?? "").Trim().TrimEnd('.').ToUpperInvariant();
        if (t.Length < 2) return null;
        string k = t switch { "GRADUATE" or "GRAD" or "5TH" => "GR", var x => x[..2] };
        int i = Array.IndexOf(Classes, k);
        return i < 0 ? null : i;
    }

    private static readonly HashSet<string> UsStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA","HI","ID","IL","IN","IA","KS","KY","LA","ME","MD","MA","MI","MN","MS",
        "MO","MT","NE","NV","NH","NJ","NM","NY","NC","ND","OH","OK","OR","PA","RI","SC","SD","TN","TX","UT","VT","VA","WA","WV",
        "WI","WY","DC",
    };
    private static readonly HashSet<string> Provinces = new(StringComparer.OrdinalIgnoreCase) { "AB","BC","MB","NB","NL","NS","NT","NU","ON","PE","QC","SK","YT" };
    private static readonly Dictionary<string, string> Countries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USA"] = "USA", ["Canada"] = "CAN", ["Sweden"] = "SWE", ["Finland"] = "FIN", ["Czechia"] = "CZE", ["Czech Republic"] = "CZE",
        ["Slovakia"] = "SVK", ["Germany"] = "DEU", ["Austria"] = "AUT", ["Switzerland"] = "CHE", ["SUI"] = "CHE", ["Latvia"] = "LVA",
        ["Kazakhstan"] = "KAZ", ["Russia"] = "RUS", ["Belarus"] = "BLR", ["Denmark"] = "DNK", ["Norway"] = "NOR", ["Japan"] = "JPN",
        ["France"] = "FRA", ["Slovenia"] = "SVN", ["Ukraine"] = "UKR", ["Lithuania"] = "LTU", ["Estonia"] = "EST", ["Poland"] = "POL",
        ["Hungary"] = "HUN", ["Italy"] = "ITA", ["Netherlands"] = "NLD", ["Great Britain"] = "GBR", ["United Kingdom"] = "GBR", ["Korea"] = "KOR",
    };

    /// <summary>"Toronto, ON" → Toronto / ON / CAN; "Edina, MN" → USA; "Riga, Latvia" → LVA.</summary>
    private static (string City, string Region, string Country) Hometown(string h)
    {
        if (string.IsNullOrWhiteSpace(h)) return ("", "", "");
        int c = h.LastIndexOf(',');
        if (c < 0) return (h.Trim(), "", "");
        string city = h[..c].Trim(), tail = h[(c + 1)..].Trim().TrimEnd('.');
        if (UsStates.Contains(tail)) return (city, tail.ToUpperInvariant(), "USA");
        if (Provinces.Contains(tail)) return (city, tail.ToUpperInvariant(), "CAN");
        return (city, "", Countries.TryGetValue(tail, out var code) ? code : tail);
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    /// <summary>"C" or ["C", "RW"] (primary, then secondary); null when missing.</summary>
    private static List<string> Positions(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString().Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString().Trim()).Where(s => s.Length > 0).ToList();
        return null;
    }

    // game positionprimary: 0 C, 1 LW, 2 RW, 3 LD, 4 RD, 5 G
    private const int Forward = -2;
    /// <summary>A position code, or Forward for "F" (decided by the team's needs), -1 when unknown.</summary>
    private static int Code(string pos, string shoots, int notThis = -1)
    {
        bool right = (shoots ?? "L").Trim().StartsWith("R", StringComparison.OrdinalIgnoreCase);
        int c = (pos ?? "").Trim().ToUpperInvariant() switch
        {
            "C" => 0, "LW" => 1, "RW" => 2, "LD" => 3, "RD" => 4, "G" => 5,
            "D" => right ? 4 : 3,
            "W" => right ? 2 : 1,
            "F" => Forward,
            _ => -1,
        };
        if (c == notThis && pos?.Trim().ToUpperInvariant() is "W" or "D") c = c switch { 1 => 2, 2 => 1, 3 => 4, 4 => 3, _ => c };
        return c;
    }
    private static int Group(int code) => code switch { 0 or 1 or 2 => 0, 3 or 4 => 1, 5 => 2, _ => -1 };

    private static int? Num(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };

    public static void Apply(T3Db db, PackageSpec spec, PackageInstaller.Result r)
    {
        var ps = spec.Players;
        if (ps == null || string.IsNullOrEmpty(ps.File)) return;
        string file = spec.PathOf(ps.File);
        if (!File.Exists(file)) throw new FileNotFoundException($"The players file {file} isn't there.");
        var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, JsonElement>>>>(File.ReadAllText(file), Json)
                  ?? throw new InvalidDataException($"{file} is empty.");
        var details = new Dictionary<string, Details>(StringComparer.OrdinalIgnoreCase);
        if (spec.PathOf(ps.Details) is { } df && File.Exists(df))
            foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, Details>>(File.ReadAllText(df), Json) ?? new())
                details[k] = v;

        // the list, by team
        var listed = new List<Listed>();
        foreach (var (teamName, seasons) in all)
        {
            var team = spec.Teams.FirstOrDefault(t => string.Equals(t.Name, teamName, StringComparison.OrdinalIgnoreCase)
                                                      || string.Equals(t.Code, teamName, StringComparison.OrdinalIgnoreCase));
            if (team == null) { r.Warnings.Add($"players: {teamName} isn't one of the package's teams; its players are skipped"); continue; }
            foreach (var (season, players) in seasons)
                foreach (var (name, el) in players)
                {
                    Details info = null;
                    string born = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
                    if (el.ValueKind == JsonValueKind.Object)
                    {
                        born = Str(el, "birthdate") ?? Str(el, "born");
                        var (city, region, country) = Hometown(Str(el, "hometown"));
                        var posList = Positions(el, "position") ?? Positions(el, "pos") ?? new List<string>();
                        info = new Details
                        {
                            Pos = posList.ElementAtOrDefault(0), Pos2 = posList.ElementAtOrDefault(1),
                            Shoots = Str(el, "shoots"), Height = Num(el, "height"), Weight = Num(el, "weight"),
                            Number = Num(el, "jersey_number") ?? Num(el, "number"), City = city, Region = region, Country = country,
                        };
                    }
                    if (!DateTime.TryParseExact(born, new[] { "MM-dd-yyyy", "M-d-yyyy", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var b))
                    { r.Warnings.Add($"players: {name} ({teamName}): birth date '{born}' isn't MM-DD-YYYY; skipped"); continue; }
                    var (first, last) = SplitPersonName(name);
                    if (info == null || string.IsNullOrWhiteSpace(info.Pos)) info = details.GetValueOrDefault(name) ?? info;
                    var item = new Listed(team, season, name, first, last, b, info);
                    if (el.ValueKind == JsonValueKind.Object && Str(el, "class") is { } cls)
                    {
                        item.Entry = ClassOffset(cls);
                        if (item.Entry == null) r.Warnings.Add($"players: {name} ({teamName}): class '{cls}' isn't Fr/So/Jr/Sr/Gr; ignored");
                    }
                    listed.Add(item);
                }
        }
        string current = ps.Season ?? listed.Select(l => l.Season).DefaultIfEmpty("").Min();
        var now = listed.Where(l => l.Season == current).ToList();
        var commits = listed.Where(l => l.Season != current).ToList();

        // positions: the file's primary/secondary; "F" takes the forward spot (C, LW, RW) the team has fewest of, the
        // wing on his shooting side first on a tie
        var posOf = new Dictionary<Listed, (int P, int S)>();
        int forwardsPlaced = 0;
        foreach (var g in now.GroupBy(l => l.Team.Code))
        {
            var count = new int[6];
            foreach (var l in g)
            {
                int p = Code(l.Info?.Pos, l.Info?.Shoots);
                int s = Code(l.Info?.Pos2, l.Info?.Shoots, p);
                if (s == p || (p >= 0 && Group(s) != Group(p))) s = -1;
                posOf[l] = (p, s);
                if (p >= 0) count[p]++;
            }
            foreach (var l in g.Where(l => posOf[l].P == Forward))
            {
                bool right = (l.Info?.Shoots ?? "L").Trim().StartsWith("R", StringComparison.OrdinalIgnoreCase);
                int side = right ? 2 : 1, other = right ? 1 : 2;
                int pick = new[] { side, 0, other }.OrderBy(c => count[c]).First();
                count[pick]++;
                int s = posOf[l].S;
                posOf[l] = (pick, s == pick || Group(s) != 0 ? -1 : s);
                forwardsPlaced++;
            }
        }

        // match, plan moves and creations
        var roster = new Roster(db);
        var matcher = new PlayerMatcher(db, roster);
        var rt = db.Table(Roster.RosterTable);
        var rtTeam = rt.Fields.First(f => f.Name == "team");
        var bioT0 = db.Table(Roster.BioTable);
        var bf0 = bioT0.Fields.ToDictionary(f => f.Name);
        var moves = new List<SyncChange>();
        var creates = new List<SyncChange>();
        var placed = new List<(Listed L, int GameId)>();
        var needDetails = new List<Listed>();
        int already = 0;
        foreach (var l in now)
        {
            var (p, nameOnly) = matcher.Find(l.First, l.Last, l.Born);
            if (p != null)
            {
                if (nameOnly) r.Warnings.Add($"players: {l.Name} matched {p.Name} by name only (birth dates differ)");
                int from = p.RosterRow >= 0 ? Convert.ToInt32(db.Get(rt, p.RosterRow, rtTeam)) : p.Team;
                placed.Add((l, p.GameId));
                bool there = p.RosterRow >= 0 && from == l.Team.Index;
                if (there) already++;
                int oldH = Convert.ToInt32(db.Get(bioT0, p.BioRow, bf0["height"])) + RosterSync.HeightOffset;
                int oldW = Convert.ToInt32(db.Get(bioT0, p.BioRow, bf0["weight"])) + RosterSync.WeightOffset;
                moves.Add(new SyncChange
                {
                    Kind = there ? SyncKind.Update : SyncKind.Join, GameId = p.GameId, Name = p.Name, Position = p.PositionDisplay, RosterRow = p.RosterRow, BioRow = p.BioRow,
                    PlayerIndex = p.PlayerIndex, FromTeam = from, ToTeam = l.Team.Index, FromTeamName = TeamName(roster, from),
                    ToTeamName = l.Team.Name, Selected = true,
                    OldNumber = p.Jersey, NewNumber = l.Info?.Number ?? -1,
                    WithSizes = l.Info?.Height > 0 || l.Info?.Weight > 0,
                    OldHeight = oldH, NewHeight = l.Info?.Height ?? 0, OldWeight = oldW, NewWeight = l.Info?.Weight ?? 0,
                });
                continue;
            }
            var d = l.Info;
            if (d == null || posOf[l].P < 0) { needDetails.Add(l); continue; }
            var bio = BioOf(l, d, posOf[l].P, posOf[l].S);
            var c = new SyncChange { Kind = SyncKind.Missing, Name = l.Name, Position = bio.Position, ToTeam = l.Team.Index, ToTeamName = l.Team.Name, Bio = bio, Selected = true };
            if (PlayerCreator.Problem(c) is { } problem) { r.Warnings.Add($"players: {l.Name}: {problem}"); continue; }
            creates.Add(c);
        }

        if (moves.Count > 0) r.Lines.Add("players: " + RosterSync.Apply(db, moves));
        if (creates.Count > 0)
        {
            var made = PlayerCreator.Create(db, creates);
            foreach (var cp in made.Created)
                if (now.FirstOrDefault(l => l.Name.Equals(cp.Bio.Name, StringComparison.OrdinalIgnoreCase)) is { } l) placed.Add((l, cp.GameId));
            foreach (var (n, why) in made.Skipped) r.Warnings.Add($"players: {n} not created: {why}");
            r.Lines.Add($"players: created {made.Created.Count}");
        }

        // the NCAA bio and gear
        roster = new Roster(db);
        var bioT = db.Table(Roster.BioTable);
        var bf = bioT.Fields.ToDictionary(f => f.Name);
        var eq = db.Table(PlayerCreator.SkaterEquipmentTable);
        var eqId = eq.Fields.First(f => f.Name == "game_id");
        var eqFace = eq.Fields.FirstOrDefault(f => f.Name == "faceprotectortype");
        var eqRow = Enumerable.Range(0, eq.WrittenRecords).Where(i => !db.IsInvalid(eq, i))
                              .GroupBy(i => Convert.ToInt32(db.Get(eq, i, eqId))).ToDictionary(g => g.Key, g => g.First());
        int league = spec.League?.Id ?? 32, cages = 0, repositioned = 0;
        var moved = moves.Select(m => m.GameId).ToHashSet();
        foreach (var (l, id) in placed)
        {
            var p = roster.Find(id);
            if (p == null) continue;
            // a player already in the game takes the file's positions when they stay in his group (forward, D, G), so
            // his ratings still fit
            if (moved.Contains(id) && posOf.TryGetValue(l, out var ps2) && ps2.P >= 0 && Group(ps2.P) == Group(p.PositionPrimary)
                && (ps2.P != p.PositionPrimary || ps2.S != p.PositionSecondary))
            {
                db.Set(bioT, p.BioRow, bf["positionprimary"], ps2.P);
                db.Set(bioT, p.BioRow, bf["position"], Roster.CollapsedPosition(ps2.P));
                if (bf.TryGetValue("positionsecondary", out var sf)) db.Set(bioT, p.BioRow, sf, ps2.S);
                repositioned++;
            }
            else if (moved.Contains(id) && posOf.TryGetValue(l, out var ps3) && ps3.P >= 0 && Group(ps3.P) != Group(p.PositionPrimary))
                r.Warnings.Add($"players: {l.Name}: the file says {PosNames[ps3.P]}, the game has him as {Roster.PositionDisplay(p.PositionPrimary, p.PositionSecondary)}; kept the game's (ratings are for that position)");
            db.Set(bioT, p.BioRow, bf["team"], -1);
            db.Set(bioT, p.BioRow, bf["juniorteam"], l.Team.Index);
            db.Set(bioT, p.BioRow, bf["juniorleague"], league);
            if (spec.Rules?.FaceProtector is int fp && eqFace != null && !p.IsGoalie && eqRow.TryGetValue(id, out int er))
            { db.Set(eq, er, eqFace, fp); cages++; }
        }

        // lines from the final positions
        foreach (var t in spec.Teams) RosterLines.BuildTeam(db, t.Index);

        r.Lines.Add($"players: season {current}: {now.Count} listed, {placed.Count} placed ({moves.Count - already} moved, {already} already there, " +
                    $"{placed.Count - moves.Count} created), {needDetails.Count} need details; {cages} face protectors; " +
                    $"{repositioned} existing players' positions from the file; {forwardsPlaced} \"F\" given a spot; lines rebuilt");
        // which game player each roster-file entry is (the game may spell him differently: "Nic Sima" for Nicholas)
        foreach (var (l, gid) in placed) r.Placed[(l.Team.Code, PackagePortraits.Slug(l.Name))] = gid;
        foreach (var g in placed.GroupBy(x => x.L.Team.Code))
        {
            var ps4 = g.Select(x => roster.Find(x.GameId)).Where(p => p != null).Select(p => Group(p.PositionPrimary)).ToList();
            int f = ps4.Count(x => x == 0), dd = ps4.Count(x => x == 1), gg = ps4.Count(x => x == 2);
            r.Lines.Add($"  {g.Key}: {g.Count()} players ({f} F, {dd} D, {gg} G)");
            if (f < 12 || dd < 6 || gg < 2) r.Warnings.Add($"players: {g.Key} can't dress a full lineup (needs 12 F, 6 D, 2 G)");
        }
        if (needDetails.Count > 0)
            r.Warnings.Add($"players: not in the game and no position in {ps.Details ?? "the details file"} ({needDetails.Count}): " +
                           string.Join(", ", needDetails.Select(l => $"{l.Name} ({l.Team.Code})")));
        // entry years: resolved to art ids after the portraits step (EntryYears), which gives created players their own
        foreach (var (l, id) in placed) if (l.Entry is int off) r.EntryGames[id] = off;
        int noClass = now.Count(l => l.Entry == null);
        r.Lines.Add($"players: classes: {r.EntryGames.Count}" + (noClass > 0 ? $", {noClass} without one (birth year + entry age)" : ""));

        if (commits.Count > 0)
        {
            // commits already in the game join their school in the summer of their commit year (code mod part 8, keyed
            // by the player's art id, which the franchise keeps). Exact name + birth date only; the art id must be his alone.
            var artCount = new Dictionary<int, int>();
            foreach (int row in Enumerable.Range(0, bioT0.WrittenRecords))
            {
                if (db.IsInvalid(bioT0, row)) continue;
                int a = Convert.ToInt32(db.Get(bioT0, row, bf0["artid"]));
                artCount[a] = artCount.GetValueOrDefault(a) + 1;
            }
            var skipped = new List<string>();
            foreach (var l in commits)
            {
                var (p, nameOnly) = matcher.Find(l.First, l.Last, l.Born);
                // not in the game yet: the game may generate him later (a draft-class prospect with his real name);
                // part 11 then sends him to this school by name
                if (p == null && int.TryParse(l.Season, out int ny) && int.TryParse(current, out int nc) && ny > nc && ny - nc <= 15)
                    r.NameCommits.Add((l.Name, l.Team.Index, ny - nc));
                if (p == null || nameOnly || !int.TryParse(l.Season, out int year) || !int.TryParse(current, out int cur) || year <= cur || year - cur > 15) { skipped.Add($"{l.Name} ({l.Team.Code} {l.Season}){(nameOnly ? ": birth date differs" : "")}"); continue; }
                int art = Convert.ToInt32(db.Get(bioT0, p.BioRow, bf0["artid"]));
                if (art <= 0 || art > 0xFFFF || artCount.GetValueOrDefault(art) != 1) { skipped.Add($"{l.Name}: art id {art} not his alone"); continue; }
                r.Commits.Add((art, l.Team.Index, year - cur));        // seasons after the roster season: the game adds the franchise's year
            }
            r.Lines.Add($"players: commits ({commits.Count}): " +
                        string.Join("; ", commits.GroupBy(c => (c.Team.Code, c.Season)).Select(g => $"{g.Key.Code} {g.Key.Season}: {g.Count()}")) +
                        $"; {r.Commits.Count} in the game join their school in their commit year");
            if (skipped.Count > 0)
                r.Lines.Add($"players: commits not in the game or not matched exactly ({skipped.Count}): {string.Join(", ", skipped)}");
        }
    }

    /// <summary>After the portraits step: the classed players' class offsets by art id (part 10's table). An art id
    /// another bio shares is skipped (the table would give the year to both).</summary>
    public static void EntryYears(T3Db db, PackageInstaller.Result r)
    {
        if (r.EntryGames.Count == 0) return;
        var roster = new Roster(db);
        var bioT = db.Table(Roster.BioTable);
        var art = bioT.Fields.First(f => f.Name == "artid");
        var count = new Dictionary<int, int>();
        foreach (int row in Enumerable.Range(0, bioT.WrittenRecords))
            if (!db.IsInvalid(bioT, row)) { int a = Convert.ToInt32(db.Get(bioT, row, art)); count[a] = count.GetValueOrDefault(a) + 1; }
        var skipped = new List<string>();
        foreach (var (id, off) in r.EntryGames)
        {
            var p = roster.Find(id);
            int a = p == null ? 0 : Convert.ToInt32(db.Get(bioT, p.BioRow, art));
            if (a <= 0 || a > 0xFFFF || count.GetValueOrDefault(a) != 1) { skipped.Add(p?.Name ?? $"game id {id}"); continue; }
            r.Entries.Add((a, off));
        }
        r.Lines.Add($"players: classes stored for {r.Entries.Count} players");
        if (skipped.Count > 0) r.Warnings.Add($"players: class not stored (art id shared with another player; no portrait gave him his own): {string.Join(", ", skipped)}");
    }

    private static string TeamName(Roster roster, int team) => roster.Teams.TryGetValue(team, out var t) ? t.Name : $"team {team}";

    /// <summary>"Dakoda Rhéaume-Mullen" → Dakoda / Rhéaume-Mullen; "J.P. Hurlbert" → J.P. / Hurlbert.</summary>
    public static (string First, string Last) SplitPersonName(string name)
    {
        name = name.Trim();
        int sp = name.IndexOf(' ');
        return sp < 0 ? (name, name) : (name[..sp], name[(sp + 1)..].Trim());
    }

    private static readonly string[] PosNames = { "C", "LW", "RW", "LD", "RD", "G" };

    private static PlayerBio BioOf(Listed l, Details d, int primary, int secondary)
    {
        string shoots = string.IsNullOrWhiteSpace(d.Shoots) ? "L" : d.Shoots.Trim().ToUpperInvariant()[..1];
        return new PlayerBio(l.First, l.Last, d.Number ?? -1, PosNames[primary], shoots, d.Height ?? 0, d.Weight ?? 0, l.Born, d.City ?? "",
                             DraftInfo.Undrafted, "package")
        { Primary = primary, Secondary = secondary, BirthCountry = d.Country ?? "", BirthRegion = d.Region ?? "" };
    }
}

/// <summary>Recipe "players": the roster file, which of its seasons is the current roster, and the details file for
/// players the game doesn't have (position, shoots, height, weight, number, city, region, country by "First Last").</summary>
public sealed class PlayersSpec
{
    public string File { get; set; }
    public string Season { get; set; }
    public string Details { get; set; }
}
