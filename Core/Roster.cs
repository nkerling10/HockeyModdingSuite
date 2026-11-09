namespace NHL27Kit.Core;

/// <summary>A team of the roster (exhibitionteams), by its index.</summary>
public sealed record RosterTeam(int Index, string Name, string Abbr, int PrimaryColor);

/// <summary>
/// A player of NHL 27's roster: his bio row (exhibitionplayerbiotable), his ratings row (exhibitionskaterai or
/// exhibitiongoalieai, by game_id) and his roster row (exhibitionrostertable, through exhibitionplayers' index).
/// </summary>
public sealed class RosterPlayer
{
    public int GameId { get; init; }
    public int BioRow { get; init; }
    public T3Table RatingsTable { get; init; }
    public int RatingsRow { get; init; } = -1;
    public int RosterRow { get; init; } = -1;
    public int PlayerIndex { get; init; } = -1;
    public string FirstName { get; init; }
    public string LastName { get; init; }
    public int Team { get; init; }
    public string TeamAbbr { get; init; }
    public int Position { get; init; }
    /// <summary>The primary position, on the 0-5 scale (0=C, 1=LW, 2=RW, 3=LD, 4=RD, 5=G).</summary>
    public int PositionPrimary { get; init; }
    /// <summary>The secondary position on the same scale, or -1 for a single-position player.</summary>
    public int PositionSecondary { get; init; }
    public int Overall { get; init; }
    public int Jersey { get; init; } = -1;
    public int ArtId { get; init; }
    public bool HasPortrait { get; init; }
    public bool IsGoalie => RatingsTable?.Name == Roster.GoalieTable;

    public string Name => $"{FirstName} {LastName}".Trim();
    public string PositionName => Roster.PositionName(Position);
    /// <summary>Primary + secondary positions, e.g. "C/LW" or "RD" — the legacy PositionName collapses LD/RD to D.</summary>
    public string PositionDisplay => Roster.PositionDisplay(PositionPrimary, PositionSecondary);
    /// <summary>The player's portrait texture (ui/textures/common/playerportraits/p + art id).</summary>
    public string Portrait => $"ui/textures/common/playerportraits/p{ArtId}";
}

/// <summary>The roster tables of nhl_game.db, joined. Built from a database read just now from the game.</summary>
public sealed class Roster
{
    public const string Database = "gamemodes/database/t3db/nhl_game.db";
    public const string BioTable = "exhibitionplayerbiotable";
    public const string SkaterTable = "exhibitionskaterai";
    public const string GoalieTable = "exhibitiongoalieai";
    public const string RosterTable = "exhibitionrostertable";
    public const string PlayersTable = "exhibitionplayers";
    public const string TeamsTable = "exhibitionteams";

    public T3Db Db { get; }
    public List<RosterPlayer> Players { get; } = new();
    public Dictionary<int, RosterTeam> Teams { get; } = new();

    public static string PositionName(int p) => p switch { 0 => "C", 1 => "LW", 2 => "RW", 3 => "D", 4 => "G", _ => "?" };

    /// <summary>A primary/secondary position on the 0-5 scale: 0=C, 1=LW, 2=RW, 3=LD, 4=RD, 5=G. Unlike the legacy
    /// <c>position</c> field, this scale splits defence into left and right — a right-shot defenceman is 4 (RD), not 3.</summary>
    public static string PositionNameFull(int v) => v switch { 0 => "C", 1 => "LW", 2 => "RW", 3 => "LD", 4 => "RD", 5 => "G", _ => "?" };

    /// <summary>The legacy <c>position</c> value for a primary position: LD and RD both collapse to D (3), G (5) to 4.</summary>
    public static int CollapsedPosition(int primary) => primary switch { 3 or 4 => 3, 5 => 4, _ => primary };

    /// <summary>"C/LW", "RD", "G": the primary position, with the secondary after a slash when the player has one.</summary>
    public static string PositionDisplay(int primary, int secondary) => secondary >= 0 ? $"{PositionNameFull(primary)}/{PositionNameFull(secondary)}" : PositionNameFull(primary);

    public Roster(T3Db db)
    {
        Db = db;
        var teams = db.Table(TeamsTable);
        foreach (int r in Rows(teams))
        {
            int idx = Int(teams, r, "index");
            Teams[idx] = new RosterTeam(idx, Str(teams, r, "teamname"), Str(teams, r, "abbrname"), Int(teams, r, "presprimarycolor"));
        }

        var ratings = new Dictionary<int, (T3Table, int)>();
        foreach (var name in new[] { SkaterTable, GoalieTable })
        {
            var t = db.Table(name);
            foreach (int r in Rows(t)) ratings[Int(t, r, "game_id")] = (t, r);
        }
        // roster row by player id: exhibitionrostertable.playerindex → exhibitionplayers.index → playerid
        var players = db.Table(PlayersTable);
        var idOfIndex = new Dictionary<int, int>();
        var versionOfIndex = new Dictionary<int, int>();
        var indexOfId = new Dictionary<int, int>();
        bool hasVersion = Field(players, "table") != null;
        foreach (int r in Rows(players))
        {
            int index = Int(players, r, "index"), id = Int(players, r, "playerid");
            idOfIndex[index] = id;
            versionOfIndex[index] = hasVersion ? Int(players, r, "table") : 0;
            indexOfId.TryAdd(id, index);
        }
        // A player can have several roster rows: one per version of him (club, All-Star, alumni, national and event teams),
        // in any order (a shared roster's order isn't the game's). His row is the one on his bio team (the club; NHL Sync
        // keeps it current); else his club version's first row; else his first row.
        var roster = db.Table(RosterTable);
        var rowsOf = new Dictionary<int, List<int>>();
        foreach (int r in Rows(roster))
            if (idOfIndex.TryGetValue(Int(roster, r, "playerindex"), out int id))
                (rowsOf.TryGetValue(id, out var l) ? l : rowsOf[id] = new List<int>()).Add(r);
        int RosterRowOf(int id, int bioTeam)
        {
            if (!rowsOf.TryGetValue(id, out var rows)) return -1;
            foreach (int r in rows) if (Int(roster, r, "team") == bioTeam) return r;
            foreach (int r in rows) if (versionOfIndex.GetValueOrDefault(Int(roster, r, "playerindex")) == 0) return r;
            return rows[0];
        }

        var bio = db.Table(BioTable);
        foreach (int r in Rows(bio))
        {
            int id = Int(bio, r, "game_id");
            ratings.TryGetValue(id, out var rt);
            int team = Int(bio, r, "team");
            int rosterRow = RosterRowOf(id, team);
            Players.Add(new RosterPlayer
            {
                GameId = id, BioRow = r,
                RatingsTable = rt.Item1, RatingsRow = rt.Item1 == null ? -1 : rt.Item2,
                RosterRow = rosterRow,
                // the index of the version his row is for (his club self), else his first
                PlayerIndex = rosterRow >= 0 ? Int(roster, rosterRow, "playerindex") : indexOfId.GetValueOrDefault(id, -1),
                FirstName = Str(bio, r, "firstname"), LastName = Str(bio, r, "lastname"),
                Team = team, TeamAbbr = Teams.TryGetValue(team, out var tm) ? tm.Abbr : "FA",
                Position = Int(bio, r, "position"),
                // the dual-position fields; a database without them falls back to the legacy position, no secondary
                PositionPrimary = Field(bio, "positionprimary") is null ? Int(bio, r, "position") : Int(bio, r, "positionprimary"),
                PositionSecondary = Field(bio, "positionsecondary") is null ? -1 : Int(bio, r, "positionsecondary"),
                Overall = rt.Item1 == null ? 0 : Int(rt.Item1, rt.Item2, "skill_overall"),
                Jersey = rosterRow < 0 ? -1 : Int(roster, rosterRow, "jersey"),
                ArtId = Int(bio, r, "artid"), HasPortrait = Int(bio, r, "hasportrait") == 1,
            });
        }
    }

    private IEnumerable<int> Rows(T3Table t)
    {
        for (int r = 0; r < t.WrittenRecords; r++)
            if (!Db.IsInvalid(t, r)) yield return r;
    }

    public static T3Field Field(T3Table t, string name) => t.Fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private int Int(T3Table t, int r, string f) => Field(t, f) is { } x ? (int)Db.Get(t, r, x) : 0;
    private string Str(T3Table t, int r, string f) => Field(t, f) is { } x ? (string)Db.Get(t, r, x) : "";

    /// <summary>The player with this game id in a (fresh) database: the rows don't move, but they are looked up again.</summary>
    public RosterPlayer Find(int gameId) => Players.FirstOrDefault(p => p.GameId == gameId);
}
