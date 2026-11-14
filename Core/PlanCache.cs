using System.Security.Cryptography;

namespace NHL27Kit.Core;

/// <summary>
/// A disk cache for pure, slow steps of planning (a texture's encoded chunk from its image and layout), so reinstalling
/// a package doesn't encode thousands of unchanged logos again. Off until <see cref="Begin"/> sets a folder; each run
/// keeps what it used and <see cref="End"/> deletes the rest, so the cache holds one install's worth.
/// </summary>
public static class PlanCache
{
    private const string Version = "1";
    private static string _dir;
    private static readonly HashSet<string> Used = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    public static int Hits, Misses;

    /// <summary>Turns the cache on for <paramref name="dir"/> (nested calls keep the outer one). Dispose to turn it off
    /// and, with <paramref name="prune"/>, delete what this run didn't use.</summary>
    public static IDisposable Begin(string dir, bool prune = true)
    {
        lock (Gate)
        {
            if (_dir != null) return new End(null, false);
            _dir = dir; Used.Clear(); Hits = Misses = 0;
            Directory.CreateDirectory(dir);
            return new End(dir, prune);
        }
    }

    private sealed class End : IDisposable
    {
        private readonly string _dir;
        private readonly bool _prune;
        public End(string dir, bool prune) { _dir = dir; _prune = prune; }
        public void Dispose()
        {
            if (_dir == null) return;
            lock (Gate)
            {
                if (_prune)
                    foreach (string f in Directory.EnumerateFiles(_dir, "*.bin", SearchOption.AllDirectories).ToList())
                        if (!Used.Contains(f)) try { File.Delete(f); } catch { /* in use: next run */ }
                PlanCache._dir = null;
            }
        }
    }

    /// <summary>The cached result for these key parts, or <paramref name="make"/>'s (then kept). Without a cache folder,
    /// just <paramref name="make"/>.</summary>
    public static byte[] Get(string kind, Func<byte[]> make, string text, params byte[][] parts)
    {
        string dir = _dir;
        if (dir == null) return make();
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(System.Text.Encoding.UTF8.GetBytes($"{Version}|{kind}|{text}|"));
        foreach (var p in parts) { h.AppendData(BitConverter.GetBytes(p.Length)); h.AppendData(p); }
        string key = Convert.ToHexString(h.GetHashAndReset());
        string file = Path.Combine(dir, kind, key[..2], key + ".bin");
        lock (Gate) Used.Add(file);
        if (File.Exists(file))
        {
            try { var v = File.ReadAllBytes(file); Interlocked.Increment(ref Hits); return v; }
            catch (IOException) { /* rebuild it */ }
        }
        var value = make();
        Interlocked.Increment(ref Misses);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string tmp = file + "." + Environment.CurrentManagedThreadId + ".tmp";
            File.WriteAllBytes(tmp, value);
            File.Move(tmp, file, overwrite: true);
        }
        catch (IOException) { /* not kept: next run encodes it again */ }
        return value;
    }
}
