using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NHL27Kit.Core;

/// <summary>What a roster pack says about itself (manifest.json).</summary>
public sealed class RosterPackManifest
{
    public int Format { get; set; } = 1;
    /// <summary>Who made the roster ("Vasy / TacTix").</summary>
    public string Source { get; set; } = "";
    /// <summary>When the roster was saved, from the save's own metadata (yyyy-MM-dd HH:mm:ss), when known.</summary>
    public string RosterDate { get; set; } = "";
    /// <summary>The game version the roster was saved with ("1.051"), when known.</summary>
    public string GameVersion { get; set; } = "";
    public string Exported { get; set; } = "";
    public List<RosterPackTable> Tables { get; set; } = new();
}

public sealed class RosterPackTable
{
    public string Name { get; set; } = "";
    public int Rows { get; set; }
    public string File { get; set; } = "";
}

/// <summary>
/// A roster pack: a shared roster as plain files HMS reads for NHL Sync's step 1, instead of the game save it came from.
/// A folder with manifest.json and one CSV per table (tables\{table}.csv): a header row of the game's field names, then
/// one line per record in the save's order. Values are written as the game stores them (integers as numbers, strings as
/// text, floats round-trip), so a pack can be read, searched, compared and edited by hand. Importing fills each table
/// field by field by name, so a game update that moves or adds fields doesn't break a pack.
/// </summary>
public static class RosterPack
{
    public const string ManifestFile = "manifest.json";
    public const string TablesDir = "tables";
    /// <summary>The column a table with deleted records gets: 1 for a record the game has marked deleted.</summary>
    public const string DeletedColumn = "_deleted";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Writes every table of <paramref name="db"/> (a roster save's database) that has rows, or that the game's
    /// database has (so an empty table empties the game's), into a pack folder.</summary>
    public static RosterPackManifest Export(T3Db db, string dir, RosterPackManifest manifest)
    {
        Directory.CreateDirectory(Path.Combine(dir, TablesDir));
        manifest.Tables.Clear();
        manifest.Exported = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        foreach (var t in db.Tables.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (t.StringsLength > 0) throw new NotSupportedException($"{t.Name} keeps strings in a separate area, which a pack can't hold yet.");
            string file = $"{TablesDir}/{t.Name}.csv";
            int rows = 0;
            // the game marks a deleted record with a flag rather than removing it; a _deleted column keeps those
            bool anyDeleted = Enumerable.Range(0, t.WrittenRecords).Any(r => db.IsInvalid(t, r));
            using (var w = new StreamWriter(Path.Combine(dir, file), false, new UTF8Encoding(false)))
            {
                w.WriteLine(string.Join(",", t.Fields.Select(f => Quote(f.Name)).Concat(anyDeleted ? new[] { DeletedColumn } : Array.Empty<string>())));
                for (int r = 0; r < t.WrittenRecords; r++)
                {
                    var cells = t.Fields.Select(f => Quote(Text(db.Get(t, r, f))));
                    if (anyDeleted) cells = cells.Append(db.IsInvalid(t, r) ? "1" : "0");
                    w.WriteLine(string.Join(",", cells));
                    rows++;
                }
            }
            manifest.Tables.Add(new RosterPackTable { Name = t.Name, Rows = rows, File = file });
        }
        File.WriteAllText(Path.Combine(dir, ManifestFile), JsonSerializer.Serialize(manifest, Json));
        return manifest;
    }

    /// <summary>A pack's manifest; throws when <paramref name="dir"/> isn't a pack.</summary>
    public static RosterPackManifest Manifest(string dir)
    {
        string path = Path.Combine(dir, ManifestFile);
        if (!File.Exists(path)) throw new FileNotFoundException("Not a roster pack: there's no manifest.json in this folder.", path);
        return JsonSerializer.Deserialize<RosterPackManifest>(File.ReadAllText(path)) ?? throw new InvalidDataException("manifest.json is empty.");
    }

    public static bool IsPack(string dir) => File.Exists(Path.Combine(dir, ManifestFile));

    /// <summary>
    /// The pack applied to a copy of <paramref name="game"/>: the game's database with every pack table's rows replaced
    /// by the pack's. Used to plan and preview without touching the game's own database.
    /// </summary>
    public static T3Db ToDb(string dir, T3Db game, string metaXml, Action<string, int, int> progress = null)
    {
        var copy = new T3Db((byte[])game.Data.Clone(), metaXml);
        Import(dir, copy, progress);
        return copy;
    }

    /// <summary>
    /// Replaces each table the pack holds with the pack's rows, field by field by name (a field the pack doesn't name is
    /// left 0). Tables the pack doesn't hold are left as they are. Returns the tables written; throws before writing a
    /// table whose rows don't fit, or with a value its field can't hold.
    /// </summary>
    /// <param name="progress">Reports each table as it's imported: (table, number, of).</param>
    /// <param name="only">Only these tables (a group of the pack's, for testing which part of a roster a game version can take); null for all.</param>
    public static List<string> Import(string dir, T3Db db, Action<string, int, int> progress = null, ISet<string> only = null)
    {
        var manifest = Manifest(dir);
        var written = new List<string>();
        using var batch = db.Batch();
        int n = 0;
        foreach (var pt in manifest.Tables)
        {
            progress?.Invoke(pt.Name, ++n, manifest.Tables.Count);
            if (only != null && !only.Contains(pt.Name)) continue;
            var t = db.Table(pt.Name);
            if (t == null) continue;
            var lines = ReadCsv(Path.Combine(dir, pt.File)).ToList();
            if (lines.Count == 0) continue;
            var header = lines[0];
            int rows = lines.Count - 1;
            if (rows > t.Records) throw new InvalidDataException($"{pt.Name}: the pack has {rows} rows, the game's table has room for {t.Records}.");
            var cols = header.Select((name, i) => (Field: t.Fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)), i))
                .Where(x => x.Field != null).ToList();
            int deletedCol = header.FindIndex(h => h == DeletedColumn);
            db.SetWritten(t, rows);
            var blank = new byte[t.RecordSize];
            for (int r = 0; r < rows; r++)
            {
                db.WriteRecord(t, r, blank);
                var line = lines[r + 1];
                var strings = new List<(T3Field F, byte[] Bytes)>();
                foreach (var (f, i) in cols)
                {
                    string v = i < line.Count ? line[i] : "";
                    if (f.Type == T3FieldType.String)
                    {
                        // written into the row below: the game fills a fixed string to its full width with no terminator
                        // (a 25-byte last name), which the editor's Set, keeping room for one, refuses
                        var b = Encoding.UTF8.GetBytes(v);
                        if (b.Length > f.Depth / 8) throw new InvalidDataException($"{pt.Name} row {r} {f.Name} = \"{v}\": {b.Length} bytes, the field holds {f.Depth / 8}.");
                        strings.Add((f, b));
                        continue;
                    }
                    object value = f.Type switch
                    {
                        T3FieldType.Integer => int.Parse(v, CultureInfo.InvariantCulture),
                        T3FieldType.Float => float.Parse(v, CultureInfo.InvariantCulture),
                        _ => v,
                    };
                    try { db.Set(t, r, f, value); }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException) { throw new InvalidDataException($"{pt.Name} row {r} {f.Name} = \"{v}\": {e.Message}", e); }
                }
                bool deleted = deletedCol >= 0 && deletedCol < line.Count && line[deletedCol] == "1";
                if (strings.Count > 0 || deleted)
                {
                    var bytes = db.RecordBytes(t, r);
                    foreach (var (f, b) in strings)
                    {
                        int at = f.BitOffset / 8, width = f.Depth / 8;
                        Array.Clear(bytes, at, width);
                        b.CopyTo(bytes, at);
                    }
                    // the deleted flag: the record's last byte's top bit
                    if (deleted) bytes[^1] |= 0x80;
                    db.WriteRecord(t, r, bytes);
                }
            }
            written.Add(pt.Name);
        }
        return written;
    }

    private static string Text(object v) => v switch
    {
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        null => "",
        _ => v.ToString(),
    };

    private static string Quote(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 || s.Length > 0 && (s[0] == ' ' || s[^1] == ' ') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>The records of a CSV file (RFC 4180: quoted fields may hold commas, quotes and line breaks).</summary>
    private static IEnumerable<List<string>> ReadCsv(string path)
    {
        string text = File.ReadAllText(path, Encoding.UTF8);
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                yield return row;
                row = new List<string>();
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); yield return row; }
    }
}
