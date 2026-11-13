using System.Text.Json;
using System.Text.Json.Serialization;

namespace NHL27Kit.Core.Packages;

/// <summary>
/// A package's recipe (package.json in its source folder): a mod made of instructions that HMS runs against the game as
/// it is now and records as one mod-list entry. See docs/PACKAGES.md.
/// </summary>
public sealed class PackageSpec
{
    public string Id { get; set; }
    public string Title { get; set; }
    public string Version { get; set; }
    public LeagueSpec League { get; set; }
    public TeamBlockSpec TeamBlock { get; set; }
    /// <summary>Optional: teams to retire when the team tables are full (see RecycleSpec).</summary>
    public RecycleSpec RecycleTeams { get; set; }
    public List<TeamSpec> Teams { get; set; } = new();
    public PlayersSpec Players { get; set; }
    public RulesSpec Rules { get; set; } = new();
    /// <summary>Language strings the package sets (key → text), besides the league's: e.g. the message the code mods show.</summary>
    public Dictionary<string, string> Strings { get; set; } = new();
    public NameSetsSpec NameSets { get; set; } = new();
    public CodeModsSpec CodeMods { get; set; }
    /// <summary>Folder (relative) of the players' portraits, &lt;team code&gt;/&lt;first-last&gt;.png, 512×340 ready-made
    /// (see PackagePortraits); default "portraits" when that folder exists.</summary>
    public string Portraits { get; set; }

    /// <summary>The source folder the recipe was read from (paths in it are relative to this).</summary>
    [JsonIgnore] public string Folder { get; set; }

    public const string FileName = "package.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads a package folder's recipe. Throws with a plain reason when it can't be used at all.</summary>
    public static PackageSpec Load(string folder)
    {
        string file = Path.Combine(folder, FileName);
        if (!System.IO.File.Exists(file)) throw new FileNotFoundException($"No {FileName} in {folder}.");
        var spec = JsonSerializer.Deserialize<PackageSpec>(System.IO.File.ReadAllText(file), Options)
                   ?? throw new InvalidDataException($"{file} is empty.");
        spec.Folder = Path.GetFullPath(folder);
        if (string.IsNullOrWhiteSpace(spec.Id)) throw new InvalidDataException($"{file} has no \"id\".");
        if (!string.IsNullOrEmpty(spec.TeamsFile)) spec.MergeTeamsFile();
        return spec;
    }

    /// <summary>
    /// The team data file (e.g. data/teams.json): one object per team, keyed by full name, with city, team_name,
    /// abbreviation, conference, colours, arena and rivals. A team in "teams" whose code matches an abbreviation takes
    /// every field it doesn't set itself from there, so the data file stays the one place team data is edited.
    /// </summary>
    public string TeamsFile { get; set; }

    /// <summary>Every team in the data file (by abbreviation), after loading; empty without a data file.</summary>
    [JsonIgnore] public Dictionary<string, TeamSource> Source { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    private void MergeTeamsFile()
    {
        string path = PathOf(TeamsFile);
        if (!System.IO.File.Exists(path)) throw new FileNotFoundException($"The teams file {path} isn't there.");
        var all = JsonSerializer.Deserialize<Dictionary<string, TeamSource>>(System.IO.File.ReadAllText(path), Options)
                  ?? throw new InvalidDataException($"{path} is empty.");
        foreach (var (key, s) in all)
        {
            s.TeamName ??= key;
            if (!string.IsNullOrEmpty(s.Abbreviation)) Source[s.Abbreviation] = s;
        }
        foreach (var t in Teams)
        {
            if (!Source.TryGetValue(t.Code ?? "", out var s)) continue;
            t.Name ??= s.TeamName;
            t.Abbr ??= s.Abbreviation;
            t.City ??= s.City;
            t.Conference ??= s.Conference;
            t.Primary ??= s.PrimaryMenuColor;
            t.Secondary ??= s.SecondaryMenuColor;
            if (t.School == null || t.Nickname == null)
            {
                var (school, nick) = SplitName(t.Name);
                t.School ??= school;
                t.Nickname ??= nick;
            }
            t.Data = s;
        }
    }

    /// <summary>Two-word nicknames; any other name splits before its last word ("Minnesota Golden Gophers" → Minnesota / Golden Gophers).</summary>
    private static readonly string[] TwoWordNicknames =
    {
        "Golden Gophers", "Black Knights", "Golden Griffins", "Purple Eagles", "Black Bears", "River Hawks", "Golden Knights",
        "Big Red", "Big Green", "Fighting Irish", "Nittany Lions", "Sun Devils", "Fighting Hawks", "Garnet Chargers",
    };

    public static (string School, string Nickname) SplitName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, null);
        string two = TwoWordNicknames.FirstOrDefault(n => name.EndsWith(" " + n, StringComparison.OrdinalIgnoreCase));
        if (two != null) return (name[..^(two.Length + 1)], name[^two.Length..]);
        int sp = name.LastIndexOf(' ');
        return sp < 0 ? (name, name) : (name[..sp], name[(sp + 1)..]);
    }

    /// <summary>A path in the recipe, made absolute against the package folder.</summary>
    public string PathOf(string relative) => string.IsNullOrEmpty(relative) ? null : Path.GetFullPath(Path.Combine(Folder, relative));

    /// <summary>The folder of a team's logo images: logos/teams/&lt;code&gt;.</summary>
    public string TeamLogoFolder(TeamSpec t) => Path.Combine(Folder, "logos", "teams", t.Code);

    /// <summary>
    /// A team's main logo: logos/teams/&lt;code&gt;/logo.png, or the folder's only PNG when there's no logo.png and the
    /// folder holds just one (any name). Null when there's none.
    /// </summary>
    public string TeamLogoFile(TeamSpec t) => LogoIn(TeamLogoFolder(t));

    /// <summary>The league logo: league.logo, or logos/league's logo.png / only PNG.</summary>
    public string LeagueLogoFile() =>
        League?.Logo is { } l && System.IO.File.Exists(PathOf(l)) ? PathOf(l) : LogoIn(Path.Combine(Folder, "logos", "league"));

    private static readonly string[] Extras = { "logo_lightbg.png", "mono.png", "ice.png" };

    private static string LogoIn(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        string main = Path.Combine(dir, "logo.png");
        if (System.IO.File.Exists(main)) return main;
        var pngs = Directory.GetFiles(dir, "*.png").Where(f => !Extras.Contains(Path.GetFileName(f).ToLowerInvariant())).ToList();
        return pngs.Count == 1 ? pngs[0] : null;
    }
}

/// <summary>One team in the team data file (data/teams.json).</summary>
public sealed class TeamSource
{
    [JsonPropertyName("city")] public string City { get; set; }
    [JsonPropertyName("team_name")] public string TeamName { get; set; }
    [JsonPropertyName("nickname")] public string Nickname { get; set; }
    [JsonPropertyName("abbreviation")] public string Abbreviation { get; set; }
    [JsonPropertyName("conference")] public string Conference { get; set; }
    [JsonPropertyName("primary_menu_color")] public string PrimaryMenuColor { get; set; }
    [JsonPropertyName("secondary_menu_color")] public string SecondaryMenuColor { get; set; }
    [JsonPropertyName("tertiary_menu_color")] public string TertiaryMenuColor { get; set; }
    [JsonPropertyName("color_1")] public string Color1 { get; set; }
    [JsonPropertyName("color_2")] public string Color2 { get; set; }
    [JsonPropertyName("color_3")] public string Color3 { get; set; }
    [JsonPropertyName("primary_broadcast_color")] public string PrimaryBroadcastColor { get; set; }
    [JsonPropertyName("secondary_broadcast_color")] public string SecondaryBroadcastColor { get; set; }
    [JsonPropertyName("tertiary_broadcast_color")] public string TertiaryBroadcastColor { get; set; }
    [JsonPropertyName("overlay_primary")] public string OverlayPrimary { get; set; }
    [JsonPropertyName("overlay_secondary")] public string OverlaySecondary { get; set; }
    [JsonPropertyName("overlay_tertiary")] public string OverlayTertiary { get; set; }
    [JsonPropertyName("arena_name")] public string ArenaName { get; set; }
    [JsonPropertyName("arena_city")] public string ArenaCity { get; set; }
    [JsonPropertyName("capacity")] public int? Capacity { get; set; }
    [JsonPropertyName("year_opened")] public int? YearOpened { get; set; }
    [JsonPropertyName("crowd_light_color")] public string CrowdLightColor { get; set; }
    [JsonPropertyName("main_rival")] public string MainRival { get; set; }
    [JsonPropertyName("second_rival")] public string SecondRival { get; set; }
    [JsonPropertyName("average_attendance_pct")] public int? AverageAttendancePct { get; set; }
}

public sealed class LeagueSpec
{
    /// <summary>The league id the teams go in (stockleagues.leagueid).</summary>
    public int Id { get; set; }
    /// <summary>Language strings to set (string id → text), e.g. the league's name and the scouting-region names.</summary>
    public Dictionary<string, string> Strings { get; set; } = new();
    /// <summary>The league logo image (relative to the package folder).</summary>
    public string Logo { get; set; }
}

/// <summary>The contiguous team-index block the package's teams live in (code mod 4 picks generated prospects' schools from it).</summary>
public sealed class TeamBlockSpec
{
    public int First { get; set; }
    public int Last { get; set; }
}

/// <summary>Teams the package may retire when the team tables run out of room: rows of this league whose name matches
/// the pattern and that own no players, highest index first, only as many as needed. Their team, strategy, arena,
/// jersey, equipment and roster rows are removed (the package's mod entry restores them on removal).</summary>
public sealed class RecycleSpec
{
    public int League { get; set; }
    /// <summary>Regex on teamname; default: the online event teams "Circuit N Team M".</summary>
    public string NamePattern { get; set; } = @"^Circuit \d+ Team \d+$";
}

public sealed class TeamSpec
{
    /// <summary>Short key: the logo folder's name and how players point at their school.</summary>
    public string Code { get; set; }
    public int Index { get; set; }
    /// <summary>The existing team the new row is cloned from (strategies, jerseys, equipment).</summary>
    public int Template { get; set; }
    public string Name { get; set; }
    public string School { get; set; }
    public string Nickname { get; set; }
    /// <summary>Optional shorter forms, used when a name set's string has no room for the full text.</summary>
    public string NicknameShort { get; set; }
    public string NameShort { get; set; }
    public string City { get; set; }
    public string Abbr { get; set; }
    public string Conference { get; set; }
    public string Primary { get; set; }
    public string Secondary { get; set; }
    /// <summary>Arena settings: exhibitionarena field → value for the team's own arena row, e.g. { "goalhorn": 2 }
    /// (an audio id from the game's goalhorns.csv: 0-31 NHL teams, 40 generic, 41-374 novelty horns).</summary>
    public Dictionary<string, int> Arena { get; set; }
    /// <summary>The team's row in the team data file, when there is one (colours, arena, rivals...).</summary>
    [JsonIgnore] public TeamSource Data { get; set; }
}

public sealed class NameSetsSpec
{
    /// <summary>Name sets (artabbr) the package must not take, e.g. the old KHL clubs' sets kept for a KHL package.</summary>
    public List<string> Avoid { get; set; } = new();
    /// <summary>"school": team lists show the school alone ("Minnesota"; nicknames keep their own strings), the same
    /// for every team. Otherwise ("full", the default) the full name where a set has room for it.</summary>
    public string Style { get; set; }
    public bool SchoolOnly => string.Equals(Style, "school", StringComparison.OrdinalIgnoreCase);
}

public sealed class RulesSpec
{
    /// <summary>faceprotectortype for the package's players: 7 full cage, 9 full shield (bubble); null leaves it.</summary>
    public int? FaceProtector { get; set; }
    /// <summary>Game days the US regions keep per 16 (the NCAA-length season, code mod); 16 = unchanged. Default 9.</summary>
    public int? SeasonKeep { get; set; }
    /// <summary>NCAA seasons before an undrafted NCAA player leaves school for free agency (code mod). Default 4.</summary>
    public int? NcaaSeasons { get; set; }
    /// <summary>NCAA players without a stored entry year started college in the fall of birth year + this. Default 19.</summary>
    public int? NcaaEntryAge { get; set; }
}

public sealed class CodeModsSpec
{
    /// <summary>A Kyty/GoldHEN patch file (relative to the package folder) whose mods the package installs.</summary>
    public string File { get; set; }
}
