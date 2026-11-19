using System.Text;

namespace NHL27Kit.Core;

/// <summary>
/// One team's roster as a CSV for rating comparisons (e.g. against Franchise Hockey Manager): name, birthday,
/// positions, potential (the game's tier text, its tier number and confidence value), overall, then every skater
/// attribute and every goalie attribute. A skater's goalie columns are 0 and a goalie's skater columns are 0.
/// A player is on the team by his bio team or his roster row's team (a school player's bio team is -1).
/// </summary>
public static class RosterExport
{
    // TXT_GROWTH_TIER_FWD_n / _DEF_n / _GOA_n (loc/eng_us.db): the potential text a player card shows for growthtier n
    private static readonly string[] Fwd = { "Franchise", "Elite", "Top 6 F", "Top 9 F", "Bottom 6 F", "AHL Top 6 F", "AHL Bottom 6 F", "AHL 4th Line F", "AHL Extra F", "AHL Other FWD" };
    private static readonly string[] Def = { "Franchise", "Elite", "Top 4 D", "Top 6 D", "7th D", "AHL Top 2 D", "AHL Top 4 D", "AHL Top 6 D", "AHL 7th D", "AHL Other D" };
    private static readonly string[] Goa = { "Franchise", "Elite", "Starter", "Fringe Starter", "Backup", "AHL Starter", "AHL Fringe G", "AHL Backup", "AHL Extra G", "AHL Other G" };

    /// <summary>The potential text for a growth tier at a primary position (0-5 scale), as the game's cards show it.</summary>
    public static string PotentialText(int tier, int primary)
    {
        var set = primary == 5 ? Goa : primary is 3 or 4 ? Def : Fwd;
        return tier >= 0 && tier < set.Length ? set[tier] : "Unknown";
    }

    /// <summary>The confidence a player card shows next to the potential, from growthletter. Seen in game (2026-11-19):
    /// 0 High (McDavid), 1 Medium (Crosby), 2 Medium (Kiviranta), 3 Medium (Gibson, Harris), 4 Low (Letang, Malkin);
    /// 5 not seen yet, taken as Low.</summary>
    public static string Confidence(int letter) => letter switch { 0 => "High", 1 or 2 or 3 => "Medium", 4 or 5 => "Low", _ => "" };

    /// <summary>The attribute fields (c_*) of a ratings table, in table order; overall and the raw c_potential aren't
    /// attributes (overall has its own column).</summary>
    private static List<T3Field> Attributes(T3Table t, string[] order) => t == null ? new List<T3Field>()
        : t.Fields.Where(f => f.Name.StartsWith("c_", StringComparison.OrdinalIgnoreCase) && f.Name is not ("c_overall" or "c_potential"))
            .OrderBy(f => Array.IndexOf(order, f.Name) is var i && i >= 0 ? i : order.Length).ThenBy(f => f.Name).ToList();

    // skating, shooting, hands, defence, physical / sense; then anything new at the end
    private static readonly string[] SkaterOrder =
    {
        "c_speed", "c_acceleration", "c_agility", "c_balance", "c_endurance",
        "c_wristshotpower", "c_wristaccuracy", "c_slapshotpower", "c_slapaccuracy",
        "c_puckcontrol", "c_deking", "c_passing", "c_handeye", "c_offawareness",
        "c_defawareness", "c_stickchecking", "c_bodychecking", "c_shotblocking", "c_faceoffs",
        "c_strength", "c_poise", "c_discipline", "c_aggression", "c_durability", "c_fightingskill", "c_fighting",
    };
    private static readonly string[] GoalieOrder =
    {
        "c_gsh", "c_gsl", "c_ssh", "c_ssl", "c_5hole",
        "c_speed", "c_agility", "c_positioning", "c_breakaway", "c_reboundcontrol", "c_shotrecover", "c_vision",
        "c_pokecheck", "c_passing", "c_puckplayfrequency", "c_poise", "c_aggression", "c_endurance", "c_durability",
        "c_consistency", "c_paddledown", "c_fighting",
    };

    public static int PlayerCount(Roster roster, int team) => Members(roster, team).Count();

    private static IEnumerable<RosterPlayer> Members(Roster roster, int team) =>
        roster.Players.Where(p => p.RatingsTable != null && p.GameId < PlayerCreator.SpecialIdStart && (p.Team == team || p.RowTeam == team));

    /// <summary>The CSV text (header row first). <paramref name="label"/> names an attribute field ("c_gsh" -> "Glove high").</summary>
    public static string TeamCsv(T3Db db, Roster roster, int team, Func<string, string> label)
    {
        var skT = db.Table(Roster.SkaterTable);
        var goT = db.Table(Roster.GoalieTable);
        var sk = Attributes(skT, SkaterOrder);
        var go = Attributes(goT, GoalieOrder);
        var bio = db.Table(Roster.BioTable);
        int B(RosterPlayer p, string f) => Roster.Field(bio, f) is { } x ? Convert.ToInt32(db.Get(bio, p.BioRow, x)) : 0;
        int R(RosterPlayer p, string f) => Roster.Field(p.RatingsTable, f) is { } x ? Convert.ToInt32(db.Get(p.RatingsTable, p.RatingsRow, x)) : 0;

        var sb = new StringBuilder();
        var head = new List<string> { "Name", "Birthday", "Position", "Potential", "Confidence", "Potential tier", "Confidence value", "Overall" };
        head.AddRange(sk.Select(f => "Skater: " + label(f.Name)));
        head.AddRange(go.Select(f => "Goalie: " + label(f.Name)));
        sb.AppendLine(string.Join(",", head.Select(Csv)));

        // forwards, defence, goalies; best first
        int Group(RosterPlayer p) => p.PositionPrimary == 5 ? 2 : p.PositionPrimary is 3 or 4 ? 1 : 0;
        foreach (var p in Members(roster, team).OrderBy(Group).ThenByDescending(p => p.Overall).ThenBy(p => p.LastName))
        {
            int y = B(p, "year") + 1900, m = B(p, "month") + 1, d = B(p, "day") + 1;
            string born = y > 1900 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m) ? $"{y:D4}-{m:D2}-{d:D2}" : "";
            int tier = R(p, "growthtier");
            var row = new List<string>
            {
                p.Name, born, p.PositionDisplay, PotentialText(tier, p.PositionPrimary), Confidence(R(p, "growthletter")), tier.ToString(), R(p, "growthletter").ToString(),
                (Roster.Field(p.RatingsTable, "skill_overall") != null ? R(p, "skill_overall") : p.Overall).ToString(),
            };
            bool goalie = p.RatingsTable == goT;
            row.AddRange(sk.Select(f => goalie ? "0" : Convert.ToInt32(db.Get(skT, p.RatingsRow, f)).ToString()));
            row.AddRange(go.Select(f => goalie ? Convert.ToInt32(db.Get(goT, p.RatingsRow, f)).ToString() : "0"));
            sb.AppendLine(string.Join(",", row.Select(Csv)));
        }
        return sb.ToString();
    }

    private static string Csv(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
