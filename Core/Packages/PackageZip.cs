using System.IO.Compression;

namespace NHL27Kit.Core.Packages;

/// <summary>
/// A package as one file to share: the package folder zipped without what only its author needs (source photos,
/// backups, build reports, caches), and unzipped into the kit's own packages folder to install.
/// </summary>
public static class PackageZip
{
    /// <summary>Folders left out at any depth: the author's sources and leftovers.</summary>
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { "backups", "build", "photos", "cache", ".git" };

    /// <summary>Where unzipped packages go: &lt;kit&gt;/packages/&lt;name&gt;.</summary>
    public static string Root => Path.Combine(KitPaths.Root, "packages");

    /// <summary>Zips <paramref name="folder"/> (it must hold a package.json) into <paramref name="zip"/>. Returns the
    /// number of files and their total size. Rink layouts (5000×2236 pictures, ~5 MB each, used only for their centre
    /// circle) aren't shipped: each team's centre ice is cut here exactly as the installer cuts it and goes in as the
    /// team's ice logo (&lt;CODE&gt;_ice.png, 1024²), which the installer then takes as it is, so the result is the same.</summary>
    public static (int Files, long Bytes) Export(string folder, string zip, Action<string> log = null)
    {
        folder = Path.GetFullPath(folder);
        if (!File.Exists(Path.Combine(folder, "package.json"))) throw new FileNotFoundException($"{folder} has no package.json.");
        if (File.Exists(zip)) File.Delete(zip);
        int n = 0; long bytes = 0;
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);

        // centre ice from the rink layouts, in place of the layouts
        var spec = PackageSpec.Load(folder);
        var skipFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int cut = 0; long rinkBytes = 0;
        foreach (var t in spec.AllTeams)
        {
            foreach (var r in new[] { spec.RinkFile(t), spec.RinkJpeg(t) }.Where(f => f != null)) { skipFiles.Add(Path.GetFullPath(r)); rinkBytes += new FileInfo(r).Length; }
            string rink = spec.RinkFile(t);
            if (rink == null) continue;
            var raw = File.ReadAllBytes(rink);
            var (w, h) = Png.Size(raw, rink);
            if (!CenterIce.IsRinkShape(w, h) || w < CenterIce.LeastWidth) { log?.Invoke($"{t.Code}: {Path.GetFileName(rink)} isn't a rink layout; left out"); continue; }
            // where the installer looks for the team's ice logo: its own logo folder, else the league's flat folder
            string teamDir = spec.TeamLogoFolder(t);
            string target = Directory.Exists(teamDir) ? Path.Combine(teamDir, "ice.png")
                          : !string.IsNullOrEmpty(t.LogoDir) ? Path.Combine(t.LogoDir, t.Code + "_ice.png") : null;
            if (target == null) { log?.Invoke($"{t.Code}: no logo folder for its centre ice; rink left out"); continue; }
            skipFiles.Add(Path.GetFullPath(target));                 // an ice logo of the author's own: the rink wins, as on install
            if (spec.TeamExtraFile(t, "ice") is { } existing) skipFiles.Add(Path.GetFullPath(existing));
            var (logo, _) = PackageInstaller.CenterIceCut(raw, rink);
            byte[] png = ImageFiles.Png(logo, CenterIce.LogoSize, CenterIce.LogoSize);
            var entry = z.CreateEntry(Path.GetRelativePath(folder, target).Replace(Path.DirectorySeparatorChar, '/'), CompressionLevel.NoCompression);
            using (var es = entry.Open()) es.Write(png);
            n++; bytes += png.Length; cut++;
        }
        if (cut > 0) log?.Invoke($"centre ice: {cut} teams cut from their rinks ({rinkBytes / 1048576.0:0.0} MB of layouts left out)");

        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(folder, file);
            var parts = rel.Split(Path.DirectorySeparatorChar);
            if (parts[..^1].Any(SkipDirs.Contains)) continue;
            if (skipFiles.Contains(Path.GetFullPath(file))) continue;
            string leaf = parts[^1];
            if (leaf.Contains(".bak", StringComparison.OrdinalIgnoreCase) || leaf.StartsWith("package.before", StringComparison.OrdinalIgnoreCase)) continue;
            // PNGs are already compressed: stored, which is much faster
            var level = Path.GetExtension(leaf).ToLowerInvariant() is ".png" or ".jpg" or ".dds" ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
            z.CreateEntryFromFile(file, rel.Replace('\\', '/'), level);
            n++; bytes += new FileInfo(file).Length;
            if (n % 200 == 0) log?.Invoke($"{n} files");
        }
        return (n, bytes);
    }

    /// <summary>
    /// Unzips a shared package into <see cref="Root"/>/&lt;zip name&gt; (replacing an earlier copy there) and returns the
    /// folder holding its package.json (the zip's root or the one folder in it).
    /// </summary>
    public static string Extract(string zip, Action<string> log = null)
    {
        string name = Path.GetFileNameWithoutExtension(zip);
        string dest = Path.Combine(Root, name);
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.CreateDirectory(dest);
        log?.Invoke($"unzipping into {dest}");
        using (var z = ZipFile.OpenRead(zip))
        {
            string full = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
            foreach (var e in z.Entries)
            {
                string target = Path.GetFullPath(Path.Combine(dest, e.FullName));
                if (!target.StartsWith(full, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{zip}: entry {e.FullName} points outside the package.");
                if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                e.ExtractToFile(target, true);
            }
        }
        if (File.Exists(Path.Combine(dest, "package.json"))) return dest;
        var inner = Directory.GetDirectories(dest).Where(d => File.Exists(Path.Combine(d, "package.json"))).ToList();
        if (inner.Count == 1) return inner[0];
        throw new InvalidDataException($"{Path.GetFileName(zip)} has no package.json (at its root or in one folder).");
    }
}
