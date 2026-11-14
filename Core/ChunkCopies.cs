using System.Security.Cryptography;

namespace NHL27Kit.Core;

/// <summary>
/// One stored copy of a chunk: its place in a TOC chunk table, or in a bundle. The same chunk id is often stored several
/// times (a chunk table entry holding every mip, each bundle that loads the texture holding its own copy).
/// </summary>
public sealed record ChunkCopy(string Kind, string Toc, TocRef Ref, int BundleId, string BundleName, long Sha1Pos, byte[] Sha1,
                               uint LogicalOffset, uint LogicalSize, bool Live, TocRef Record = default)
{
    public bool InBundle => BundleId >= 0;
}

/// <summary>Parsed superbundle TOCs, kept for the lifetime of one planning run.</summary>
public sealed class TocCache
{
    private readonly AssetIndex _index;
    private readonly Dictionary<string, SuperBundleToc> _tocs = new(StringComparer.OrdinalIgnoreCase);

    public TocCache(AssetIndex index) => _index = index;

    public SuperBundleToc Get(string path)
    {
        if (!_tocs.TryGetValue(path, out var toc))
            _tocs[path] = toc = new SuperBundleToc(path, File.ReadAllBytes(path), _index.Layout.TocCatalogs);
        return toc;
    }
}

public static class ChunkCopies
{
    // Bundle records read once per planning pass (CacheRecords), not once per chunk: a package plans thousands of
    // textures listed in the same few big bundles. Off by default: a record's SHA1s change when a mod is written.
    [ThreadStatic] private static Dictionary<(int, int, bool, uint, uint), BundleRecord> _records;

    // Where each stored place is listed in a bundle (location key -> TOC path, bundle, entry index), built once per
    // planning pass the first time FindAt runs: FindAt used to walk every bundle of every TOC (~372k) on each call, which
    // a package's logo pass did once per grown texture.
    [ThreadStatic] private static Dictionary<long, List<(string Path, int PathAt, TocBundle Bundle, int BundleAt, int Entry)>> _places;

    private sealed class RecordCache : IDisposable { public void Dispose() { _records = null; _places = null; } }
    private sealed class NoOp : IDisposable { public void Dispose() { } }

    /// <summary>Keeps bundle records read on this thread until disposed (nested calls share the outer cache). Only for
    /// planning that writes nothing in between.</summary>
    public static IDisposable CacheRecords()
    {
        if (_records != null) return new NoOp();
        _records = new();
        return new RecordCache();
    }

    private static BundleRecord Record(AssetIndex index, TocRef r)
    {
        var key = (r.Catalog, r.Cas, r.Patch, r.Offset, r.Size);
        if (_records != null && _records.TryGetValue(key, out var hit)) return hit;
        var rec = BundleRecord.Read(index.ReadStored(r.Catalog, r.Cas, r.Patch, r.Offset, r.Size), r.Offset);
        if (_records != null) _records[key] = rec;
        return rec;
    }

    /// <summary>A TOC's path below Data or Patch, which names the superbundle it belongs to.</summary>
    public static string SuperbundleKey(AssetIndex index, string toc)
    {
        string rel = Path.GetRelativePath(index.Layout.GameDir, toc).Replace('\\', '/');
        int slash = rel.IndexOf('/');
        return slash < 0 ? rel : rel[(slash + 1)..];
    }

    /// <summary>
    /// Every stored copy of a chunk. A copy in a Patch TOC or bundle replaces the same one in Data, so only the last copy
    /// per superbundle (and per bundle name) is <see cref="ChunkCopy.Live"/>; the rest are still listed.
    /// </summary>
    public static List<ChunkCopy> Find(AssetIndex index, Guid id, TocCache tocs)
    {
        var copies = new List<ChunkCopy>();

        // chunk table entries, one per TOC that has the chunk
        if (index.TableChunks.TryGetValue(id, out var table))
        {
            var lastBySb = table.GroupBy(t => SuperbundleKey(index, t.Toc)).ToDictionary(g => g.Key, g => g.Last());
            foreach (var t in table)
                copies.Add(new ChunkCopy("table", t.Toc, t.Ref, -1, null, -1, null, 0, t.Ref.Size, ReferenceEquals(lastBySb[SuperbundleKey(index, t.Toc)], t)));
        }

        // bundle entries: read each listing bundle's record to find the chunk's position in it
        if (index.GetChunk(id) is { InChunkTable: false } listed)
        {
            var lastByName = listed.Bundles.GroupBy(b => (SuperbundleKey(index, index.BundleList[b].Toc), index.BundleList[b].Name)).ToDictionary(g => g.Key, g => g.Max());
            foreach (int b in listed.Bundles)
            {
                var info = index.BundleList[b];
                var toc = tocs.Get(info.Toc);
                var tb = toc.Bundles[info.BundleIndex];
                var rec = Record(index, tb.Entries[0]);
                int at = rec.Chunks.FindIndex(c => c.Id == id);
                if (at < 0) continue;
                var item = rec.Chunks[at];
                var entry = tb.Entries[1 + rec.Ebx.Count + rec.Res.Count + at];
                bool live = lastByName[(SuperbundleKey(index, info.Toc), info.Name)] == b;
                copies.Add(new ChunkCopy("bundle", info.Toc, entry, b, info.Name, item.Sha1Pos, item.Sha1, item.LogicalOffset, item.Size, live, tb.Entries[0]));
            }
        }
        return copies;
    }

    /// <summary>
    /// Bundle copies found by place rather than through the index: every bundle entry in the TOCs that starts at one of
    /// <paramref name="places"/> (LocationIndex keys) and is a chunk of its bundle. The index lists a chunk once, so a
    /// bundle in another superbundle that carries the same chunk can be missing from <see cref="Find"/>. Copies already
    /// in <paramref name="known"/> are skipped; the ones found count as live (they are checked like any live copy).
    /// </summary>
    /// <param name="only">Only copies of this chunk.</param>
    public static List<ChunkCopy> FindAt(AssetIndex index, TocCache tocs, IReadOnlySet<long> places, IEnumerable<ChunkCopy> known, Guid? only = null)
    {
        var have = known.Where(c => c.InBundle).Select(c => (c.Toc, c.BundleName, LocationIndex.Key(c.Ref))).ToHashSet();
        var found = new List<ChunkCopy>();
        void Consider(string path, TocBundle tb, int i)
        {
            long key = LocationIndex.Key(tb.Entries[i]);
            if (!places.Contains(key) || have.Contains((path, tb.Name, key))) return;
            var r0 = tb.Entries[0];
            var rec = Record(index, r0);
            int at = i - 1 - rec.Ebx.Count - rec.Res.Count;
            if (at < 0 || at >= rec.Chunks.Count) return;
            var item = rec.Chunks[at];
            if (only != null && item.Id != only) return;
            found.Add(new ChunkCopy("bundle", path, tb.Entries[i], int.MaxValue, tb.Name, item.Sha1Pos, item.Sha1, item.LogicalOffset, item.Size, true, r0));
            have.Add((path, tb.Name, key));
        }
        if (_records != null)
        {
            // a planning pass: look the places up (the same walk as below, done once and kept, in the same order)
            if (_places == null)
            {
                _places = new();
                int pathAt = 0;
                foreach (string path in index.Layout.AllTocPaths())
                {
                    var bundles = tocs.Get(path).Bundles;
                    for (int b = 0; b < bundles.Count; b++)
                        for (int i = 1; i < bundles[b].Entries.Count; i++)
                        {
                            long key = LocationIndex.Key(bundles[b].Entries[i]);
                            if (!_places.TryGetValue(key, out var list)) _places[key] = list = new();
                            list.Add((path, pathAt, bundles[b], b, i));
                        }
                    pathAt++;
                }
            }
            var hits = new List<(string Path, int PathAt, TocBundle Bundle, int BundleAt, int Entry)>();
            foreach (long key in places)
                if (_places.TryGetValue(key, out var list)) hits.AddRange(list);
            // in the full walk's order: TOC paths, then bundles, then entries
            foreach (var h in hits.OrderBy(h => h.PathAt).ThenBy(h => h.BundleAt).ThenBy(h => h.Entry))
                Consider(h.Path, h.Bundle, h.Entry);
            return found;
        }
        foreach (string path in index.Layout.AllTocPaths())
        {
            var toc = tocs.Get(path);
            foreach (var tb in toc.Bundles)
                for (int i = 1; i < tb.Entries.Count; i++)
                    Consider(path, tb, i);
        }
        return found;
    }

    /// <summary>The bytes a bundle record's SHA1 for a stored chunk covers, as the game's own files show it: the stored (framed) bytes.</summary>
    public static byte[] Sha1Of(ReadOnlySpan<byte> stored) => SHA1.HashData(stored);
}
