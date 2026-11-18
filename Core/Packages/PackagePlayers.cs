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

    internal sealed record Listed(TeamSpec Team, string Season, string Name, string First, string Last, DateTime Born, Details Info)
    {
        /// <summary>Seasons he has been in college before the roster season (0 Fr .. 4 Gr), from "class"; or null.</summary>
        public int? Entry { get; set; }
        /// <summary>His NHL draft, from "draft" { year, round, pick, team }; null when the file gives none.</summary>
        public DraftInfo Draft { get; set; }
        /// <summary>"bubble": true in the file: he wears a clear full shield instead of the package's cage.</summary>
        public bool Bubble { get; set; }
        /// <summary>"neck_guard" in the file (0 none, 1 turtleneck, 2 floating): his neck guard over the package's; or null.</summary>
        public int? NeckGuard { get; set; }
        /// <summary>"player_model": { skin_tone, hair_color, hair_length, hair_type, facial_hair, ethnicity }: skin tone,
        /// East Asian ethnicity (face), hair colour and facial hair applied; hair_length / hair_type checked, not applied
        /// yet (see docs/PACKAGES.md: appearance).</summary>
        public Dictionary<string, string> Model { get; set; }
        /// <summary>"captaincy": "C" or "A"; read and checked (1 C + 2 A, or 3 A per team), not applied yet.</summary>
        public string Captaincy { get; set; }
        /// <summary>"player_type": DFD, OFD, TWD, GRN, PLY, SNP, PWF, TWF (ENF); read and checked, not applied yet.</summary>
        public string PlayerType { get; set; }
    }

    /// <summary>
    /// Roster "player_type" codes → exhibitionrostertable.playerstyle (from the game's players, 2026-11-17: Pelech/Tanev 1,
    /// Makar/Fox 2, Hedman/Josi/Slavin 4, Lomberg/Kastelic 5, McDavid/Crosby 6, Ovechkin/Matthews 7, the Tkachuks/Wilson 8,
    /// Barkov/Marchand 9, Ross Johnston 10; 0 = none). Goalies use 1 (butterfly, most) or 0.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> PlayerStyles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["DFD"] = 1, ["OFD"] = 2, ["TWD"] = 4, ["GRN"] = 5, ["PLY"] = 6, ["SNP"] = 7, ["PWF"] = 8, ["TWF"] = 9, ["ENF"] = 10,
    };

    private static readonly string[] NeckGuardNames = { "none", "turtleneck", "floating" };

    /// <summary>A string-valued object field ("player_model") as a dictionary; null when missing.</summary>
    private static Dictionary<string, string> StrMap(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object) return null;
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in v.EnumerateObject()) d[x.Name] = x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() : x.Value.ToString();
        return d;
    }

    /// <summary>"draft": { "year": 2025, "round": 2, "pick": 63, "team": "NJ" } → DraftInfo; null when absent or incomplete.</summary>
    private static DraftInfo DraftOf(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("draft", out var d) || d.ValueKind != JsonValueKind.Object) return null;
        if (Num(d, "year") is not int y || y < 1900 || string.IsNullOrWhiteSpace(Str(d, "team"))) return null;
        return new DraftInfo(y, Str(d, "team").Trim().ToUpperInvariant(), Num(d, "round") ?? 0, Num(d, "pick") ?? Num(d, "overall") ?? 0);
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

    /// <summary>A yes/no field: true, "true", "yes", "1" or 1 (any case); anything else or missing is no.</summary>
    private static bool Flag(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() is "true" or "yes" or "1",
            JsonValueKind.Number => v.TryGetInt32(out int n) && n != 0,
            _ => false,
        };

    /// <summary>A roster entry's "neck_guard": "none"/"turtleneck"/"floating" (also false/true) or 0-2 → neckguardtype; null when invalid.</summary>
    private static int? NeckGuardOf(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetInt32(out int n) && n is >= 0 and <= 2 => n,
        JsonValueKind.False => 0,
        JsonValueKind.True => 1,
        JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() switch
        {
            "none" or "no" or "0" => 0, "turtleneck" or "1" => 1, "floating" or "float" or "2" => 2, _ => null,
        },
        _ => null,
    };
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

    /// <summary>
    /// The roster keys read but not applied yet (player_model, captaincy, player_type): counted and checked, so the
    /// file is right before the installer starts using them. Goalies may give "catches" in place of "shoots".
    /// </summary>
    private static HashSet<TeamSpec> CheckNewKeys(List<Listed> now, PackageInstaller.Result r)
    {
        var badTypes = now.Where(l => l.PlayerType != null && !PlayerStyles.ContainsKey(l.PlayerType)).ToList();
        foreach (var l in badTypes.Take(10)) r.Warnings.Add($"players: {l.Name} ({l.Team.Code}): player_type '{l.PlayerType}' isn't one of {string.Join("/", PlayerStyles.Keys)}; left as the game has it");
        var badTones = now.Where(l => l.Model?.GetValueOrDefault("skin_tone") is { } t && !SkinTones.ContainsKey(t)).ToList();
        foreach (var l in badTones.Take(10)) r.Warnings.Add($"players: {l.Name} ({l.Team.Code}): skin_tone '{l.Model["skin_tone"]}' isn't one of {string.Join("/", SkinTones.Keys)}; left as the game has it");
        var badHair = now.Where(l => l.Model?.GetValueOrDefault("hair_color") is { } t && !HairColors.ContainsKey(t)).ToList();
        foreach (var l in badHair.Take(10)) r.Warnings.Add($"players: {l.Name} ({l.Team.Code}): hair_color '{l.Model["hair_color"]}' isn't one of {string.Join("/", HairColors.Keys)}; left as the game has it");
        var badBeard = now.Where(l => l.Model?.GetValueOrDefault("facial_hair") is { } t && !FacialHair.ContainsKey(t)).ToList();
        foreach (var l in badBeard.Take(10)) r.Warnings.Add($"players: {l.Name} ({l.Team.Code}): facial_hair '{l.Model["facial_hair"]}' isn't one of {string.Join("/", FacialHair.Keys)}; left as the game has it");
        // captains only for a team whose letters the game accepts: 1 C + 2 A, or 3 A
        var ok = new HashSet<TeamSpec>();
        foreach (var g in now.Where(l => !string.IsNullOrEmpty(l.Captaincy)).GroupBy(l => l.Team))
        {
            int c = g.Count(l => l.Captaincy == "C"), a = g.Count(l => l.Captaincy == "A"), other = g.Count() - c - a;
            if (((c == 1 && a == 2) || (c == 0 && a == 3)) && other == 0) ok.Add(g.Key);
            else r.Warnings.Add($"players: {g.Key.Code}: captaincy is {c} C + {a} A{(other > 0 ? $" + {other} other" : "")}; the game needs 1 C + 2 A, or 3 A: no letters set for this team");
        }
        return ok;
    }

    /// <summary>
    /// Roster "player_model" skin_tone → bio skincolor (1-12, light to dark): the range a tone is given (Pick) and the
    /// range a player may already be in to be left alone (Keep). From the game's own players: 1-3 hold 7,650 of the
    /// 8,200 generic-head players, 6-7 the medium browns.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (int Lo, int Hi, int KeepLo, int KeepHi)> SkinTones =
        new Dictionary<string, (int, int, int, int)>(StringComparer.OrdinalIgnoreCase)
        {
            ["fair"] = (1, 1, 1, 1), ["light"] = (1, 2, 1, 3), ["medium"] = (3, 4, 3, 4), ["olive"] = (4, 5, 4, 5),
            ["medium_brown"] = (6, 7, 6, 7), ["brown"] = (8, 9, 8, 9), ["dark"] = (10, 12, 10, 12),
        };

    /// <summary>
    /// Roster "player_model" hair_color → bio haircolorcode (content/common/configs/mappings/player_hair_color_mapping:
    /// 0 black, 1 dark brown, 2 medium brown, 3 dark blond, 4 light brown, 5 platinum, 6 auburn, 7 white, 8 ginger, 9 ash,
    /// 10 dark ash, 11 golden blond, 12 grey, 13 taupe). The game's own players have eyebrowcolorcode and
    /// playoffbeardcolor equal to their hair code (same numbering; the eyebrow table holds darker shades of each).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> HairColors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = 0, ["dark_brown"] = 1, ["medium_brown"] = 2, ["brown"] = 2, ["light_brown"] = 4, ["blond"] = 3,
        ["dark_blond"] = 3, ["golden_blond"] = 11, ["platinum"] = 5, ["auburn"] = 6, ["red"] = 8, ["ginger"] = 8,
        ["grey"] = 12, ["gray"] = 12, ["white"] = 7,
    };

    /// <summary>
    /// Roster "player_model" facial_hair → beard styles (player_beard_style_mapping: 1 full, 2 goatee, 3 partial, 4 patchy,
    /// 5 wildman, 6 moustache, 7 lambchops, 8 jagr, 9 chincentric, 10 handlebar, 11 goatee2, 12 full2, 13 partial2,
    /// 14 groomed, 15 chincentric2, 16 moustache2) and the stage worn now (bio playoffbeardstart, 1-4, beard_*_stageN
    /// meshes); in the playoffs it grows towards playoffbeardmax. "none" is clean-shaven now (start 0), playoff beard as
    /// the game has it. Stubble is partial stage 1, the game's own most common short beard (596 of its players).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (int[] Styles, int Stage)> FacialHair =
        new Dictionary<string, (int[], int)>(StringComparer.OrdinalIgnoreCase)
        {
            ["none"] = (Array.Empty<int>(), 0), ["stubble"] = (new[] { 3, 1 }, 1), ["mustache"] = (new[] { 6 }, 2),
            ["moustache"] = (new[] { 6 }, 2), ["goatee"] = (new[] { 2, 11 }, 2), ["beard"] = (new[] { 1, 12, 14 }, 2),
        };

    /// <summary>
    /// Roster "player_model" ethnicity values that get an East Asian generic head: the heads the game's own Korean,
    /// Japanese and Chinese players mostly wear (2026-11-18, from the base roster: e.g. 100051 Jin Hwan, Kyung-Sam Tae;
    /// 100261 Haoxi Wang; 100190 Yushiroh Hirano).
    /// </summary>
    /// <summary>
    /// Glove ids by brand (content/common/configs/mappings/player_glove_mapping, names "&lt;brand&gt;_..."), so a team's
    /// sticks can follow its glove brand.
    /// </summary>
    private static readonly (string Brand, int[] Ids)[] GloveBrands =
    {
        ("bauer", new[] { 24, 28, 38, 39, 42, 43, 50, 55, 60, 61, 80, 82, 83, 94, 102, 105, 112, 114, 117, 123, 131, 136 }),
        ("ccm", new[] { 20, 29, 41, 53, 68, 69, 84, 85, 91, 101, 111, 113, 119, 126, 128, 132, 135, 138 }),
        ("warrior", new[] { 15, 44, 52, 58, 66, 86, 93, 100, 109, 118, 121, 124, 129, 134, 139 }),
        ("true", new[] { 95, 96, 104, 110, 115, 125, 127, 140 }),
        ("sherwood", new[] { 47, 62, 87, 106, 108, 116, 120, 122, 130, 133, 137 }),
    };

    /// <summary>
    /// exhibitionskaterequipment.stick by brand (player_stick_mapping): only sticks the game's own NHL skaters carry
    /// (2026-11-18 count), so every one has its model. Bauer 236 supreme_fuse, 221 vapor_flylite, 237 pulse_2; CCM 214
    /// jetspeed_ft7, 222 ft8, 212 ribcor_trigger9, 223 trigger10, 213 tacks_xf, 224 xfghost, 220 vizion; Warrior 199
    /// alpha_lx2, 230 lx3, 215 covert_qr6, 201 novium_super; TRUE 228 hzrdus_smoke, 218 hzrdus_9x4, 196 catalyst_9x3, 179
    /// catalyst_9x; Sherwood 209 code_encrypt, 225 rekker_morph, 244 code_photon.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int[]> StickPools = new Dictionary<string, int[]>
    {
        ["bauer"] = new[] { 236, 221, 237 },
        ["ccm"] = new[] { 214, 222, 212, 223, 213, 224, 220 },
        ["warrior"] = new[] { 199, 230, 215, 201 },
        ["true"] = new[] { 228, 218, 196, 179 },
        ["sherwood"] = new[] { 209, 225, 244 },
    };

    /// <summary>A stick of the brand of glove <paramref name="gloves"/>, the same for the same seed; null for an unknown glove.</summary>
    private static int? StickFor(int? gloves, uint seed)
    {
        if (gloves is not int g) return null;
        foreach (var (brand, ids) in GloveBrands)
            if (ids.Contains(g)) { var pool = StickPools[brand]; return pool[(int)(seed % (uint)pool.Length)]; }
        return null;
    }

    private static readonly HashSet<string> EastAsian =new(StringComparer.OrdinalIgnoreCase) { "asian", "east_asian", "korean", "japanese", "chinese" };
    private static readonly int[] EastAsianHeads = { 100051, 100189, 100190, 100191, 100197, 100243, 100258, 100261, 100269 };

    /// <summary>
    /// The game's generic heads (headid 100000+, headtexture = headid) by the skin colour most of the game's own players
    /// wearing them have: a head is used across neighbouring skin colours, so its usual one is the tone it was made for.
    /// Package-created players (game ids from PlayerCreator.PackageIdStart) don't count: their skin colours were random.
    /// </summary>
    private sealed class GenericFaces
    {
        private readonly Dictionary<int, int> _tone = new();
        private readonly Dictionary<int, List<int>> _byTone = new();

        public GenericFaces(T3Db db, T3Table bio, IReadOnlyDictionary<string, T3Field> bf)
        {
            var counts = new Dictionary<int, Dictionary<int, int>>();
            for (int row = 0; row < bio.WrittenRecords; row++)
            {
                if (db.IsInvalid(bio, row) || Convert.ToInt32(db.Get(bio, row, bf["game_id"])) >= PlayerCreator.PackageIdStart) continue;
                int head = Convert.ToInt32(db.Get(bio, row, bf["headid"])), skin = Convert.ToInt32(db.Get(bio, row, bf["skincolor"]));
                if (head < 100000 || skin < 1) continue;
                var c = counts.TryGetValue(head, out var x) ? x : counts[head] = new Dictionary<int, int>();
                c[skin] = c.GetValueOrDefault(skin) + 1;
            }
            foreach (var (head, c) in counts.OrderBy(x => x.Key))
            {
                int tone = c.OrderByDescending(x => x.Value).ThenBy(x => x.Key).First().Key;
                _tone[head] = tone;
                (_byTone.TryGetValue(tone, out var l) ? l : _byTone[tone] = new List<int>()).Add(head);
            }
        }

        /// <summary>The skin colour a head was made for; -1 when it isn't a known generic head.</summary>
        public int Tone(int head) => _tone.GetValueOrDefault(head, -1);

        /// <summary>A skin colour in lo..hi and a head made for it, the same for the same <paramref name="seed"/>; null when none.</summary>
        public (int Skin, int Head)? Pick(int lo, int hi, uint seed)
        {
            var tones = Enumerable.Range(lo, hi - lo + 1).Where(t => _byTone.ContainsKey(t)).ToList();
            if (tones.Count == 0) return null;
            int tone = tones[(int)(seed % (uint)tones.Count)];
            var heads = _byTone[tone];
            return (tone, heads[(int)(seed / 7 % (uint)heads.Count)]);
        }
    }

    /// <summary>FNV-1a of the name: a stable seed, so a reinstall gives a player the same face.</summary>
    private static uint Seed(string s)
    {
        uint h = 0x811c9dc5;
        foreach (char ch in s) { h ^= ch; h *= 0x01000193; }
        return h;
    }

    public static void Apply(T3Db db, PackageSpec spec, PackageInstaller.Result r)
    {
        // one checksum fix per table at the end, not one per field written (no-op inside the installer's own batch)
        using var batch = db.Batch();
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
                            Shoots = Str(el, "shoots") ?? Str(el, "catches"), Height = Num(el, "height"), Weight = Num(el, "weight"),
                            Number = Num(el, "jersey_number") ?? Num(el, "number"), City = city, Region = region, Country = country,
                        };
                    }
                    if (!DateTime.TryParseExact(born, new[] { "MM-dd-yyyy", "M-d-yyyy", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var b))
                    { r.Warnings.Add($"players: {name} ({teamName}): birth date '{born}' isn't MM-DD-YYYY; skipped"); continue; }
                    var (first, last) = SplitPersonName(name);
                    if (info == null || string.IsNullOrWhiteSpace(info.Pos)) info = details.GetValueOrDefault(name) ?? info;
                    var item = new Listed(team, season, name, first, last, b, info) { Draft = DraftOf(el), Bubble = Flag(el, "bubble") };
                    if (el.ValueKind == JsonValueKind.Object)
                    {
                        item.Model = StrMap(el, "player_model");
                        item.Captaincy = Str(el, "captaincy")?.Trim().ToUpperInvariant();
                        item.PlayerType = Str(el, "player_type")?.Trim().ToUpperInvariant();
                        if (el.TryGetProperty("neck_guard", out var ngv))
                        {
                            item.NeckGuard = NeckGuardOf(ngv);
                            if (item.NeckGuard == null) r.Warnings.Add($"players: {name} ({teamName}): neck_guard '{ngv}' isn't none/turtleneck/floating (0-2); ignored");
                        }
                    }
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
        var captainTeams = CheckNewKeys(now, r);

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
        var notCreated = new List<Listed>();
        int already = 0;
        foreach (var l in now)
        {
            var (p, nameOnly) = matcher.Find(l.First, l.Last, l.Born);
            // a name-only match whose birth date isn't a typo of the file's is someone else (the game's Connor Brown, born
            // 1994, for a 2005-born college player): the file's player is then not in the game
            if (p != null && nameOnly && !SamePerson(matcher.Born(p), l.Born))
            {
                r.Warnings.Add($"players: {l.Name} ({l.Team.Code}, born {l.Born:yyyy-MM-dd}) is not the game's {p.Name} (born {matcher.Born(p):yyyy-MM-dd}): treated as not in the game");
                p = null;
            }
            // a goalie is never a skater: the same name and birth date on a skater is another person (USHL goalie Adam
            // Novotny and the game's winger Adam Novotny, both born Nov 1 2007)
            if (p != null && GoalieClash(posOf[l].P, p.PositionPrimary))
            {
                r.Warnings.Add($"players: {l.Name} ({l.Team.Code}) is a {(posOf[l].P == 5 ? "goalie" : "skater")} in the file, the game's {p.Name} a {(p.PositionPrimary == 5 ? "goalie" : "skater")}: another person, treated as not in the game");
                p = null;
            }
            // one game player for one roster-file entry (two files' Jackson Smiths both matched the same one)
            if (p != null && !r.TakenPlayers.Add(p.GameId))
            {
                r.Warnings.Add($"players: {l.Name} ({l.Team.Code}): the game's {p.Name} is already placed for another roster-file entry; treated as not in the game");
                p = null;
            }
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
            if (!ps.Create) { notCreated.Add(l); continue; }                // this league only moves existing players
            PlanCreate(l);
        }
        void PlanCreate(Listed l)
        {
            var d = l.Info;
            if (d == null || posOf[l].P < 0) { needDetails.Add(l); return; }
            var bio = BioOf(l, d, posOf[l].P, posOf[l].S);
            var c = new SyncChange { Kind = SyncKind.Missing, Name = l.Name, Position = bio.Position, ToTeam = l.Team.Index, ToTeamName = l.Team.Name, Bio = bio, Selected = true };
            if (PlayerCreator.Problem(c) is { } problem) { r.Warnings.Add($"players: {l.Name}: {problem}"); return; }
            creates.Add(c);
        }
        // a league that only moves existing players still creates the file's players of a team none of whose players
        // are in the game: an empty team crashes Edit Player (see the empty-team check below)
        var filled = new List<string>();
        if (ps.CreateForEmpty && notCreated.Count > 0)
        {
            var teamsWithPlayers = placed.Select(x => x.L.Team.Index).ToHashSet();
            foreach (var g in notCreated.GroupBy(l => l.Team.Index).Where(g => !teamsWithPlayers.Contains(g.Key)).ToList())
            {
                int before = creates.Count;
                foreach (var l in g) { PlanCreate(l); notCreated.Remove(l); }
                filled.Add($"{g.First().Team.Code} ({creates.Count - before})");
            }
            if (filled.Count > 0) r.Lines.Add($"players: league {spec.League?.Id ?? 32}: teams with none of their players in the game get theirs created: {string.Join(", ", filled)}");
        }

        if (moves.Count > 0) r.Lines.Add("players: " + RosterSync.Apply(db, moves));
        if (creates.Count > 0)
        {
            var rule = RuleOf(ps, now);
            var idBlock = IdBlockOf(ps);
            var made = PlayerCreator.Create(db, creates, null, rule, r.OwnArt, idBlock);
            // each created player back to his own roster-file entry: the same name on his team, with his birth date
            // (two players of one name, e.g. Jack O'Brien at ASU and at SHU, must not swap: his photo follows the entry)
            foreach (var cp in made.Created)
                if ((now.FirstOrDefault(l => l.Team.Index == cp.Team && l.Born == cp.Bio.Born && l.Name.Equals(cp.Bio.Name, StringComparison.OrdinalIgnoreCase))
                    ?? now.FirstOrDefault(l => l.Team.Index == cp.Team && l.Name.Equals(cp.Bio.Name, StringComparison.OrdinalIgnoreCase))) is { } l)
                    placed.Add((l, cp.GameId));
            foreach (var (n, why) in made.Skipped) r.Warnings.Add($"players: {n} not created: {why}");
            var ovr = made.Created.Select(x => x.TemplateOverall).OrderBy(x => x).ToList();
            string source = rule == null ? "typical NHL/AHL players"
                : "leagues " + string.Join("/", rule.Leagues) + (rule.MaxOverall > 0 ? $" up to {rule.MaxOverall}" : "")
                  + (rule.Median > 0 || rule.TeamMedians?.Count > 0 ? $", aiming at {(rule.TeamMedians?.Count > 0 ? "each team's median" : $"median {rule.Median}")} +/-{rule.Spread}" : "");
            var gids = made.Created.Select(x => x.GameId).ToList();
            if (gids.Count > 0) r.Lines.Add($"players: game ids {gids.Min()}-{gids.Max()}" + (idBlock is { } b ? $" (block {b.Item1}-{b.Item2})" : ""));
            r.Lines.Add($"players: created {made.Created.Count}"
                        + (ovr.Count == 0 ? "" : $" (ratings copied from {source}: overall {ovr[0]}-{ovr[^1]}, median {ovr[ovr.Count / 2]})"));
        }

        // the NCAA bio and gear
        roster = new Roster(db);
        var bioT = db.Table(Roster.BioTable);
        var bf = bioT.Fields.ToDictionary(f => f.Name);
        var eq = db.Table(PlayerCreator.SkaterEquipmentTable);
        var eqId = eq.Fields.First(f => f.Name == "game_id");
        var eqFace = eq.Fields.FirstOrDefault(f => f.Name == "faceprotectortype");
        var eqF = eq.Fields.ToDictionary(f => f.Name);
        int gloved = 0, helmeted = 0, sticked = 0;
        var neckGuards = new SortedDictionary<int, int>();
        var eqRow = Enumerable.Range(0, eq.WrittenRecords).Where(i => !db.IsInvalid(eq, i))
                              .GroupBy(i => Convert.ToInt32(db.Get(eq, i, eqId))).ToDictionary(g => g.Key, g => g.First());
        int league = spec.League?.Id ?? 32, cages = 0, bubbles = 0, repositioned = 0, drafted = 0, uncontracted = 0;
        var moved = moves.Select(m => m.GameId).ToHashSet();
        // the file's draft (year, round, pick, team) on his bio; the drafting team holds his rights (proteam) unless the
        // game has them with another team (a traded pick). No "draft" in the file: his draft stays as the game has it
        // (a created player is undrafted).
        var nhlByCode = RosterSync.TeamIndex(db);
        var badDraft = new List<string>();
        bool SetDraft(int bioRow, Listed l) => SetFileDraft(db, bioT, bf, nhlByCode, badDraft, bioRow, l);
        // captaincy and player type go on his row on the school's roster (exhibitionrostertable: captain 1 C / 2 A,
        // playerstyle); a team with valid letters has its old ones cleared first
        var rosterT = db.Table(Roster.RosterTable);
        var rosterF = rosterT.Fields.ToDictionary(f => f.Name);
        var rosterRows = Enumerable.Range(0, rosterT.WrittenRecords).Where(i => !db.IsInvalid(rosterT, i))
                                   .GroupBy(i => (Pi: Convert.ToInt32(db.Get(rosterT, i, rosterF["playerindex"])), Team: Convert.ToInt32(db.Get(rosterT, i, rosterF["team"]))))
                                   .ToDictionary(g => g.Key, g => g.ToList());
        var capTeamIdx = captainTeams.Select(t => t.Index).ToHashSet();
        foreach (var ((_, team), rows) in rosterRows)
            if (capTeamIdx.Contains(team)) foreach (int row in rows) db.Set(rosterT, row, rosterF["captain"], 0);
        int captains = 0, styled = 0, toned = 0, keptTone = 0, realFace = 0, eastAsianFaces = 0, haired = 0, shaven = 0;
        var bearded = new SortedDictionary<string, int>();
        var faces = new GenericFaces(db, bioT, bf);
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
            if (SetDraft(p.BioRow, l)) drafted++;
            if (ps.NoContract)
            {
                // no NHL deal while in college, as the game's own NCAA prospects (e.g. L.J. Mooney): status 0, the rest 0
                foreach (var (fname, v) in NoContractFields)
                    if (bf.TryGetValue(fname, out var cf)) db.Set(bioT, p.BioRow, cf, v);
                for (int k = 0; k < ContractSync.ClauseSlots; k++)
                {
                    if (bf.TryGetValue($"contractclause{k}", out var c1)) db.Set(bioT, p.BioRow, c1, 0);
                    if (bf.TryGetValue($"contractclausemntcnumteams{k}", out var c2)) db.Set(bioT, p.BioRow, c2, 0);
                }
                uncontracted++;
            }
            // the package's cage, or a clear bubble for a player the file marks "bubble"
            if (spec.Rules?.FaceProtector is int fp && eqFace != null && !p.IsGoalie && eqRow.TryGetValue(id, out int er))
            {
                if (l.Bubble) { db.Set(eq, er, eqFace, spec.Rules.BubbleFaceProtector ?? 9); bubbles++; }
                else { db.Set(eq, er, eqFace, fp); cages++; }
            }
            // gloves, helmet and neck guard: the player's own neck guard, else his team's "equipment", else the package's
            // rules (each off unless set)
            if (!p.IsGoalie && eqRow.TryGetValue(id, out int er2))
            {
                int? gl = l.Team.Gear("gloves", spec.Rules?.Gloves), hm = l.Team.Gear("helmet", spec.Rules?.Helmet),
                     ng = l.NeckGuard ?? l.Team.Gear("neckGuard", spec.Rules?.NeckGuard);
                if (gl is int g1 && eqF.TryGetValue("gloves", out var gf)) { db.Set(eq, er2, gf, g1); gloved++; }
                if (hm is int h1 && eqF.TryGetValue("helmetbrand", out var hf)) { db.Set(eq, er2, hf, h1); helmeted++; }
                // stick: the team's own "stick" id, else one of its glove brand's (by name, stable across installs)
                if ((l.Team.Gear("stick", null) ?? StickFor(gl, Seed(l.Name + "/stick"))) is int s1 && eqF.TryGetValue("stick", out var sf))
                {
                    db.Set(eq, er2, sf, s1);
                    sticked++;
                }
                if (ng is int n1 && eqF.TryGetValue("neckguardtype", out var nf))
                {
                    db.Set(eq, er2, nf, n1);
                    if (bf.TryGetValue("neckguardexempt", out var xf)) db.Set(bioT, p.BioRow, xf, n1 > 0 ? 0 : 1);
                    neckGuards[n1] = neckGuards.GetValueOrDefault(n1) + 1;
                }
            }
            // the file's captaincy (teams with valid letters only) and player type (skaters), on his school roster row
            if (p.PlayerIndex >= 0 && rosterRows.TryGetValue((p.PlayerIndex, l.Team.Index), out var myRows))
            {
                if (l.Captaincy is "C" or "A" && captainTeams.Contains(l.Team))
                {
                    foreach (int row in myRows) db.Set(rosterT, row, rosterF["captain"], l.Captaincy == "C" ? 1 : 2);
                    captains++;
                }
                if (!p.IsGoalie && l.PlayerType != null && PlayerStyles.TryGetValue(l.PlayerType, out int style))
                    foreach (int row in myRows) db.Set(rosterT, row, rosterF["playerstyle"], style);
            }
            // the player type Edit Player shows ("Player Type") is the ratings row's playerstyle (exhibitionskaterai, same
            // numbers as the roster row's; 2026-11-17: Ziemer showed TWF from a 9 there while his roster row had the
            // file's 7): written there too
            if (!p.IsGoalie && l.PlayerType != null && PlayerStyles.TryGetValue(l.PlayerType, out int ownStyle)
                && p.RatingsTable != null && p.RatingsRow >= 0 && p.RatingsTable.Fields.FirstOrDefault(x => x.Name == "playerstyle") is { } psf)
            {
                db.Set(p.RatingsTable, p.RatingsRow, psf, ownStyle);
                styled++;
            }
            // the file's skin tone: a generic-head player gets a skin colour of that tone and a head made for it (left
            // alone when he's already in the tone's range on a head of it); a player with his own face keeps it
            bool hasTone = l.Model?.GetValueOrDefault("skin_tone") is { } toneName && SkinTones.ContainsKey(toneName);
            var tr = hasTone ? SkinTones[l.Model["skin_tone"]] : default;
            bool eastAsian = l.Model?.GetValueOrDefault("ethnicity") is { } eth && EastAsian.Contains(eth);
            if (hasTone || eastAsian)
            {
                int head = Convert.ToInt32(db.Get(bioT, p.BioRow, bf["headid"])), skin = Convert.ToInt32(db.Get(bioT, p.BioRow, bf["skincolor"]));
                uint seed = Seed(l.Name);
                void SetFace(int newSkin, int newHead)
                {
                    db.Set(bioT, p.BioRow, bf["skincolor"], newSkin);
                    db.Set(bioT, p.BioRow, bf["headid"], newHead);
                    if (bf.TryGetValue("headtexture", out var htf)) db.Set(bioT, p.BioRow, htf, newHead);
                }
                if (head < 100000) realFace++;
                else if (eastAsian)
                {
                    // one of the game's East Asian heads; its own players on them have skin 2-4, so a light/medium file
                    // tone keeps 2-4 and is given 2-3 (Haoxi Wang: the game's 4 kept), a darker one the tone's range
                    var (lo, hi, keepLo, keepHi) = !hasTone || tr.Hi <= 4 ? (2, 3, 2, 4) : tr;
                    int newSkin = skin >= keepLo && skin <= keepHi ? skin : lo + (int)(seed % (uint)(hi - lo + 1));
                    int newHead = EastAsianHeads.Contains(head) ? head : EastAsianHeads[(int)(seed / 7 % (uint)EastAsianHeads.Length)];
                    if (newSkin == skin && newHead == head) keptTone++;
                    else { SetFace(newSkin, newHead); eastAsianFaces++; }
                }
                else if (skin >= tr.KeepLo && skin <= tr.KeepHi && faces.Tone(head) is int ht && ht >= tr.KeepLo && ht <= tr.KeepHi) keptTone++;
                else if (faces.Pick(tr.Lo, tr.Hi, seed) is { } pick)
                {
                    SetFace(pick.Skin, pick.Head);
                    toned++;
                }
            }
            // hair colour (eyebrows and beard the same code, as the game's own players have) and facial hair; generic
            // heads only, a player's own face carries his own hair
            if (Convert.ToInt32(db.Get(bioT, p.BioRow, bf["headid"])) >= 100000)
            {
                int hairCode = Convert.ToInt32(db.Get(bioT, p.BioRow, bf["haircolorcode"]));
                if (l.Model?.GetValueOrDefault("hair_color") is { } hc && HairColors.TryGetValue(hc, out int code))
                {
                    if (hairCode != code || Convert.ToInt32(db.Get(bioT, p.BioRow, bf["eyebrowcolorcode"])) != code) haired++;
                    hairCode = code;
                    db.Set(bioT, p.BioRow, bf["haircolorcode"], code);
                    db.Set(bioT, p.BioRow, bf["eyebrowcolorcode"], code);
                }
                if (l.Model?.GetValueOrDefault("facial_hair") is { } fh && FacialHair.TryGetValue(fh, out var beard)
                    && bf.TryGetValue("playoffbeardstart", out var bStart) && bf.TryGetValue("playoffbeardtype", out var bType)
                    && bf.TryGetValue("playoffbeardmax", out var bMax) && bf.TryGetValue("playoffbeardcolor", out var bColor))
                {
                    db.Set(bioT, p.BioRow, bStart, beard.Stage);
                    if (beard.Stage == 0) shaven++;
                    else
                    {
                        db.Set(bioT, p.BioRow, bType, beard.Styles[(int)(Seed(l.Name + "/beard") % (uint)beard.Styles.Length)]);
                        int max = Convert.ToInt32(db.Get(bioT, p.BioRow, bMax));
                        db.Set(bioT, p.BioRow, bMax, Math.Min(4, Math.Max(max, beard.Stage + 1)));
                        if (bf.TryGetValue("playoffbeardgrowthrate", out var bRate) && Convert.ToInt32(db.Get(bioT, p.BioRow, bRate)) == 0)
                            db.Set(bioT, p.BioRow, bRate, 1);
                        db.Set(bioT, p.BioRow, bColor, hairCode);
                        bearded[fh.ToLowerInvariant()] = bearded.GetValueOrDefault(fh.ToLowerInvariant()) + 1;
                    }
                }
            }
        }

        // lines from the final positions
        RosterLines.BuildTeams(db, spec.Teams.Select(t => t.Index));

        r.Lines.Add($"players: season {current}: {now.Count} listed, {placed.Count} placed ({moves.Count - already} moved, {already} already there, " +
                    $"{placed.Count - moves.Count} created), {needDetails.Count} need details; {drafted} drafted (file's draft); " +
                    (ps.NoContract ? $"{uncontracted} contracts cleared; " : "") + $"{cages} cages, {bubbles} bubbles; gear set: {gloved} gloves, {helmeted} helmets, {sticked} sticks, neck guards " +
                    (neckGuards.Count == 0 ? "left" : string.Join("/", neckGuards.Select(x => $"{x.Value} {NeckGuardNames[x.Key]}"))) + "; " +
                    $"{repositioned} existing players' positions from the file; {forwardsPlaced} \"F\" given a spot; lines rebuilt");
        if (captains + styled + toned + keptTone + realFace > 0)
            r.Lines.Add($"players: letters for {captains} players on {captainTeams.Count} teams; player type for {styled}; skin tone: {toned} given a " +
                    $"matching skin colour and face, {eastAsianFaces} an East Asian face, {keptTone} already matched, {realFace} keep their own face; " +
                    $"hair colour changed for {haired}; facial hair: {shaven} clean-shaven" +
                    string.Concat(bearded.Select(x => $", {x.Value} {x.Key}")) + " (hairstyles not applied yet)");
        // which game player each roster-file entry is (the game may spell him differently: "Nic Sima" for Nicholas)
        foreach (var (l, gid) in placed) r.Placed[(l.Team.Code, PackagePortraits.Slug(l.Name))] = gid;
        foreach (var g in placed.GroupBy(x => x.L.Team.Code))
        {
            var ps4 = g.Select(x => roster.Find(x.GameId)).Where(p => p != null).Select(p => Group(p.PositionPrimary)).ToList();
            int f = ps4.Count(x => x == 0), dd = ps4.Count(x => x == 1), gg = ps4.Count(x => x == 2);
            r.Lines.Add($"  {g.Key}: {g.Count()} players ({f} F, {dd} D, {gg} G)");
            if (f < 12 || dd < 6 || gg < 2) r.Warnings.Add($"players: {g.Key} can't dress a full lineup (needs 12 F, 6 D, 2 G)");
        }
        // a team with nobody on it: Edit Player on its league crashed the game (2026-11-13: the roster provider hands
        // back the inactive Create-a-Player slots for an empty team); every team needs at least one player
        var juniors = new HashSet<int>();
        foreach (int row in Enumerable.Range(0, bioT.WrittenRecords))
            if (!db.IsInvalid(bioT, row)) juniors.Add(Convert.ToInt32(db.Get(bioT, row, bf["juniorteam"])));
        var empty = spec.Teams.Where(t => !juniors.Contains(t.Index) && !roster.Players.Any(p => p.Team == t.Index)).Select(t => t.Code).ToList();
        if (empty.Count > 0)
            r.Warnings.Add($"players: {empty.Count} team(s) with no players ({string.Join(", ", empty)}): Edit Player on this league may crash the game; give each at least one player");
        if (notCreated.Count > 0)
            r.Lines.Add($"players: league {league}: {notCreated.Count} not in the game, not created (players.create = false)");
        if (needDetails.Count > 0)
            r.Warnings.Add($"players: not in the game and no position in {ps.Details ?? "the details file"} ({needDetails.Count}): " +
                           string.Join(", ", needDetails.Select(l => $"{l.Name} ({l.Team.Code})")));
        // entry years: resolved to art ids after the portraits step (EntryYears), which gives created players their own
        foreach (var (l, id) in placed) if (l.Entry is int off) r.EntryGames[id] = off;
        int noClass = now.Count(l => l.Entry == null);
        r.Lines.Add($"players: classes: {r.EntryGames.Count}" + (noClass > 0 ? $", {noClass} without one (birth year + entry age)" : ""));

        // commits: matched after every group's players are in (ApplyCommits), so a commit the package creates itself (an
        // empty USHL team's file players) is found too
        if (commits.Count > 0) r.PendingCommits.Add(new CommitBatch(spec, commits, current));
        if (badDraft.Count > 0) r.Warnings.Add($"players: draft team not an NHL team, draft left as the game has it: {string.Join(", ", badDraft)}");
    }

    /// <summary>A players file's commits (its later seasons) and its roster season, waiting for <see cref="ApplyCommits"/>.</summary>
    internal sealed record CommitBatch(PackageSpec Spec, List<Listed> Commits, string Current);

    /// <summary>The players file's ratings rule (players.ratings), with each listed team's own median.</summary>
    private static TemplateRule RuleOf(PlayersSpec ps, IEnumerable<Listed> listed)
    {
        if (ps?.Ratings?.From is not { Count: > 0 } from) return null;
        var teamMedians = new Dictionary<int, int>();
        if (ps.Ratings.Teams != null)
            foreach (var l in listed)
                if (ps.Ratings.Teams.TryGetValue(l.Team.Code, out int m)) teamMedians[l.Team.Index] = m;
        return new TemplateRule(from, ps.Ratings.MaxOverall, ps.Ratings.Median, teamMedians, ps.Ratings.Spread > 0 ? ps.Ratings.Spread : 6);
    }

    private static (int, int)? IdBlockOf(PlayersSpec ps) => ps?.Ids is { Count: 2 } ids ? (ids[0], ids[1]) : null;

    /// <summary>
    /// Creates the commits not in the game (players.createCommits): each as an unsigned prospect with no team (the way
    /// the game keeps its own young prospects), rated by the file's rule for his school and in its id block, then given
    /// the file's draft and an art-id commit to his school. Returns the commits created; the rest stay name commits.
    /// </summary>
    private static HashSet<Listed> CreateCommits(T3Db db, PackageInstaller.Result r, PackageSpec spec, List<(Listed L, int Offset)> missing, List<string> badDraft)
    {
        var done = new HashSet<Listed>();
        if (missing.Count == 0) return done;
        var changes = new List<SyncChange>();
        var of = new Dictionary<SyncChange, (Listed L, int Offset)>();
        foreach (var (l, off) in missing)
        {
            var d = l.Info;
            int p = Code(d?.Pos, d?.Shoots);
            if (d == null || p == -1) continue;
            bool right = (d.Shoots ?? "L").Trim().StartsWith("R", StringComparison.OrdinalIgnoreCase);
            if (p == Forward) p = right ? 2 : 1;                          // "F": his shooting side's wing
            int s = Code(d.Pos2, d.Shoots, p);
            if (s == p || s == Forward || Group(s) != Group(p)) s = -1;
            var bio = BioOf(l, d, p, s);
            var c = new SyncChange { Kind = SyncKind.Missing, Name = l.Name, Position = bio.Position, ToTeam = l.Team.Index, ToTeamName = l.Team.Name, Bio = bio, Selected = true };
            if (PlayerCreator.Problem(c) != null) continue;
            changes.Add(c); of[c] = (l, off);
        }
        if (changes.Count == 0) return done;
        var teamless = new List<int>();
        var made = PlayerCreator.Create(db, changes, null, RuleOf(spec.Players, missing.Select(m => m.L)), r.OwnArt, IdBlockOf(spec.Players), teamless: true);
        foreach (var (n, why) in made.Skipped) r.Warnings.Add($"players: commit {n} not created: {why}");
        var roster = new Roster(db);
        var bioT = db.Table(Roster.BioTable);
        var bf = bioT.Fields.ToDictionary(f => f.Name);
        var nhlByCode = RosterSync.TeamIndex(db);
        foreach (var cp in made.Created)
        {
            var c = changes.FirstOrDefault(x => x.Bio.Name == cp.Bio.Name && x.Bio.Born == cp.Bio.Born && x.ToTeam == cp.Team);
            if (c == null || roster.Find(cp.GameId) is not { } p) continue;
            var (l, off) = of[c];
            SetFileDraft(db, bioT, bf, nhlByCode, badDraft, p.BioRow, l);
            r.Commits.Add((cp.ArtId, l.Team.Index, off));
            done.Add(l);
            if (p.PlayerIndex >= 0) teamless.Add(p.PlayerIndex);
        }
        var ovr = made.Created.Select(x => x.TemplateOverall).OrderBy(x => x).ToList();
        var gids = made.Created.Select(x => x.GameId).ToList();
        if (made.Created.Count > 0)
            r.Lines.Add($"players: commits created ({made.Created.Count}, unsigned prospects with no team until they join their school): " +
                        $"overall {ovr[0]}-{ovr[^1]}, median {ovr[ovr.Count / 2]}; game ids {gids.Min()}-{gids.Max()}");
        // on the free-agent list, or a new franchise drops them (it keeps a rosterless player only when he's listed)
        int listed = RosterSync.ListFreeAgents(db, teamless);
        if (made.Created.Count > 0) r.Lines.Add($"players: {listed} created commits put on the free-agent list (a franchise keeps them)");
        return done;
    }

    /// <summary>
    /// The players files' commits (their later seasons), after every group's players are in: a commit the package
    /// creates itself (the file players of a USHL team none of whose players were in the game) is in the game by then,
    /// so he gets an art-id commit (part 8 sends any player to his school) instead of a name commit, which only catches
    /// a player already at a school (part 11 runs for league-32 players). Before 2026-11-14 the commits were matched
    /// inside each group's step, before the USHL group created its players: ~50 USHL commits would never have joined.
    /// A commit already in the game is matched by full name + birth date (PlayerMatcher), by his unique full name when
    /// the birth dates are a typo or days apart (SamePerson; Rogowski Jun 24 / Jun 28), or by first name +
    /// birth date with a last name one letter off (Dinneen / Dineen, the only such player); his art id must be his
    /// alone: one on a shared art id without a portrait (the generic 12586/12587) is given his own, after the highest
    /// art or audio id. Commits not in the game go to the name table for the game's generated prospects.
    /// </summary>
    public static void ApplyCommits(T3Db db, PackageInstaller.Result r)
    {
        if (r.PendingCommits.Count == 0) return;
        using var batch = db.Batch();
        var roster = new Roster(db);
        var matcher = new PlayerMatcher(db, roster);
        var bioT = db.Table(Roster.BioTable);
        var bf = bioT.Fields.ToDictionary(f => f.Name);
        var nhlByCode = RosterSync.TeamIndex(db);
        var artCount = new Dictionary<int, int>();
        foreach (var p in roster.Players) artCount[p.ArtId] = artCount.GetValueOrDefault(p.ArtId) + 1;
        // a commit on a shared art id (the generic 12586/12587 of 300-odd players without a portrait) gets an art id of
        // his own, as a created player does: after the highest art or audio id, so none clashes
        var audio = bf.GetValueOrDefault("audioid");
        int nextArt = 1 + Math.Max(r.OwnArt.Max, roster.Players.Max(p => Math.Max(p.ArtId, audio == null ? 0 : Convert.ToInt32(db.Get(bioT, p.BioRow, audio)))));
        var byBorn = new Dictionary<DateTime, List<RosterPlayer>>();
        foreach (var p in roster.Players)
            if (matcher.Born(p) is { } b) (byBorn.TryGetValue(b, out var l) ? l : byBorn[b] = new List<RosterPlayer>()).Add(p);
        foreach (var (spec, commits, current) in r.PendingCommits)
        {
            var skipped = new List<string>();
            var badDraft = new List<string>();
            int drafted = 0, close = 0, spelled = 0, own = 0, before = r.Commits.Count;
            var toCreate = new List<(Listed L, int Offset)>();
            var rosterless = new List<int>();
            foreach (var l in commits)
            {
                var (p, nameOnly) = matcher.Find(l.First, l.Last, l.Born);
                // a goalie is never a skater (and back): another person with the same name
                if (p != null && GoalieClash(Code(l.Info?.Pos, l.Info?.Shoots), p.PositionPrimary))
                {
                    r.Lines.Add($"players: commit {l.Name} ({l.Team.Code} {l.Season}) is not the game's {p.Name}: one is a goalie, the other a skater");
                    p = null; nameOnly = false;
                }
                // the same unique full name with a birth date a typo or a few days off the file's: the same player
                if (p != null && nameOnly && SamePerson(matcher.Born(p), l.Born)) { nameOnly = false; close++; }
                // the same first name and birth date, a last name one letter off: the only such player
                if (p == null && byBorn.TryGetValue(l.Born, out var sameDay)
                    && sameDay.Where(c => RosterSync.Norm(c.FirstName) == RosterSync.Norm(l.First) && OneEdit(RosterSync.Norm(c.LastName), RosterSync.Norm(l.Last))).Take(2).ToList() is { Count: 1 } one)
                { p = one[0]; spelled++; r.Lines.Add($"players: commit {l.Name} ({l.Team.Code} {l.Season}) is the game's {p.Name} (same birth date)"); }
                // not in the game yet: the game may generate him later (a draft-class prospect with his real name);
                // part 11 then sends him to this school by name
                // (players.createCommits: he is created instead, below; one whose birth date says he's another person too)
                if ((p == null || nameOnly) && int.TryParse(l.Season, out int ny) && int.TryParse(current, out int nc) && ny > nc && ny - nc <= 15)
                {
                    // created: no name commit as well. The created recruit is on the free-agent list, so the franchise
                    // keeps him; a name commit would also send the game's own generated namesake to the school (Joey
                    // Cullen, MINN 2027: the franchise generates a second one; 2026-11-17)
                    if (spec.Players?.CreateCommits == true && l.Info != null) { toCreate.Add((l, ny - nc)); continue; }
                    if (p == null) r.NameCommits.Add((l.Name, l.Team.Index, ny - nc));
                }
                if (p == null || nameOnly || !int.TryParse(l.Season, out int year) || !int.TryParse(current, out int cur) || year <= cur || year - cur > 15)
                { skipped.Add($"{l.Name} ({l.Team.Code} {l.Season}){(nameOnly ? ": birth date differs" : "")}"); continue; }
                if (SetFileDraft(db, bioT, bf, nhlByCode, badDraft, p.BioRow, l)) drafted++;
                int art = Convert.ToInt32(db.Get(bioT, p.BioRow, bf["artid"]));
                if (artCount.GetValueOrDefault(art) != 1 && !p.HasPortrait && nextArt <= 0xFFFF)
                {
                    // his own art id (no portrait uses the shared one, so nothing he shows changes)
                    artCount[art]--;
                    // the one he had on the last install, when nobody has it now
                    string artKey = matcher.Born(p) is { } pb ? ArtIdMemory.Key(p.FirstName, p.LastName, pb) : null;
                    bool keptArt = artKey != null && r.OwnArt.Remembered(artKey) is int was && artCount.GetValueOrDefault(was) == 0;
                    art = keptArt ? r.OwnArt.Remembered(artKey).Value : nextArt++;
                    if (artKey != null) r.OwnArt.Record(artKey, art, keptArt);
                    db.Set(bioT, p.BioRow, bf["artid"], art);
                    artCount[art] = 1;
                    own++;
                }
                if (art <= 0 || art > 0xFFFF || artCount.GetValueOrDefault(art) != 1) { skipped.Add($"{l.Name}: art id {art} not his alone"); continue; }
                r.Commits.Add((art, l.Team.Index, year - cur));        // seasons after the roster season: the game adds the franchise's year
                if (p.RosterRow < 0 && p.PlayerIndex >= 0) rosterless.Add(p.PlayerIndex);
            }
            // a matched commit with no roster row (a bio-only prospect) needs the free-agent list too, or a new
            // franchise drops him
            int listedMatched = RosterSync.ListFreeAgents(db, rosterless);
            if (listedMatched > 0) r.Lines.Add($"players: {listedMatched} commits in the game with no roster row put on the free-agent list");
            var createdCommits = CreateCommits(db, r, spec, toCreate, badDraft);
            foreach (var c in r.Commits) nextArt = Math.Max(nextArt, c.ArtId + 1);       // past the created ones' ids
            // those not created after all (no usable position, no room): name commits, as before
            foreach (var (l, off) in toCreate)
                if (!createdCommits.Contains(l)) r.NameCommits.Add((l.Name, l.Team.Index, off));
            r.Lines.Add($"players: commits ({commits.Count}): " +
                        string.Join("; ", commits.GroupBy(c => (c.Team.Code, c.Season)).Select(g => $"{g.Key.Code} {g.Key.Season}: {g.Count()}")) +
                        $"; {r.Commits.Count - before} in the game join their school in their commit year ({close} with a birth date a typo or a few days off, " +
                        $"{spelled} with a last name one letter off, {own} given an art id of their own); {drafted} given the file's draft");
            if (skipped.Count > 0)
                r.Lines.Add($"players: commits not in the game or not matched exactly ({skipped.Count}): {string.Join(", ", skipped)}");
            if (badDraft.Count > 0) r.Warnings.Add($"players: commits' draft team not an NHL team, draft left as the game has it: {string.Join(", ", badDraft)}");
        }
        r.PendingCommits.Clear();
    }

    /// <summary>
    /// Whether a game player found by his unique full name, born <paramref name="game"/>, is the file's player born
    /// <paramref name="file"/>: the dates are within a month (sources differ by days), differ in one of year (by one),
    /// month or day (a typo), or have day and month swapped. Anything else is another person with the same name.
    /// </summary>
    internal static bool SamePerson(DateTime? game, DateTime file)
    {
        if (game is not { } g) return false;
        if (Math.Abs((g - file).TotalDays) <= 31) return true;
        int fields = (g.Year != file.Year ? 1 : 0) + (g.Month != file.Month ? 1 : 0) + (g.Day != file.Day ? 1 : 0);
        if (fields == 1 && Math.Abs(g.Year - file.Year) <= 1) return true;
        return g.Year == file.Year && g.Month == file.Day && g.Day == file.Month;
    }

    /// <summary>Whether one of two known positions is a goalie and the other a skater (then they are two people).</summary>
    private static bool GoalieClash(int file, int game) => file != -1 && game >= 0 && (file == 5) != (game == 5);

    /// <summary>Whether two names differ by at most one letter (added, dropped or changed).</summary>
    private static bool OneEdit(string a, string b)
    {
        if (a == b) return true;
        if (Math.Abs(a.Length - b.Length) > 1 || a.Length < 3 || b.Length < 3) return false;
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return a.Length == b.Length ? a[(i + 1)..] == b[(i + 1)..]
             : a.Length > b.Length ? a[(i + 1)..] == b[i..] : a[i..] == b[(i + 1)..];
    }

    /// <summary>
    /// The file's draft (year, round, pick, team) on his bio; the drafting team holds his rights (proteam) unless the game
    /// has them with another team (a traded pick). No "draft" in the file: his draft stays as the game has it.
    /// </summary>
    private static bool SetFileDraft(T3Db db, T3Table bioT, Dictionary<string, T3Field> bf, Dictionary<string, int> nhlByCode, List<string> badDraft, int bioRow, Listed l)
    {
        if (l.Draft is not { } d) return false;
        int team = PlayerCreator.DraftTeam(nhlByCode, d.Team);
        if (team < 0) { badDraft.Add($"{l.Name} ({d.Team})"); return false; }
        int oldDraft = Convert.ToInt32(db.Get(bioT, bioRow, bf["draftteam"]));
        int pro = Convert.ToInt32(db.Get(bioT, bioRow, bf["proteam"]));
        db.Set(bioT, bioRow, bf["draftyear"], d.Year - 1900);
        db.Set(bioT, bioRow, bf["draftround"], d.Round);
        db.Set(bioT, bioRow, bf["draftposition"], d.Overall);
        db.Set(bioT, bioRow, bf["draftteam"], team);
        if (pro < 0 || pro == oldDraft) db.Set(bioT, bioRow, bf["proteam"], team);
        return true;
    }

    /// <summary>
    /// After the portraits step: the classed players' class offsets by art id (part 10's table). An art id another bio
    /// shares can't carry a class (the table would give the year to both): such a player's class goes into the name
    /// table instead (part 11, matched by "first last" as the game stores it; offset = minus his class), when his name
    /// is his alone in the game, his school (junior team) is in <paramref name="block"/> and no commit by name uses it.
    /// </summary>
    public static void EntryYears(T3Db db, PackageInstaller.Result r, IReadOnlyCollection<int> block = null)
    {
        if (r.EntryGames.Count == 0) return;
        var roster = new Roster(db);
        var bioT = db.Table(Roster.BioTable);
        var art = bioT.Fields.First(f => f.Name == "artid");
        var junior = bioT.Fields.First(f => f.Name == "juniorteam");
        var count = new Dictionary<int, int>();
        foreach (int row in Enumerable.Range(0, bioT.WrittenRecords))
            if (!db.IsInvalid(bioT, row)) { int a = Convert.ToInt32(db.Get(bioT, row, art)); count[a] = count.GetValueOrDefault(a) + 1; }
        string Key(RosterPlayer p) => $"{p.FirstName?.Trim()} {p.LastName?.Trim()}";
        var names = roster.Players.GroupBy(p => NcaaCodeMods.NameHash(Key(p))).ToDictionary(g => g.Key, g => g.Count());
        var commitNames = r.NameCommits.Select(c => NcaaCodeMods.NameHash(c.Name)).ToHashSet();
        var skipped = new List<string>();
        int byName = 0;
        foreach (var (id, off) in r.EntryGames)
        {
            var p = roster.Find(id);
            int a = p == null ? 0 : Convert.ToInt32(db.Get(bioT, p.BioRow, art));
            if (a > 0 && a <= 0xFFFF && count.GetValueOrDefault(a) == 1) { r.Entries.Add((a, off)); continue; }
            uint h = p == null ? 0 : NcaaCodeMods.NameHash(Key(p));
            int school = p == null ? -1 : Convert.ToInt32(db.Get(bioT, p.BioRow, junior));    // NCAA players: team -1, school = junior team
            if (p != null && block?.Contains(school) == true && !(p.FirstName ?? "").Trim().Contains(' ') && off is >= 0 and <= 4
                && names.GetValueOrDefault(h) == 1 && commitNames.Add(h))
            {
                r.NameCommits.Add((Key(p), school, -off));
                byName++;
                continue;
            }
            skipped.Add(p?.Name ?? $"game id {id}");
        }
        r.Lines.Add($"players: classes stored for {r.Entries.Count + byName} players ({byName} by name: their art id is shared)");
        if (skipped.Count > 0) r.Warnings.Add($"players: class not stored (art id shared, name not his alone): {string.Join(", ", skipped)}");
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

    /// <summary>players.contract "none": the bio's contract as the game's NCAA prospects have it (L.J. Mooney, Michael
    /// Hage): status 0, no length, salary, two-way, extension, retained salary or clauses (clauses set separately).</summary>
    private static readonly (string Field, int Value)[] NoContractFields =
    {
        ("contractstatus", 0), ("contractlength", 0), ("contractdollars", 0), ("contractis2way", 0), ("contractismultiyear", 0),
        ("contractneedsentrylevel", 0), ("extensionlength", 0), ("extensiondollars", 0), ("extensionis2way", 0), ("retainedsalarytrades", 0),
    };

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
    /// <summary>false: players not in the game are listed, not created (e.g. the USHL: only existing prospects move).</summary>
    public bool Create { get; set; } = true;
    /// <summary>With <see cref="Create"/> false: a team none of whose players are in the game still gets the file's players
    /// created (an empty team crashes the game's Edit Player screen). Default true.</summary>
    public bool CreateForEmpty { get; set; } = true;
    /// <summary>"none": every placed player's NHL contract is cleared the way the game's own NCAA prospects have it
    /// (status 0, length 0, salary 0, not two-way), whatever deal he had; null leaves contracts alone.</summary>
    public string Contract { get; set; }
    /// <summary>Where created players' ratings come from: { "from": [league ids], "maxOverall": n } (e.g. the CHL, 11-13,
    /// up to 67 for the USHL). Missing: a typical NHL/AHL player of his profile (PlayerCreator's default).</summary>
    public RatingsSpec Ratings { get; set; }
    /// <summary>true: commits (later seasons) not in the game are created as unsigned prospects with no team, rated by
    /// <see cref="Ratings"/> for their school and in <see cref="Ids"/>, and join their school by art id like any commit.
    /// false (default): they are only listed by name, for a prospect the game may generate with that name.</summary>
    public bool CreateCommits { get; set; }
    /// <summary>The game ids this file's created players use: [from, to], within PlayerCreator.PackageIdStart..SpecialIdStart-1
    /// (e.g. [40000, 41999]), so the package can say which ids it takes. Missing: after the highest id below the blocks.</summary>
    public List<int> Ids { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool NoContract => string.Equals(Contract, "none", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Recipe players.ratings: see <see cref="PlayersSpec.Ratings"/>.</summary>
public sealed class RatingsSpec
{
    public List<int> From { get; set; }
    public int MaxOverall { get; set; }
    /// <summary>The overall created players centre on (0: any candidate, a spread over the leagues' range).</summary>
    public int Median { get; set; }
    /// <summary>A team's own median, by team code (wins over <see cref="Median"/>).</summary>
    public Dictionary<string, int> Teams { get; set; }
    /// <summary>How far a player may land from the median (default 6; most land near it).</summary>
    public int Spread { get; set; }
}
