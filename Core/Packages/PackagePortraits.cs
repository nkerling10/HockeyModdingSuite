using System.Globalization;
using System.Text;

namespace NHL27Kit.Core.Packages;

/// <summary>
/// The package's player portraits: portraits/&lt;team code&gt;/&lt;first-last&gt;.png, already framed like the game's own
/// (512×340, transparent; made from the original photos by franchise-patch-research/eboot_tools/prepare_photos.py, so
/// the package ships a few hundred KB per player, not the originals).
///
/// Each player of a package team whose name matches a file gets one of the game's unused portrait slots (a shipped
/// portrait no bio uses: PortraitSync.FreeArtIds), the image written into that slot's texture, and his bio pointed at it
/// (artid, hasportrait). No portrait any player uses is changed.
/// </summary>
public static class PackagePortraits
{
    public sealed record Planned(string Code, string Name, int BioRow, int Slot, string File);

    /// <summary>"L.J. Mooney" → "lj-mooney", "Jakub Vaněček" → "jakub-vanecek".</summary>
    public static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in (s ?? "").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c is '.' or '\'' or '’') continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }
        var t = sb.ToString();
        while (t.Contains("--")) t = t.Replace("--", "-");
        return t.Trim('-');
    }

    /// <summary>The portraits folder: the recipe's "portraits", else portraits/ when it exists; null when none.</summary>
    public static string Folder(PackageSpec spec)
    {
        string dir = spec.PathOf(string.IsNullOrEmpty(spec.Portraits) ? "portraits" : spec.Portraits);
        return dir != null && Directory.Exists(dir) ? dir : null;
    }

    /// <summary>
    /// Step 3 (database, in memory): gives each matched player a free slot and points his bio at it. Returns what the
    /// texture step has to write. A commit whose art id changes is updated in <paramref name="r"/>'s commit table.
    /// </summary>
    public static List<Planned> Assign(AssetIndex index, Legacy legacy, T3Db db, PackageSpec spec, PackageInstaller.Result r)
    {
        var plan = new List<Planned>();
        string root = Folder(spec);
        if (root == null) return plan;
        var others = PortraitSync.OtherRosters.Select(n => { try { return legacy.OpenDb(n); } catch { return null; } }).Where(d => d != null).ToList();
        var free = new Queue<int>(PortraitSync.FreeArtIds(index, db, others));
        var roster = new Roster(db);
        var bio = db.Table(Roster.BioTable);
        var f = bio.Fields.ToDictionary(x => x.Name);
        // a school player's bio team is -1 (the players step); his school is his roster row's team
        var rt = db.Table(Roster.RosterTable);
        var rtTeam = rt.Fields.First(x => x.Name == "team");
        int TeamOf(RosterPlayer p) => p.RosterRow >= 0 ? Convert.ToInt32(db.Get(rt, p.RosterRow, rtTeam)) : p.Team;
        var unmatched = new List<string>();
        foreach (var t in spec.Teams)
        {
            string dir = Path.Combine(root, t.Code);
            if (!Directory.Exists(dir)) continue;
            var players = roster.Players.Where(p => TeamOf(p) == t.Index).GroupBy(p => Slug(p.Name)).ToDictionary(g => g.Key, g => g.First());
            int n = 0;
            foreach (string file in Directory.GetFiles(dir, "*.png").OrderBy(x => x, StringComparer.Ordinal))
            {
                string key = Slug(Path.GetFileNameWithoutExtension(file));
                // the players step's match first (the file is named as the roster file names him), then his game name
                var p = r.Placed.TryGetValue((t.Code, key), out int gid) ? roster.Find(gid) : null;
                if (p == null && !players.TryGetValue(key, out p)) { unmatched.Add($"{t.Code}/{Path.GetFileName(file)}"); continue; }
                if (free.Count == 0) { r.Warnings.Add("portraits: no free portrait slots left"); break; }
                int slot = free.Dequeue();
                int oldArt = Convert.ToInt32(db.Get(bio, p.BioRow, f["artid"]));
                db.Set(bio, p.BioRow, f["artid"], slot);
                if (f.TryGetValue("hasportrait", out var hp)) db.Set(bio, p.BioRow, hp, 1);
                for (int i = 0; i < r.Commits.Count; i++)
                    if (r.Commits[i].ArtId == oldArt) r.Commits[i] = (slot, r.Commits[i].Team, r.Commits[i].Offset);
                plan.Add(new Planned(t.Code, p.Name, p.BioRow, slot, file));
                n++;
            }
            r.Lines.Add($"portraits: {t.Code}: {n} players");
        }
        if (unmatched.Count > 0)
            r.Warnings.Add($"portraits: no player of that team matches ({unmatched.Count}): {string.Join(", ", unmatched.Take(12))}{(unmatched.Count > 12 ? ", ..." : "")}");
        return plan;
    }
}
