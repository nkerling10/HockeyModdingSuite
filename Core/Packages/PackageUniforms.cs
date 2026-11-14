using System.Text.Json;
using System.Text.Json.Serialization;

namespace NHL27Kit.Core.Packages;

/// <summary>
/// A package team's uniforms (recipe "uniforms", e.g. "ncaa/uniforms"): &lt;folder&gt;/&lt;team code&gt;/&lt;kit&gt;/uniform.json,
/// exported by the app's Jersey Studio ("Export to mod"). Each is a design rendered for one kit folder of the game that
/// no team's menu shows (the folder it was exported for stays that kit's for good): its textures as PNGs, its garments'
/// stamps (where the game puts logos and lettering) as the new EBX bytes, the menu name and light/dark.
/// The installer writes the textures and stamps into that folder and makes the team's jersey menu (stockteamjerseys)
/// these uniforms, home and away first. A team with none keeps the template's jerseys.
/// </summary>
public sealed class PackageUniform
{
    public const string FileName = "uniform.json";

    /// <summary>The kit folder the design is rendered for: content/character/skater/league/&lt;league&gt;/&lt;team&gt;/&lt;brand&gt;/&lt;kit&gt;.</summary>
    public string Folder { get; set; }
    /// <summary>The art id of that folder's team (what the menu slot's teamartid must be).</summary>
    public int ArtId { get; set; }
    /// <summary>Menu name: 0 Home, 1 Away, 2 Third.</summary>
    public int Label { get; set; }
    public bool Light { get; set; }
    /// <summary>One of the team's two default uniforms (home and away are, unless set).</summary>
    public bool? Default { get; set; }
    /// <summary>Kit texture slot (jersey_base_color, jersey_color, font_color, pant_base_color…) → PNG file in this folder.</summary>
    public Dictionary<string, string> Textures { get; set; } = new();
    /// <summary>Garment (jersey, pant) → its stamp EBX, rewritten for the design, in this folder.</summary>
    public Dictionary<string, string> Stamps { get; set; } = new();
    /// <summary>Helmet and glove zone colours (0xRRGGBB, 15 each; helmet 1-3 shell, trim, maker's logo; gloves 1-12):
    /// the team's equipment rows for this uniform (stockteamequipmenttable, by its kit's art id, era and variant).
    /// Null: the template's.</summary>
    public int[] Helmet { get; set; }
    public int[] Gloves { get; set; }
    /// <summary>What it was made from, for the report.</summary>
    public string From { get; set; }
    public DateTime Exported { get; set; }

    /// <summary>The kit's name (home, away, third…): its folder's name.</summary>
    [JsonIgnore] public string Name { get; set; }
    [JsonIgnore] public string Dir { get; set; }
    [JsonIgnore] public bool IsDefault => Default ?? Label is 0 or 1;

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static PackageUniform Load(string dir)
    {
        var u = JsonSerializer.Deserialize<PackageUniform>(File.ReadAllText(Path.Combine(dir, FileName)), Json)
                ?? throw new InvalidDataException($"{Path.Combine(dir, FileName)} is empty.");
        u.Dir = dir; u.Name = Path.GetFileName(dir);
        return u;
    }

    public void Save(string dir) => File.WriteAllText(Path.Combine(dir, FileName), JsonSerializer.Serialize(this, Json));

    /// <summary>Menu order: home, away, third, then the rest by name.</summary>
    public static int Order(PackageUniform u) => u.Name switch { "home" => 0, "away" => 1, "third" => 2, _ => 3 };
}

public static class PackageUniforms
{
    /// <summary>A group's uniforms folder (recipe "uniforms"), or null when it has none.</summary>
    public static string Folder(PackageSpec group) => string.IsNullOrEmpty(group.Uniforms) ? null : group.PathOf(group.Uniforms);

    /// <summary>A team's exported uniforms, menu order. Unreadable ones are reported in <paramref name="problems"/>.</summary>
    public static List<PackageUniform> Of(PackageSpec group, TeamSpec t, List<string> problems = null)
    {
        var list = new List<PackageUniform>();
        string dir = Folder(group) is { } f ? Path.Combine(f, t.Code) : null;
        if (dir == null || !Directory.Exists(dir)) return list;
        foreach (string k in Directory.GetDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(Path.Combine(k, PackageUniform.FileName))) continue;
            try { list.Add(PackageUniform.Load(k)); }
            catch (Exception ex) { problems?.Add($"{t.Code} {Path.GetFileName(k)}: {ex.Message}"); }
        }
        return list.OrderBy(PackageUniform.Order).ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Every exported uniform of the package: (group, team, uniform).</summary>
    public static IEnumerable<(PackageSpec Group, TeamSpec Team, PackageUniform Uniform)> All(PackageSpec spec, List<string> problems = null) =>
        spec.Groups.SelectMany(g => g.Teams.SelectMany(t => Of(g, t, problems).Select(u => (g, t, u))));

    /// <summary>The kit folders the package's uniforms take, with who has each (a folder belongs to one uniform).</summary>
    public static Dictionary<string, string> Reserved(PackageSpec spec) =>
        All(spec).Where(x => !string.IsNullOrEmpty(x.Uniform.Folder))
                 .GroupBy(x => x.Uniform.Folder, StringComparer.OrdinalIgnoreCase)
                 .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => $"{x.Team.Code} {x.Uniform.Name}")), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Step 3 (database): each team with uniforms gets exactly those in its jersey menu. Returns the uniforms whose
    /// textures and stamps step 4 writes (the ones whose kit folder is in the game).
    /// </summary>
    public static List<(TeamSpec Team, PackageUniform Uniform, Kit Kit)> ApplySlots(T3Db db, IReadOnlyList<Kit> kits, PackageSpec spec, PackageInstaller.Result r)
    {
        var problems = new List<string>();
        var byFolder = kits.GroupBy(k => k.Folder, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var done = new List<(TeamSpec, PackageUniform, Kit)>();
        var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in spec.Groups)
            foreach (var t in g.Teams)
            {
                var slots = new List<JerseySlot>();
                foreach (var u in Of(g, t, problems))
                {
                    string who = $"{t.Code} {u.Name}";
                    if (string.IsNullOrEmpty(u.Folder) || !byFolder.TryGetValue(u.Folder, out var kit)) { problems.Add($"{who}: its kit folder {u.Folder} isn't in the game; export it again"); continue; }
                    if (taken.TryGetValue(u.Folder, out var other)) { problems.Add($"{who}: kit folder {u.Folder} is {other}'s too; export one of them again"); continue; }
                    if (JerseySlots.Address(kit) is not { } a) { problems.Add($"{who}: a menu slot can't name {u.Folder}"); continue; }
                    taken[u.Folder] = who;
                    slots.Add(new JerseySlot { ArtId = u.ArtId, Style = a.Style, Variant = a.Variant, Label = u.Label, IsDefault = u.IsDefault, IsLight = u.Light });
                    done.Add((t, u, kit));
                    WriteGear(db, t.Index, u, a.Style, a.Variant, who, problems);
                }
                if (slots.Count == 0) continue;
                // a team needs a home and an away uniform: until both are exported, the template's stays for the missing one
                var before = JerseySlots.Read(db, t.Index);
                foreach (int label in new[] { 0, 1 })
                    if (!slots.Any(s => s.Label == label) && before.FirstOrDefault(b => b.Label == label && b.IsDefault) is { } keep)
                        slots.Insert(Math.Min(label, slots.Count), keep);
                string summary = JerseySlots.Write(db, t.Index, slots);
                r.Lines.Add($"{t.Code}: jersey menu {string.Join(", ", done.Where(x => x.Item1 == t).Select(x => x.Item2.Name))} ({summary})");
            }
        foreach (var p in problems) r.Warnings.Add("uniforms: " + p);
        int gear = done.Count(x => x.Item2.Helmet != null || x.Item2.Gloves != null);
        if (gear > 0) r.Lines.Add($"uniforms: helmet and glove colours for {gear} uniform(s)");
        return done;
    }

    /// <summary>
    /// The uniform's helmet and glove colours as the team's own equipment rows (stockteamequipmenttable: team, kind 7
    /// helmet / 4 gloves, the kit's art id, era = brand, variant = the kit's number, brand -1): the row for exactly that
    /// kit is updated, or a copy of one of the team's rows of that kind is added with a new key.
    /// </summary>
    private static void WriteGear(T3Db db, int team, PackageUniform u, int era, int variant, string who, List<string> problems)
    {
        if (u.Helmet == null && u.Gloves == null) return;
        var t = db.Table("stockteamequipmenttable");
        var f = t.Fields.ToDictionary(x => x.Name);
        int I(int r, string n) => Convert.ToInt32(db.Get(t, r, f[n]));
        var rows = Enumerable.Range(0, t.WrittenRecords).Where(r => !db.IsInvalid(t, r)).ToList();
        foreach (var (kind, zones) in new[] { (KitEquipment.HelmetKind, u.Helmet), (KitEquipment.GloveKind, u.Gloves) })
        {
            if (zones == null || zones.Length == 0) continue;
            int row = rows.FirstOrDefault(r => I(r, "team") == team && I(r, "equipment") == kind && I(r, "brand") == -1
                                               && I(r, "jerseyartid") == u.ArtId && I(r, "era") == era && I(r, "variant") == variant, -1);
            if (row < 0)
            {
                int src = rows.FirstOrDefault(r => I(r, "team") == team && I(r, "equipment") == kind, -1);
                if (src < 0) { problems.Add($"{who}: the team has no {(kind == KitEquipment.HelmetKind ? "helmet" : "glove")} row to copy; colours left out"); continue; }
                if (t.WrittenRecords >= t.Records) { problems.Add($"{who}: stockteamequipmenttable is full; colours left out"); continue; }
                int key = rows.Max(r => I(r, "index")) + 1;
                row = t.WrittenRecords;
                db.SetWritten(t, row + 1);
                db.WriteRecord(t, row, db.RecordBytes(t, src));
                rows.Add(row);
                db.Set(t, row, f["index"], key);
                db.Set(t, row, f["jerseyartid"], u.ArtId);
                db.Set(t, row, f["era"], era);
                db.Set(t, row, f["variant"], variant);
                db.Set(t, row, f["brand"], -1);
            }
            for (int z = 1; z <= 15; z++) db.Set(t, row, f["zone" + z], zones[Math.Min(z - 1, zones.Length - 1)] & 0xFFFFFF);
        }
    }
}
