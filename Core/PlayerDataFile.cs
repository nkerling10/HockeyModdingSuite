using System.Text.Json;
using System.Text.Json.Serialization;

namespace NHL27Kit.Core;

/// <summary>One player in the player data file: what his CapWages page says that almost never changes.</summary>
public sealed class PlayerDataEntry
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string First { get; set; } = "";
    public string Last { get; set; } = "";
    /// <summary>yyyy-MM-dd, or "".</summary>
    public string Born { get; set; } = "";
    public int HeightIn { get; set; }
    public int WeightLb { get; set; }
    public string Shoots { get; set; } = "";
    /// <summary>The source's position (C/LW/RW/D/G) and the game's primary/secondary codes (0=C … 5=G, -1 none).</summary>
    public string Position { get; set; } = "";
    public int Primary { get; set; } = -1;
    public int Secondary { get; set; } = -1;
    public string BirthCity { get; set; } = "";
    public string BirthCountry { get; set; } = "";
    public string BirthRegion { get; set; } = "";
    /// <summary>0 when undrafted.</summary>
    public int DraftYear { get; set; }
    public string DraftTeam { get; set; } = "";
    public int DraftRound { get; set; }
    public int DraftOverall { get; set; }
    /// <summary>His photo on the page: NHL.com's headshot when <see cref="HeadshotNhl"/>, else another source's; "" for none.</summary>
    public string Headshot { get; set; } = "";
    public bool HeadshotNhl { get; set; }
    /// <summary>When his page was read (yyyy-MM-dd).</summary>
    public string Read { get; set; } = "";

    public PlayerBio ToBio() =>
        new(First, Last, -1, Position, Shoots, HeightIn, WeightLb,
            DateTime.TryParse(Born, out var b) ? b : null, BirthCity,
            DraftYear > 0 ? new DraftInfo(DraftYear, DraftTeam, DraftRound, DraftOverall) : DraftInfo.Undrafted, "CapWages")
        { Primary = Primary, Secondary = Secondary, BirthCountry = BirthCountry ?? "", BirthRegion = BirthRegion ?? "" };

    public static PlayerDataEntry From(string slug, PlayerBio b, (string Url, bool Nhl)? shot) => new()
    {
        Slug = slug, Name = b.Name, First = b.First ?? "", Last = b.Last ?? "", Born = b.Born is { } d ? d.ToString("yyyy-MM-dd") : "",
        HeightIn = b.HeightIn, WeightLb = b.WeightLb, Shoots = b.Shoots ?? "", Position = b.Position ?? "",
        Primary = b.Primary, Secondary = b.Secondary, BirthCity = b.BirthCity ?? "", BirthCountry = b.BirthCountry ?? "",
        BirthRegion = b.BirthRegion ?? "",
        DraftYear = b.Draft?.Year ?? 0, DraftTeam = b.Draft?.Team ?? "", DraftRound = b.Draft?.Round ?? 0, DraftOverall = b.Draft?.Overall ?? 0,
        Headshot = shot?.Url ?? "", HeadshotNhl = shot?.Nhl ?? false, Read = DateTime.Now.ToString("yyyy-MM-dd"),
    };
}

/// <summary>
/// The player data file: each AHL player's bio from his CapWages page (height, weight, birth date and place, shot,
/// positions, draft, photo), read once and kept. Syncs are for transactions, which come from the team pages (TheAHL.com
/// rosters, CapWages team pages with contracts and picks), so a later sync reads a player page only for a player the
/// file doesn't have yet. Deleting the file (or a player's entry) makes the next sync read those pages again; an entry
/// can also be edited by hand. On this machine only, in the kit's player-data folder (never in the repo or shipped).
/// </summary>
public sealed class PlayerDataFile
{
    public static string DefaultPath => System.IO.Path.Combine(KitPaths.Root, "player-data", "ahl-players.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Dictionary<string, PlayerDataEntry> _players;
    private readonly object _lock = new();
    public string Path { get; }

    private PlayerDataFile(string path, Dictionary<string, PlayerDataEntry> players) { Path = path; _players = players; }

    public int Count { get { lock (_lock) return _players.Count; } }
    public bool Exists => File.Exists(Path);

    /// <summary>The file at <paramref name="path"/> (default <see cref="DefaultPath"/>); empty when there's none or it can't be read.</summary>
    public static PlayerDataFile Load(string path = null)
    {
        path ??= DefaultPath;
        var map = new Dictionary<string, PlayerDataEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<FileBody>(File.ReadAllText(path), Json) is { Players: { } list })
                foreach (var e in list.Where(e => !string.IsNullOrWhiteSpace(e.Slug))) map[e.Slug] = e;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new PlayerDataFile(path, map);
    }

    public PlayerDataEntry Get(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        lock (_lock) return _players.TryGetValue(slug, out var e) ? e : null;
    }

    public void Set(PlayerDataEntry e)
    {
        lock (_lock) _players[e.Slug] = e;
    }

    public void Save()
    {
        List<PlayerDataEntry> list;
        lock (_lock) list = _players.Values.OrderBy(e => e.Last, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.First).ThenBy(e => e.Slug).ToList();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
        string tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new FileBody { Saved = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), Players = list }, Json));
        File.Move(tmp, Path, true);
    }

    /// <summary>
    /// A player's entry: from the file when it has him, else read from his CapWages page (cached pages count) and added.
    /// Null when the page has no player. <paramref name="fetched"/> is true when a page was read.
    /// </summary>
    public async Task<(PlayerDataEntry Entry, bool Fetched)> GetOrReadAsync(string slug, CancellationToken ct = default)
    {
        if (Get(slug) is { } have) return (have, false);
        var bio = await CapWagesApi.BioAsync(slug, ct);
        if (bio == null) return (null, true);
        // the same page again (now cached): no second request
        var shot = await CapWagesApi.HeadshotAsync(slug, ct);
        var e = PlayerDataEntry.From(slug, bio, shot);
        Set(e);
        return (e, true);
    }

    private sealed class FileBody
    {
        [JsonPropertyOrder(0)] public string Note { get; set; } =
            "HMS player data: each AHL player's CapWages bio, read once. Delete an entry (or the file) to read it again.";
        [JsonPropertyOrder(1)] public string Saved { get; set; } = "";
        [JsonPropertyOrder(2)] public List<PlayerDataEntry> Players { get; set; } = new();
    }
}
