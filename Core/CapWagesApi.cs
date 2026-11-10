using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NHL27Kit.Core;

/// <summary>One season of a contract as CapWages lists it. Dollars are whole dollars; StartYear 2026 = the 2026-27 season.</summary>
public sealed record CapSeason(int StartYear, long CapHit, long BaseSalary, long? MinorsSalary, string Clause, int? ListSize,
                               IReadOnlyList<(string Team, long CapHit)> Retention);

/// <summary>One contract: its type ("Entry-Level Contract", "Two-Way Contract (Extension)"…), expiry status ("UFA", "RFA"),
/// the clause text ("10 team no trade list"), and its seasons in order.</summary>
public sealed record CapContract(string Type, string Expiry, string ClauseDetails, IReadOnlyList<CapSeason> Seasons);

/// <summary>A player under contract with a team (NHL roster, minors, injured or inactive). Section is the page's group ("roster", "non-roster", "inactive").</summary>
public sealed record CapPlayer(string First, string Last, DateTime? Born, string Section, string TermsDetails, IReadOnlyList<CapContract> Contracts)
{
    public string Name => $"{First} {Last}";
    /// <summary>The positions CapWages lists him at ("C", "LW, RW", "LD, RD"), from where he has played; "" when none.</summary>
    public string Pos { get; init; } = "";
    /// <summary>Games he has played at each position ("LW" → 96, "RW" → 46, "C" → 3); empty when CapWages has none.</summary>
    public IReadOnlyDictionary<string, int> PositionGames { get; init; } = new Dictionary<string, int>();
    /// <summary>His status on the team page: "NHL", "Minor", "IR", "LTIR", "SEIR" (season-ending IR), "Suspended"…; "" when none.</summary>
    public string Status { get; init; } = "";
    /// <summary>His CapWages player page (capwages.com/players/{Slug}); "" when the team page gives none.</summary>
    public string Slug { get; init; } = "";
}

/// <summary>One row of a player's stats on his CapWages page: season ("2026-27"), team ("Saginaw Spirit"), league ("OHL"), games.</summary>
public sealed record CapStat(string Season, string Team, string League, int Games);

/// <summary>A player's CapWages page, as CapWagesApi.PageAsync reads it.</summary>
public sealed record CapPage(string Slug, string Name, DateTime? Born, string Status, string TeamAbbr, IReadOnlyList<CapStat> Stats);

/// <summary>A draft pick a team's page lists: real draft year, round, the original team's full name, and whether the team traded it away.</summary>
public sealed record CapPick(int Year, int Round, string OriginalTeam, bool TradedAway);

/// <summary>A bought-out or terminated contract still charged to a team's cap: whose it was ("Skinner, Jeff"), what kind
/// ("buyout history", "terminated penalty") and the cap hit per season (StartYear 2026 = 2026-27), whole dollars.</summary>
public sealed record CapBuyout(string Name, string Kind, IReadOnlyList<(int StartYear, long CapHit)> Seasons);

/// <summary>One NHL team's CapWages page, parsed.</summary>
public sealed record CapTeam(string Abbr, string Name, IReadOnlyList<CapPlayer> Players, IReadOnlyList<CapPick> Picks)
{
    /// <summary>Buyouts and terminated contracts charged to this team (the page's dead cap lists, minus retained salary,
    /// buried contracts and LTIR, which the game works out or stores elsewhere).</summary>
    public IReadOnlyList<CapBuyout> Buyouts { get; init; } = Array.Empty<CapBuyout>();

    /// <summary>The affiliate's players on AHL contracts (the page's "AHL Contracts" table: name, birth date, one position, no
    /// NHL contract). Kept apart from Players so contracts and picks never see them; only their positions are used.</summary>
    public IReadOnlyList<CapPlayer> AhlPlayers { get; init; } = Array.Empty<CapPlayer>();

    /// <summary>The unsigned draft picks whose rights the team holds (the page's "reserves" list: name, birth date, position,
    /// slug; Section "reserve", Status "Prospect", no contract). Only used to find a player's page; never contracts or picks.</summary>
    public IReadOnlyList<CapPlayer> Reserves { get; init; } = Array.Empty<CapPlayer>();
}

/// <summary>
/// NHL contracts and draft picks from CapWages (capwages.com), read from each team's public page at sync time on the
/// user's own machine — nothing from CapWages is stored in the repo or shipped. Each page (capwages.com/teams/{slug})
/// is a Next.js page whose data is embedded as JSON in its __NEXT_DATA__ script: props.pageProps.data holds the
/// players by group ("roster", "non-roster", "inactive", "dead cap"), each with contracts and per-season cap hits,
/// clauses and retention; props.pageProps.draftPicks holds the picks the team owns or traded away. This class is the
/// only code that knows that layout: if CapWages changes it, parsing fails here and the sync reports CapWages failed.
/// The "dead cap" group (retained salary, buyouts, terminated contracts, buried contracts, LTIR) isn't read as players:
/// retained players are read from their own team's roster, where each season carries a retention list, so nobody is
/// counted twice. Only its buyouts and terminated contracts are kept, as the team's Buyouts (each season's buyout.capHit).
/// </summary>
public static class CapWagesApi
{
    public const string Base = "https://capwages.com/teams/";

    /// <summary>NHL abbreviation and CapWages page slug for every team. Abbreviations resolve to game teams via RosterSync.TeamIndex.</summary>
    public static readonly (string Abbr, string Slug)[] Teams =
    {
        ("ANA", "anaheim_ducks"), ("BOS", "boston_bruins"), ("BUF", "buffalo_sabres"), ("CGY", "calgary_flames"),
        ("CAR", "carolina_hurricanes"), ("CHI", "chicago_blackhawks"), ("COL", "colorado_avalanche"),
        ("CBJ", "columbus_blue_jackets"), ("DAL", "dallas_stars"), ("DET", "detroit_red_wings"),
        ("EDM", "edmonton_oilers"), ("FLA", "florida_panthers"), ("LAK", "los_angeles_kings"),
        ("MIN", "minnesota_wild"), ("MTL", "montreal_canadiens"), ("NSH", "nashville_predators"),
        ("NJD", "new_jersey_devils"), ("NYI", "new_york_islanders"), ("NYR", "new_york_rangers"),
        ("OTT", "ottawa_senators"), ("PHI", "philadelphia_flyers"), ("PIT", "pittsburgh_penguins"),
        ("SJS", "san_jose_sharks"), ("SEA", "seattle_kraken"), ("STL", "st_louis_blues"),
        ("TBL", "tampa_bay_lightning"), ("TOR", "toronto_maple_leafs"), ("UTA", "utah_mammoth"),
        ("VAN", "vancouver_canucks"), ("VGK", "vegas_golden_knights"), ("WSH", "washington_capitals"),
        ("WPG", "winnipeg_jets"),
    };

    private static readonly HttpClient Http = MakeClient();

    private static HttpClient MakeClient()
    {
        var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HockeyModdingSuite/1.0");
        return http;
    }

    /// <summary>
    /// Every team's page, one at a time with a pause between them (a light, browser-like pace). Progress is reported per
    /// team (its abbreviation). All or nothing: any team failing throws, because retention totals and pick owners need
    /// the whole league — half a league must never be compared or written.
    /// </summary>
    public static async Task<List<CapTeam>> AllTeamsAsync(Action<string> progress = null, CancellationToken ct = default)
    {
        var all = new List<CapTeam>();
        foreach (var (abbr, slug) in Teams)
        {
            string html = await GetAsync(Base + slug, ct);
            all.Add(Parse(html, abbr));
            progress?.Invoke(abbr);
            await Task.Delay(500, ct);
        }
        return all;
    }

    /// <summary>
    /// The real season the data is "now", as its start year (2026 = 2026-27): the year before the earliest draft the
    /// teams still hold picks in, since a draft that has happened is no longer listed. Pages also list last season's
    /// contracts, so the earliest contract season would be a year too early. Falls back to the date (July 1 starts a season).
    /// </summary>
    public static int CurrentSeason(IReadOnlyList<CapTeam> teams)
    {
        var years = teams.SelectMany(t => t.Picks).Select(p => p.Year).Where(y => y > 0).ToList();
        if (years.Count > 0) return years.Min() - 1;
        var now = DateTime.Today;
        return now.Month >= 7 ? now.Year : now.Year - 1;
    }

    /// <summary>One team page's embedded data, parsed. Throws when the page has no __NEXT_DATA__ block (CapWages changed its layout).</summary>
    public static CapTeam Parse(string html, string abbr)
    {
        var m = Regex.Match(html, "<script id=\"__NEXT_DATA__\"[^>]*>(.*?)</script>", RegexOptions.Singleline);
        if (!m.Success) throw new InvalidOperationException($"CapWages' {abbr} page has no data block: the site's layout may have changed.");
        using var doc = JsonDocument.Parse(m.Groups[1].Value);
        var pp = doc.RootElement.GetProperty("props").GetProperty("pageProps");
        string name = pp.TryGetProperty("teamName", out var tn) && tn.ValueKind == JsonValueKind.String ? tn.GetString() : abbr;

        var players = new List<CapPlayer>();
        if (pp.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            foreach (var section in data.EnumerateObject())
            {
                if (section.Name == "dead cap" || section.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var group in section.Value.EnumerateObject())
                    if (group.Value.ValueKind == JsonValueKind.Array)
                        foreach (var p in group.Value.EnumerateArray())
                            if (ParsePlayer(p, section.Name) is { } player) players.Add(player);
            }

        var picks = new List<CapPick>();
        if (pp.TryGetProperty("draftPicks", out var dp) && dp.ValueKind == JsonValueKind.Array)
            foreach (var p in dp.EnumerateArray())
            {
                if (!int.TryParse(Str(p, "year"), out int year) || !p.TryGetProperty("round", out var r) || r.ValueKind != JsonValueKind.Number) continue;
                bool away = p.TryGetProperty("isTradedAway", out var ta) && ta.ValueKind == JsonValueKind.True;
                picks.Add(new CapPick(year, r.GetInt32(), Str(p, "team"), away));
            }
        // a list of { name "Last, First", born, pos, slug } as players with no contract
        List<CapPlayer> Listed(JsonElement arr, string section, string status)
        {
            var list = new List<CapPlayer>();
            foreach (var p in arr.EnumerateArray())
            {
                string full = Str(p, "name");
                if (full.Length == 0) continue;
                int comma = full.IndexOf(',');
                string last = comma > 0 ? full[..comma].Trim() : full, first = comma > 0 ? full[(comma + 1)..].Trim() : "";
                list.Add(new CapPlayer(first, last, ParseDate(Str(p, "born")), section, "", Array.Empty<CapContract>()) { Pos = Str(p, "pos"), Slug = Str(p, "slug"), Status = status });
            }
            return list;
        }
        // the AHL affiliate's AHL-contract players (pageProps.ahlClub.players)
        var ahl = pp.TryGetProperty("ahlClub", out var club) && club.ValueKind == JsonValueKind.Object
            && club.TryGetProperty("players", out var ap) && ap.ValueKind == JsonValueKind.Array ? Listed(ap, "ahl", "") : new List<CapPlayer>();
        // unsigned draft picks whose rights the team holds (pageProps.reserves): L.J. Mooney (MTL, at college), Egor Surin
        // (NSH, in the KHL); their slugs are often not what their names suggest ("l.j.-mooney")
        var reserves = pp.TryGetProperty("reserves", out var rs) && rs.ValueKind == JsonValueKind.Array ? Listed(rs, "reserve", "Prospect") : new List<CapPlayer>();
        return new CapTeam(abbr, name, players, picks) { AhlPlayers = ahl, Reserves = reserves, Buyouts = ParseBuyouts(pp, abbr) };
    }

    /// <summary>
    /// The dead cap lists' bought-out and terminated contracts: every season that carries a "buyout" object charged to
    /// this team counts with that object's capHit (a buyout whose cap charge has ended but is still being paid shows $0
    /// and adds nothing). Buried contracts and LTIR overage have no buyout object. The retained salary list is skipped:
    /// a retained contract that another team later bought out carries that team's buyout too (Utah, which retains part
    /// of Ekman-Larsson's deal from Arizona, lists Vancouver's buyout of it), and buyout.teamName says whose it is.
    /// </summary>
    private static List<CapBuyout> ParseBuyouts(JsonElement pp, string abbr)
    {
        var all = new List<CapBuyout>();
        if (!pp.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("dead cap", out var dead) || dead.ValueKind != JsonValueKind.Object) return all;
        foreach (var group in dead.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Array || group.Name == "retained salary") continue;
            foreach (var p in group.Value.EnumerateArray())
            {
                var seasons = new List<(int, long)>();
                if (p.TryGetProperty("contracts", out var cs) && cs.ValueKind == JsonValueKind.Array)
                    foreach (var c in cs.EnumerateArray())
                        if (c.TryGetProperty("details", out var ds) && ds.ValueKind == JsonValueKind.Array)
                            foreach (var d in ds.EnumerateArray())
                            {
                                string season = Str(d, "season");
                                if (season.Length < 4 || !int.TryParse(season[..4], out int start)) continue;
                                if (!d.TryGetProperty("buyout", out var b) || b.ValueKind != JsonValueKind.Object) continue;
                                string charged = Str(b, "teamName");
                                if (charged.Length > 0 && NhlApi.TeamAbbr(charged) != abbr) continue;
                                if (Money(Str(b, "capHit")) is { } hit && hit > 0) seasons.Add((start, hit));
                            }
                if (seasons.Count > 0) all.Add(new CapBuyout(Str(p, "name"), group.Name, seasons));
            }
        }
        return all;
    }

    private static CapPlayer ParsePlayer(JsonElement p, string section)
    {
        string full = Str(p, "name");
        if (full.Length == 0) return null;
        // "Gauthier, Cutter" -> First "Cutter", Last "Gauthier"
        int comma = full.IndexOf(',');
        string last = comma > 0 ? full[..comma].Trim() : full, first = comma > 0 ? full[(comma + 1)..].Trim() : "";
        var contracts = new List<CapContract>();
        if (p.TryGetProperty("contracts", out var cs) && cs.ValueKind == JsonValueKind.Array)
            foreach (var c in cs.EnumerateArray())
            {
                var seasons = new List<CapSeason>();
                if (c.TryGetProperty("details", out var ds) && ds.ValueKind == JsonValueKind.Array)
                    foreach (var d in ds.EnumerateArray())
                        if (ParseSeason(d) is { } s) seasons.Add(s);
                contracts.Add(new CapContract(Str(c, "type").Trim(), Str(c, "expiryStatus"), Str(c, "clauseDetails"), seasons));
            }
        var games = new Dictionary<string, int>();
        if (p.TryGetProperty("positions", out var ps) && ps.ValueKind == JsonValueKind.Object)
            foreach (var g in ps.EnumerateObject())
                if (g.Value.ValueKind == JsonValueKind.Number) games[g.Name] = g.Value.GetInt32();
        return new CapPlayer(first, last, ParseDate(Str(p, "born")), section, Str(p, "termsDetails"), contracts) { Pos = Str(p, "pos"), PositionGames = games, Slug = Str(p, "slug"), Status = Str(p, "status") };
    }

    private static CapSeason ParseSeason(JsonElement d)
    {
        // "2026-27" -> 2026
        string season = Str(d, "season");
        if (season.Length < 4 || !int.TryParse(season[..4], out int start)) return null;
        var retention = new List<(string, long)>();
        if (d.TryGetProperty("retention", out var ret) && ret.ValueKind == JsonValueKind.Object)
            foreach (var t in ret.EnumerateObject())
                if (t.Value.ValueKind == JsonValueKind.Object && Money(Str(t.Value, "capHit")) is { } hit)
                    retention.Add((t.Name, hit));
        int? size = d.TryGetProperty("clauseListSize", out var ls) && ls.ValueKind == JsonValueKind.Number ? ls.GetInt32() : null;
        return new CapSeason(start, Money(Str(d, "capHit")) ?? -1, Money(Str(d, "baseSalary")) ?? -1, Money(Str(d, "minorsSalary")),
                             Str(d, "clause").Trim(), size, retention);
    }

    /// <summary>"$13,500,000" -> 13500000. Blank or text ("ENTRY-LEVEL SLIDE") -> null.</summary>
    public static long? Money(string s)
    {
        string digits = (s ?? "").Replace("$", "").Replace(",", "").Trim();
        return digits.Length > 0 && digits.All(char.IsDigit) && long.TryParse(digits, out long v) ? v : null;
    }

    /// <summary>"Jan. 19, 2004", "May 5, 2003", "Sept. 3, 2001" -> the date; null when unreadable.</summary>
    public static DateTime? ParseDate(string s)
    {
        string t = (s ?? "").Replace(".", "").Replace("Sept ", "Sep ").Trim();
        // "Jul. 22, 2002", "July 22, 2002", and a few pages' "Mar 15 2002" (no comma: Cooper Gay)
        foreach (var fmt in new[] { "MMM d, yyyy", "MMMM d, yyyy", "MMM d yyyy", "MMMM d yyyy" })
            if (DateTime.TryParseExact(t, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        return null;
    }

    /// <summary>One NHL team's page (by abbreviation, "MIN"): its NHL contracts and its AHL affiliate's AHL contracts.</summary>
    public static async Task<CapTeam> TeamAsync(string abbr, CancellationToken ct = default)
    {
        var (a, slug) = Teams.FirstOrDefault(t => string.Equals(t.Abbr, abbr, StringComparison.OrdinalIgnoreCase));
        if (a == null) throw new ArgumentException($"No CapWages team for {abbr}.");
        return Parse(await GetAsync(Base + slug, ct), a);
    }

    public const string PlayerBase = "https://capwages.com/players/";

    /// <summary>
    /// A player's photo from his CapWages page (capwages.com/players/{slug}): its "headshot", which is NHL.com's own
    /// headshot (assets.nhle.com/mugs/…, a 336×336 PNG on a transparent background, the same shoot the game's portraits
    /// come from), else its "imageUrl" (an EliteProspects photo, not on a transparent background). Null when it has neither.
    /// </summary>
    public static async Task<(string Url, bool Nhl)?> HeadshotAsync(string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        using var doc = JsonDocument.Parse(await PlayerDataAsync(slug, ct));
        string head = Find(doc.RootElement, "headshot"), image = Find(doc.RootElement, "imageUrl");
        return head.Length > 0 ? (head, true) : image.Length > 0 ? (image, false) : null;
    }

    /// <summary>
    /// Where player pages are kept between syncs, on this machine only (never in the repo or shipped): one file per
    /// player, used again for <see cref="PlayerPageMaxAge"/>. A sync reads hundreds of AHL players' pages; most don't
    /// change from one day to the next. Null turns the cache off.
    /// </summary>
    public static string PlayerCacheDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HockeyModdingSuite", "cache", "capwages-players");
    public static TimeSpan PlayerPageMaxAge { get; set; } = TimeSpan.FromHours(20);

    /// <summary>True when the player's page is in the cache and fresh (reading it costs no request).</summary>
    public static bool IsCached(string slug) => CachePath(slug) is { } p && File.Exists(p) && DateTime.UtcNow - File.GetLastWriteTimeUtc(p) < PlayerPageMaxAge;

    private static string CachePath(string slug) =>
        PlayerCacheDir == null || string.IsNullOrWhiteSpace(slug) || slug.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? null : Path.Combine(PlayerCacheDir, slug + ".json");

    /// <summary>A player page's embedded data (its __NEXT_DATA__ JSON), from the cache when fresh, else downloaded and kept.</summary>
    public static async Task<string> PlayerDataAsync(string slug, CancellationToken ct = default)
    {
        string path = CachePath(slug);
        if (path != null && IsCached(slug))
            try { return await File.ReadAllTextAsync(path, ct); } catch (IOException) { }
        var m = Regex.Match(await GetAsync(PlayerBase + slug, ct), "<script id=\"__NEXT_DATA__\"[^>]*>(.*?)</script>", RegexOptions.Singleline);
        if (!m.Success) throw new InvalidOperationException($"CapWages' page for {slug} has no data block: the site's layout may have changed.");
        string json = m.Groups[1].Value;
        if (path != null)
            try { Directory.CreateDirectory(PlayerCacheDir); await File.WriteAllTextAsync(path, json, ct); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return json;
    }

    /// <summary>
    /// A player's bio from his CapWages page, for creating him: name, number (his NHL one; an AHL player's comes from
    /// the AHL roster instead), positions ("RW, LW" → RW with LW as the second, never a forward and a defenceman
    /// together), shot, height, weight, birth date and place ("Eden Prairie, Minnesota, USA", "Brunico, ITA") and his
    /// NHL draft. Null when the page has no player.
    /// </summary>
    public static async Task<PlayerBio> BioAsync(string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        using var doc = JsonDocument.Parse(await PlayerDataAsync(slug, ct));
        if (!doc.RootElement.TryGetProperty("props", out var props) || !props.TryGetProperty("pageProps", out var pp)
            || !pp.TryGetProperty("player", out var p) || p.ValueKind != JsonValueKind.Object) return null;
        string full = Str(p, "name");
        int comma = full.IndexOf(',');
        string last = comma > 0 ? full[..comma].Trim() : full, first = comma > 0 ? full[(comma + 1)..].Trim() : "";
        string shoots = Str(p, "shootsCatches");
        var listed = Str(p, "pos").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        string official = Str(p, "officialPosition");
        int Code(string pos) => pos switch
        {
            "C" => 0, "LW" or "L" => 1, "RW" or "R" => 2, "LD" => 3, "RD" => 4, "G" => 5,
            "D" => shoots == "R" ? 4 : 3, _ => -1,
        };
        int primary = Code(official.Length > 0 ? official : listed.FirstOrDefault() ?? "");
        // "D" as the official position and "LD" listed first: the listed side wins
        if (official == "D" && listed.FirstOrDefault() is "LD" or "RD") primary = Code(listed[0]);
        static bool Forward(int c) => c is 0 or 1 or 2;
        int secondary = listed.Select(Code).FirstOrDefault(c => c >= 0 && c != primary && c != 5 && primary != 5 && Forward(c) == Forward(primary), -1);
        string position = primary switch { 0 => "C", 1 => "LW", 2 => "RW", 3 or 4 => "D", 5 => "G", _ => "" };
        int Lead(string s) => int.TryParse(Regex.Match(s ?? "", @"^\d+").Value, out int v) ? v : 0;
        string FirstOf(string name) => p.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0 && a[0].ValueKind == JsonValueKind.String ? a[0].GetString() : Str(p, name);
        var hm = Regex.Match(FirstOf("height"), "(\\d+)'\\s*(\\d+)");
        int height = hm.Success ? int.Parse(hm.Groups[1].Value) * 12 + int.Parse(hm.Groups[2].Value) : 0;
        int weight = Lead(FirstOf("weight"));
        int Num(string name) => p.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetInt32() : int.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : "", out int x) ? x : 0 : 0;
        int year = Num("draft_year");
        var draft = year > 0 && Str(p, "drafted_by").Length > 0 ? new DraftInfo(year, Str(p, "drafted_by"), Num("draft_round"), Num("drafted_overall")) : DraftInfo.Undrafted;
        var (city, country, region) = BirthPlace(Str(p, "birthplace"), Str(p, "nationality"));
        return new PlayerBio(first, last, Num("sweaterNumber") is var n and > 0 ? n : -1, position, shoots, height, weight,
            ParseDate(Str(p, "born")), city, draft, "CapWages")
        { Primary = primary, Secondary = secondary, BirthCountry = country, BirthRegion = region };
    }

    /// <summary>"Eden Prairie, Minnesota, USA" → (Eden Prairie, USA, Minnesota); "Brunico, ITA" → (Brunico, ITA, "");
    /// "Toronto, Ontario, Canada" → (Toronto, CAN, Ontario). The country falls back on his nationality.</summary>
    public static (string City, string Country, string Region) BirthPlace(string place, string nationality)
    {
        var parts = (place ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return ("", Country(nationality), "");
        string country = parts.Length >= 2 ? Country(parts[^1]) : "";
        if (country.Length == 0) country = Country(nationality);
        string region = parts.Length >= 3 ? parts[1] : "";
        // "Calgary, AB" with no country: the province or state says which
        if (parts.Length == 2 && country.Length == 0 || parts.Length == 2 && Country(parts[1]).Length == 0)
        {
            var (c2, r2) = MissingPlayers.BirthCountry(place);
            if (c2.Length > 0) { country = c2; region = r2; }
        }
        return (parts[0], country, region);
    }

    private static string Country(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return "";
        var (c, _) = MissingPlayers.BirthCountry("x, " + s);
        if (c.Length > 0 && !(s.Length == 2)) return c;
        return s.Length == 3 && s.All(char.IsLetter) ? s.ToUpperInvariant() : "";
    }

    /// <summary>
    /// A player's CapWages page: name, birth date, status ("Minor", "Junior", "Prospect" for an unsigned pick whose rights a
    /// team holds, "Inactive"…), the NHL team he belongs to ("" when none) and his stats rows in the page's order: oldest
    /// season first, and within a season in the order he played for each team ("2026-27 Anaheim Ducks NHL", then
    /// "2026-27 Saginaw Spirit OHL"). Null when CapWages has no page by that slug.
    /// </summary>
    public static async Task<CapPage> PageAsync(string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        string json;
        try { json = await PlayerDataAsync(slug, ct); }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("props", out var props) || !props.TryGetProperty("pageProps", out var pp)
            || !pp.TryGetProperty("player", out var pl) || pl.ValueKind != JsonValueKind.Object) return null;
        var list = new List<CapStat>();
        if (pl.TryGetProperty("stats", out var st) && st.ValueKind == JsonValueKind.Array)
            foreach (var s in st.EnumerateArray())
            {
                int gp = s.TryGetProperty("gp", out var g) && g.ValueKind == JsonValueKind.Number ? g.GetInt32() : 0;
                if (Str(s, "team").Length > 0) list.Add(new CapStat(Str(s, "season"), Str(s, "team"), Str(s, "league"), gp));
            }
        return new CapPage(slug, Str(pl, "name"), ParseDate(Str(pl, "born")), Str(pl, "status"), Str(pl, "currentTeamTricode"), list);
    }

    /// <summary>The slug CapWages most likely gives a player: "Michael Hage" → "michael-hage" (accents, dots and
    /// apostrophes dropped). Only a guess: a page found by it is checked against his birth date before it's believed.</summary>
    public static string GuessSlug(string name)
    {
        string plain = new string((name ?? "").Normalize(System.Text.NormalizationForm.FormD)
            .Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        plain = Regex.Replace(plain.ToLowerInvariant().Replace(".", "").Replace("'", "").Replace("’", ""), "[^a-z0-9]+", "-").Trim('-');
        return plain;
    }

    /// <summary>The first string value named <paramref name="name"/> anywhere under <paramref name="e"/>; "" when none.</summary>
    private static string Find(JsonElement e, string name)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == name && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } s) return s;
                    if (Find(p.Value, name) is { Length: > 0 } inner) return inner;
                }
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray())
                    if (Find(x, name) is { Length: > 0 } inner) return inner;
                break;
        }
        return "";
    }

    /// <summary>Downloads a file (a headshot) as bytes.</summary>
    public static async Task<byte[]> BytesAsync(string url, CancellationToken ct = default)
    {
        using var r = await Http.GetAsync(url, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadAsByteArrayAsync(ct);
    }

    private static async Task<string> GetAsync(string url, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var r = await Http.GetAsync(url, ct);
            if ((int)r.StatusCode == 429 && attempt < 5)
            {
                await Task.Delay(r.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 << attempt), ct);
                continue;
            }
            r.EnsureSuccessStatusCode();
            return await r.Content.ReadAsStringAsync(ct);
        }
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
}
