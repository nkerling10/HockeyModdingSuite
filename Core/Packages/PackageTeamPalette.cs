using System.Globalization;
using System.Text.Json;

namespace NHL27Kit.Core.Packages;

/// <summary>
/// The teams' palettes (team setting "palette"): rows of stockteamcolorstable, one per colour id (0 = primary), for
/// the team's index. The game's NHL and AHL teams have one (2-5 colours: Boston gold, black; Detroit white, red);
/// created teams have none, and their crowd wears the colours of the team whose kit folder holds their home uniform.
/// "palette": true takes color_1..3 from the team data file; a list of "#RRGGBB" sets them outright.
/// The table's index field is 8 bits and the game ships 221 of its 253 rows, so only ~32 colours fit (the table is not
/// grown: an index past 255 can't be stored). Teams that don't fit are skipped with a warning.
/// </summary>
public static class PackageTeamPalette
{
    public const string Table = "stockteamcolorstable";

    /// <summary>Call inside the install's database batch, after the teams exist.</summary>
    public static void Apply(T3Db db, PackageSpec spec, PackageInstaller.Result r)
    {
        var wanted = spec.AllTeams.Select(t => (t, Colors: Colors(t))).Where(x => x.Colors.Count > 0).OrderBy(x => x.t.Index).ToList();
        if (wanted.Count == 0) return;
        var t = db.Table(Table) ?? throw new InvalidDataException($"No {Table} table.");
        var f = t.Fields.ToDictionary(x => x.Name);
        int maxIndex = (1 << f["index"].Depth) - 1;
        // a team that already has rows (a game team the package reuses) keeps them; it gets no second set
        var has = Enumerable.Range(0, t.WrittenRecords).Where(i => !db.IsInvalid(t, i)).Select(i => Convert.ToInt32(db.Get(t, i, f["team"]))).ToHashSet();
        int nextIndex = Enumerable.Range(0, t.WrittenRecords).Where(i => !db.IsInvalid(t, i)).Select(i => Convert.ToInt32(db.Get(t, i, f["index"]))).DefaultIfEmpty(-1).Max() + 1;
        var template = db.RecordBytes(t, 0);
        var done = new List<string>();
        var skipped = new List<string>();
        foreach (var (team, colors) in wanted)
        {
            if (has.Contains(team.Index)) { skipped.Add($"{team.Code} (has a palette)"); continue; }
            if (t.WrittenRecords + colors.Count > t.Records || nextIndex + colors.Count - 1 > maxIndex) { skipped.Add($"{team.Code} (no room)"); continue; }
            int first = t.WrittenRecords;
            db.SetWritten(t, first + colors.Count);
            for (int c = 0; c < colors.Count; c++)
            {
                db.WriteRecord(t, first + c, (byte[])template.Clone());
                db.Set(t, first + c, f["index"], nextIndex++);
                db.Set(t, first + c, f["color"], colors[c]);
                db.Set(t, first + c, f["team"], team.Index);
                db.Set(t, first + c, f["colorid"], c);
            }
            done.Add($"{team.Code} {string.Join("/", colors.Select(TeamEditor.Hex))}");
        }
        if (done.Count > 0) r.Lines.Add($"team palettes ({Table}, {t.WrittenRecords}/{t.Records} rows): {string.Join(", ", done)}");
        if (skipped.Count > 0) r.Warnings.Add($"team palettes skipped: {string.Join(", ", skipped)}");
    }

    /// <summary>The team's palette colours, primary first (duplicates dropped, at most 5); empty without a "palette" setting.</summary>
    private static List<int> Colors(TeamSpec t)
    {
        IEnumerable<string> hex = t.Palette switch
        {
            { ValueKind: JsonValueKind.True } => new[] { t.Data?.Color1 ?? t.Primary, t.Data?.Color2 ?? t.Secondary, t.Data?.Color3 },
            { ValueKind: JsonValueKind.Array } a => a.EnumerateArray().Select(x => x.GetString()),
            _ => Array.Empty<string>(),
        };
        var list = new List<int>();
        foreach (var h in hex)
        {
            string s = (h ?? "").Trim().TrimStart('#');
            if (s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) && !list.Contains(v)) list.Add(v);
        }
        return list.Take(5).ToList();
    }
}
