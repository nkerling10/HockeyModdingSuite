using System.Security.Cryptography;
using System.Text.Json;

namespace NHL27Kit.Core;

/// <summary>One byte range of a game file a mod changes: the bytes before and after, kept in the mod's folder.</summary>
public sealed class RangePatch
{
    /// <summary>Path below the game folder, with forward slashes.</summary>
    public string File { get; set; }
    public long Offset { get; set; }
    public int Length { get; set; }
    public string OldSha1 { get; set; }
    public string NewSha1 { get; set; }
    public string Note { get; set; }
    /// <summary>
    /// The range is added at the end of the file (Offset = its length before). Restoring cuts the file back when the range
    /// is at the end; otherwise it stays, unused, until what was added after it is restored too (<see cref="ModStore"/>).
    /// </summary>
    public bool Append { get; set; }
    /// <summary>Where the range's old and new bytes are in its mod's old.pack / new.pack, and the old bytes' length. Null
    /// for a mod saved before packs: its bytes are &lt;n&gt;.old and &lt;n&gt;.new files.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? OldAt { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? NewAt { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int OldLength { get; set; }
}

/// <summary>A replacement of one asset: what it replaces, and the ranges it writes.</summary>
public sealed class ModEntry
{
    public string Id { get; set; }
    /// <summary>"texture" or "sound".</summary>
    public string Kind { get; set; }
    /// <summary>The asset's name (the key: one entry per asset).</summary>
    public string Name { get; set; }
    public string Source { get; set; }
    public string Summary { get; set; }
    public List<string> Warnings { get; set; } = new();
    public DateTime Created { get; set; } = DateTime.Now;
    public List<RangePatch> Patches { get; set; } = new();
}

public enum ModState { NotApplied, Applied, Partial, Changed, Missing }

/// <summary>
/// The mod list and what it does to the game folder. A mod is a set of byte ranges: applying writes each range's new
/// bytes, restoring writes its old bytes back, and every step first checks the range holds what it expects (by SHA1), so
/// nothing is overwritten that the kit did not put there or read from there.
/// Layout: &lt;dir&gt;\mods.json, and per mod &lt;dir&gt;\&lt;id&gt;\&lt;n&gt;.old and .new.
/// </summary>
public sealed class ModStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Dir { get; }
    public List<ModEntry> Entries { get; private set; } = new();

    public ModStore(string dir)
    {
        Dir = dir;
        Load();
    }

    private string ListFile => Path.Combine(Dir, "mods.json");
    private string LeftoverFile => Path.Combine(Dir, "leftovers.json");

    /// <summary>
    /// Data that removed mods added at the end of game files and that may still be there (a restore leaves added data
    /// that isn't at the end of its file): trimmed like the list's own once it reaches the end.
    /// </summary>
    private List<RangePatch> _leftovers = new();

    private void Load()
    {
        try
        {
            Entries = File.Exists(ListFile) ? JsonSerializer.Deserialize<List<ModEntry>>(File.ReadAllText(ListFile)) ?? new() : new();
            _leftovers = File.Exists(LeftoverFile) ? JsonSerializer.Deserialize<List<RangePatch>>(File.ReadAllText(LeftoverFile)) ?? new() : new();
        }
        catch (Exception e)
        {
            throw new InvalidDataException($"The mod list {ListFile} can't be read: {e.Message}. Fix or move it; the kit won't replace it.");
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(ListFile + ".tmp", JsonSerializer.Serialize(Entries, Json));
        File.Move(ListFile + ".tmp", ListFile, overwrite: true);
        if (_leftovers.Count > 0 || File.Exists(LeftoverFile))
        {
            File.WriteAllText(LeftoverFile + ".tmp", JsonSerializer.Serialize(_leftovers, Json));
            File.Move(LeftoverFile + ".tmp", LeftoverFile, overwrite: true);
        }
    }

    public ModEntry Find(string kind, string name) =>
        Entries.FirstOrDefault(e => e.Kind == kind && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    private string Old(ModEntry e, int n) => Path.Combine(Dir, e.Id, $"{n}.old");
    private string New(ModEntry e, int n) => Path.Combine(Dir, e.Id, $"{n}.new");
    private string OldPack(ModEntry e) => Path.Combine(Dir, e.Id, "old.pack");
    private string NewPack(ModEntry e) => Path.Combine(Dir, e.Id, "new.pack");

    /// <summary>A mod's kept bytes: from its packs (one file each for old and new, opened once), or the older one file per range.</summary>
    private sealed class Kept : IDisposable
    {
        private readonly ModStore _s;
        private readonly ModEntry _e;
        private FileStream _old, _new;
        public Kept(ModStore s, ModEntry e) { _s = s; _e = e; }
        private static byte[] Slice(ref FileStream fs, string file, long at, int length)
        {
            fs ??= new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            fs.Position = at;
            var b = new byte[length];
            fs.ReadExactly(b);
            return b;
        }
        public byte[] Old(int i) => _e.Patches[i].OldAt is long at ? Slice(ref _old, _s.OldPack(_e), at, _e.Patches[i].OldLength) : File.ReadAllBytes(_s.Old(_e, i));
        public byte[] New(int i) => _e.Patches[i].NewAt is long at ? Slice(ref _new, _s.NewPack(_e), at, _e.Patches[i].Length) : File.ReadAllBytes(_s.New(_e, i));
        public void Dispose() { _old?.Dispose(); _new?.Dispose(); }
    }

    /// <summary>Replaces a range's kept new bytes (its Length is set by the caller): at the end of the pack, or its file.</summary>
    private void WriteNew(ModEntry e, int i, byte[] data)
    {
        var p = e.Patches[i];
        if (p.NewAt == null) { File.WriteAllBytes(New(e, i), data); return; }
        using var fs = new FileStream(NewPack(e), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        p.NewAt = fs.Position;
        fs.Write(data);
    }

    /// <summary>The game files one apply or restore touches, each opened once.</summary>
    private sealed class GameFiles : IDisposable
    {
        private readonly string _game;
        private readonly bool _write;
        private readonly Dictionary<string, FileStream> _open = new(StringComparer.OrdinalIgnoreCase);
        public GameFiles(string game, bool write = true) { _game = game; _write = write; }
        public FileStream Get(RangePatch p)
        {
            if (!_open.TryGetValue(p.File, out var fs))
                _open[p.File] = fs = new FileStream(Path_(_game, p), FileMode.Open, _write ? FileAccess.ReadWrite : FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            return fs;
        }
        /// <summary>Whether the range's file is in the game folder (asked of the disk once per file).</summary>
        public bool Has(RangePatch p) => _open.ContainsKey(p.File) || File.Exists(Path_(_game, p));
        public void FlushAll() { foreach (var fs in _open.Values) fs.Flush(true); }
        public void Dispose() { foreach (var fs in _open.Values) fs.Dispose(); _open.Clear(); }
    }

    /// <summary><see cref="Current(string, RangePatch)"/> on an open file.</summary>
    private static string Current(FileStream fs, RangePatch p)
    {
        if (p.Append && fs.Length <= p.Offset) return Hex(SHA1.HashData(Array.Empty<byte>()));
        if (p.Offset + p.Length > fs.Length) return null;
        fs.Position = p.Offset;
        var b = new byte[p.Length];
        fs.ReadExactly(b);
        return Hex(SHA1.HashData(b));
    }

    /// <summary>Adds a mod, replacing any entry for the same asset (the caller restores that one first).</summary>
    public ModEntry Add(string kind, string name, string source, string summary, IReadOnlyList<PatchData> patches, IEnumerable<string> warnings = null)
    {
        var existing = Find(kind, name);
        if (existing != null) Remove(existing);
        var e = Begin(kind, name, source, summary, warnings);
        AddPatches(e, patches);
        Commit(e);
        return e;
    }

    /// <summary>
    /// Starts a mod whose ranges are added in parts (<see cref="AddPatches"/>, then <see cref="Commit"/>), for one too
    /// big to hold in memory at once. It isn't in the list until it's committed.
    /// </summary>
    public ModEntry Begin(string kind, string name, string source, string summary, IEnumerable<string> warnings = null)
    {
        var e = new ModEntry { Id = Guid.NewGuid().ToString("N")[..12], Kind = kind, Name = name, Source = source, Summary = summary, Warnings = warnings?.ToList() ?? new() };
        Directory.CreateDirectory(Path.Combine(Dir, e.Id));
        return e;
    }

    /// <summary>Keeps more ranges of a begun mod (their old and new bytes go to its folder).</summary>
    public void AddPatches(ModEntry e, IEnumerable<PatchData> patches)
    {
        // every range's bytes go at the end of the mod's two packs (thousands of small files are slow to write and read)
        using var oldFs = new FileStream(OldPack(e), FileMode.Append, FileAccess.Write, FileShare.None, 1 << 16);
        using var newFs = new FileStream(NewPack(e), FileMode.Append, FileAccess.Write, FileShare.None, 1 << 16);
        foreach (var p in patches)
        {
            long oa = oldFs.Position, na = newFs.Position;
            oldFs.Write(p.Old);
            newFs.Write(p.New);
            e.Patches.Add(new RangePatch
            {
                File = p.File, Offset = p.Offset, Length = p.New.Length, OldSha1 = Hex(SHA1.HashData(p.Old)), NewSha1 = Hex(SHA1.HashData(p.New)),
                Note = p.Note, Append = p.Append, OldAt = oa, NewAt = na, OldLength = p.Old.Length,
            });
        }
    }

    /// <summary>Puts a begun mod in the list, replacing any entry for the same asset (the caller restores that one first).</summary>
    public void Commit(ModEntry e)
    {
        if (Find(e.Kind, e.Name) is { } existing && existing != e) Remove(existing);
        if (!Entries.Contains(e)) Entries.Add(e);
        Save();
    }

    /// <summary>Forgets a mod. It must not be applied: its backups go with it.</summary>
    public void Remove(ModEntry e)
    {
        Entries.Remove(e);
        _leftovers.AddRange(e.Patches.Where(p => p.Append));
        Save();
        try { Directory.Delete(Path.Combine(Dir, e.Id), recursive: true); } catch { /* leftovers are harmless */ }
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b);

    private static string Path_(string game, RangePatch p) => Path.Combine(game, p.File.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// The SHA1 a range's place holds now, or null when the file is too short. An appended range reads as its old
    /// (empty) bytes while the file ends where it began.
    /// </summary>
    private static string Current(string file, RangePatch p)
    {
        // an earlier range of the same mod may be added before it, so a file that ends at or before it hasn't got it
        if (p.Append && new FileInfo(file).Length <= p.Offset) return Hex(SHA1.HashData(Array.Empty<byte>()));
        return HashRange(file, p.Offset, p.Length);
    }

    /// <summary>
    /// The SHA1 of a file range, or null when the file is too short. Read through one pooled buffer: checking every
    /// mod's state reads well over 100 MB, which as one array per range would leave the large-object heap in holes.
    /// </summary>
    private static string HashRange(string file, long offset, int length)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
        if (offset + length > fs.Length) return null;
        fs.Position = offset;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            for (int left = length; left > 0;)
            {
                int n = fs.Read(buf, 0, Math.Min(buf.Length, left));
                if (n <= 0) return null;
                sha.AppendData(buf, 0, n);
                left -= n;
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
        return Hex(sha.GetHashAndReset());
    }

    /// <summary>What the game folder holds for this mod: all old, all new, a mix, or something else.</summary>
    public ModState State(ModEntry e, string game) => StateOf(e, StateHashes(e, game));

    /// <summary>
    /// The SHA1 each range's place holds now, read once (null: its file is missing or too short, or the range doesn't
    /// count for the state). Added data says nothing on its own (it may stay after a restore): the ranges that point at
    /// it decide, so it is read only when the mod is nothing but added data.
    /// </summary>
    private static string[] StateHashes(ModEntry e, string game)
    {
        bool appendsCount = e.Patches.All(p => p.Append);
        var h = new string[e.Patches.Count];
        using var files = new GameFiles(game, write: false);
        for (int i = 0; i < h.Length; i++)
        {
            var p = e.Patches[i];
            if (p.Append && !appendsCount) continue;
            if (files.Has(p)) h[i] = Current(files.Get(p), p);
        }
        return h;
    }

    private static ModState StateOf(ModEntry e, string[] h)
    {
        int old = 0, neu = 0, other = 0;
        bool appendsCount = e.Patches.All(p => p.Append);
        for (int i = 0; i < h.Length; i++)
        {
            var p = e.Patches[i];
            if (p.Append && !appendsCount) continue;
            if (h[i] == null) return ModState.Missing;
            // a range whose old and new bytes are the same (a size that didn't change) says nothing either way
            if (p.OldSha1 == p.NewSha1 && h[i] == p.NewSha1) continue;
            if (h[i] == p.NewSha1) neu++;
            else if (h[i] == p.OldSha1) old++;
            else other++;
        }
        if (other > 0) return ModState.Changed;
        if (old == 0) return ModState.Applied;
        return neu == 0 ? ModState.NotApplied : ModState.Partial;
    }

    /// <summary>Writes the mod's new bytes. Every range must hold its old or new bytes.</summary>
    public void Apply(ModEntry e, string game, Action<string> log = null) => Write(e, game, toNew: true, log);

    /// <summary>Writes the mod's old bytes back.</summary>
    public void Restore(ModEntry e, string game, Action<string> log = null) => Write(e, game, toNew: false, log);

    /// <summary>
    /// Restores the mod when it is applied or partly applied (<see cref="State"/> then <see cref="Restore"/>), reading
    /// the game's bytes once for both. Returns whether it restored.
    /// </summary>
    public bool RestoreIfApplied(ModEntry e, string game, Action<string> log = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var h = StateHashes(e, game);
        if (StateOf(e, h) is not (ModState.Applied or ModState.Partial)) return false;
        log?.Invoke($"{e.Name}: state read in {clock.Elapsed.TotalSeconds:0.0} s");
        Write(e, game, toNew: false, log, h);
        return true;
    }

    /// <summary>Whether two of the mod's ranges share bytes of a file (then a write can change what another range holds).</summary>
    private static bool Overlapping(ModEntry e) =>
        e.Patches.GroupBy(p => p.File, StringComparer.OrdinalIgnoreCase).Any(g =>
        {
            long end = long.MinValue;
            foreach (var p in g.OrderBy(p => p.Offset))
            {
                if (p.Offset < end) return true;
                end = Math.Max(end, p.Offset + p.Length);
            }
            return false;
        });

    /// <param name="known">What each range held when the caller just read it (restore only: <see cref="StateHashes"/>),
    /// so it isn't read again.</param>
    private void Write(ModEntry e, string game, bool toNew, Action<string> log, string[] known = null)
    {
        string verb = toNew ? "apply" : "restore";
        if (toNew) MoveAppended(e, game, log);
        int written = 0;
        // seconds per phase, in the last log line
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var took = new List<string>();
        void Took(string phase) { took.Add($"{phase} {clock.Elapsed.TotalSeconds:0.0} s"); clock.Restart(); }
        // each game file opened once; written ranges flushed to disk per file at the end, then read back
        using (var files = new GameFiles(game))
        using (var kept = new Kept(this, e))
        {
            // check everything before writing anything; what each range holds is kept for the writing pass below, which
            // needn't read it again unless two ranges share bytes (then an earlier write can change a later range)
            var held = new string[e.Patches.Count];
            bool reuse = !Overlapping(e);
            for (int i = 0; i < e.Patches.Count; i++)
            {
                var p = e.Patches[i];
                if (!files.Has(p)) throw new FileNotFoundException($"Can't {verb} {e.Name}: {p.File} is not in the game folder.");
                // added data needs no check to restore: nothing points at it afterwards, and TrimAdded cuts only what it recognises
                if (p.Append && !toNew) continue;
                string h = (toNew ? null : known?[i]) ?? Current(files.Get(p), p)
                           ?? throw new InvalidDataException($"Can't {verb} {e.Name}: {p.File} is shorter than the patch expects.");
                if (h != p.OldSha1 && h != p.NewSha1)
                    throw new InvalidDataException($"Can't {verb} {e.Name}: {p.File} at 0x{p.Offset:X} holds neither the original nor the replacement. The game files changed since this mod was made; nothing was written.");
                held[i] = h;
            }
            Took("check");
            var done = new List<int>();
            // restoring goes backwards, so appended ranges are cut off last-added first
            foreach (int i in toNew ? Enumerable.Range(0, e.Patches.Count) : Enumerable.Range(0, e.Patches.Count).Reverse())
            {
                var p = e.Patches[i];
                string want = toNew ? p.NewSha1 : p.OldSha1;
                // added data comes off after the ranges pointing at it are back (TrimAdded)
                if (p.Append && !toNew) continue;
                var fs = files.Get(p);
                if ((reuse ? held[i] : Current(fs, p)) == want) continue;
                var data = toNew ? kept.New(i) : kept.Old(i);
                if (Hex(SHA1.HashData(data)) != want) throw new InvalidDataException($"The stored {(toNew ? "new" : "old")} bytes of {e.Name} (patch {i}) are damaged.");
                fs.Position = p.Offset;
                fs.Write(data);
                done.Add(i);
                if (p.Append) log?.Invoke($"{p.Note ?? "range"}: {p.Length:N0} bytes at 0x{p.Offset:X} in {p.File}");
            }
            Took("write");
            files.FlushAll();
            Took("flush");
            foreach (int i in done)
            {
                var p = e.Patches[i];
                if (Current(files.Get(p), p) != (toNew ? p.NewSha1 : p.OldSha1))
                    throw new IOException($"{p.File} did not hold what was written at 0x{p.Offset:X}.");
            }
            written = done.Count;
            Took("read back");
        }
        if (!toNew)
        {
            // this mod points at none of its added data now: every other range holds its old bytes (checked or read back above)
            var pointsAt = new Dictionary<ModEntry, bool> { [e] = false };
            foreach (var f in e.Patches.Where(p => p.Append).Select(p => p.File).Distinct(StringComparer.OrdinalIgnoreCase))
                written += TrimAdded(game, f, log, pointsAt);
            Took("cut added data");
        }
        log?.Invoke($"{e.Name}: {written} range(s) written, {e.Patches.Count - written} already as wanted ({string.Join(", ", took)})");
    }

    /// <summary>
    /// A mod not in the game whose added data's place now holds something else (restoring it cut the file, then another
    /// mod added data there): its data goes at the file's end instead, and the TOC offsets it writes follow. Nothing
    /// changes unless every offset can follow.
    /// </summary>
    private void MoveAppended(ModEntry e, string game, Action<string> log)
    {
        using var kept = new Kept(this, e);
        var moves = new List<(int Index, byte[] Data, long At)>();
        var offsets = new Dictionary<uint, uint>();
        foreach (var group in e.Patches.Select((p, i) => (P: p, I: i)).Where(x => x.P.Append).GroupBy(x => x.P.File, StringComparer.OrdinalIgnoreCase))
        {
            string file = Path_(game, group.First().P);
            if (!File.Exists(file)) continue;
            long end = new FileInfo(file).Length;
            var pending = group.Where(x => Current(file, x.P) != x.P.NewSha1).ToList();
            if (pending.All(x => end <= x.P.Offset)) continue;
            foreach (var (p, i) in pending)
            {
                var data = kept.New(i);
                if (Hex(SHA1.HashData(data)) != p.NewSha1) throw new InvalidDataException($"The stored new bytes of {e.Name} (patch {i}) are damaged.");
                long oldAt = (p.Offset + 15) & ~15L, newAt = (end + 15) & ~15L;
                int pad = (int)(oldAt - p.Offset);
                var moved = new byte[newAt - end + data.Length - pad];
                data.AsSpan(pad).CopyTo(moved.AsSpan((int)(newAt - end)));
                if (newAt + data.Length > uint.MaxValue) throw new InvalidDataException($"{p.File} would pass 4 GB, past what a TOC offset holds.");
                if (offsets.ContainsKey((uint)oldAt)) throw new InvalidDataException($"Can't apply {e.Name}: two of its added ranges start at 0x{oldAt:X}, so they can't be moved.");
                offsets[(uint)oldAt] = (uint)newAt;
                moves.Add((i, moved, end));
                end += moved.Length;
            }
        }
        if (moves.Count == 0) return;

        // the TOC offsets that point at the added data, all still as the game shipped them
        var pointers = new List<(int Index, byte[] Data)>();
        var followed = new HashSet<uint>();
        for (int i = 0; i < e.Patches.Count; i++)
        {
            var p = e.Patches[i];
            if (p.Append || p.Length != 4 || p.Note == null || !p.Note.EndsWith(" offset", StringComparison.Ordinal)) continue;
            var cur = kept.New(i);
            uint at = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(cur);
            if (!offsets.TryGetValue(at, out uint to)) continue;
            string file = Path_(game, p);
            if (!File.Exists(file) || Current(file, p) != p.OldSha1)
                throw new InvalidDataException($"Can't apply {e.Name}: {p.File} at 0x{p.Offset:X} isn't as the game shipped it, so this mod's added data can't be moved; nothing was written.");
            var neu = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(neu, to);
            pointers.Add((i, neu));
            followed.Add(at);
        }
        if (followed.Count != offsets.Count)
            throw new InvalidDataException($"Can't apply {e.Name}: its added data's place in the CAS file is taken, and this mod doesn't record every TOC offset that points at it. Restore the mods added after it first; nothing was written.");

        foreach (var (i, data, at) in moves)
        {
            var p = e.Patches[i];
            log?.Invoke($"{p.Note ?? "range"}: its place at 0x{p.Offset:X} in {p.File} is taken, moved to 0x{at:X}");
            WriteNew(e, i, data);
            p.Offset = at;
            p.Length = data.Length;
            p.NewSha1 = Hex(SHA1.HashData(data));
        }
        foreach (var (i, data) in pointers)
        {
            WriteNew(e, i, data);
            e.Patches[i].NewSha1 = Hex(SHA1.HashData(data));
        }
        Save();
    }

    /// <summary>
    /// Cuts data the mod list added off the end of <paramref name="rel"/> while the data at the end belongs to a mod
    /// that isn't applied (nothing points at it any more), so restoring every mod gives back the original file even
    /// when they were restored out of order. Returns how many ranges were cut.
    /// </summary>
    private int TrimAdded(string game, string rel, Action<string> log, Dictionary<ModEntry, bool> pointsAt = null)
    {
        // whether some range of a mod still holds other than its old bytes: read once per mod (cutting added data
        // doesn't change them), not once per cut
        pointsAt ??= new();
        bool StillPoints(ModEntry x)
        {
            if (pointsAt.TryGetValue(x, out bool v)) return v;
            using var files = new GameFiles(game, write: false);
            return pointsAt[x] = x.Patches.Any(p => !p.Append && File.Exists(Path_(game, p)) && Current(files.Get(p), p) is { } h && h != p.OldSha1);
        }
        string file = Path.Combine(game, rel.Replace('/', Path.DirectorySeparatorChar));
        int cut = 0;
        bool Here(RangePatch p) => p.Append && string.Equals(p.File, rel, StringComparison.OrdinalIgnoreCase);
        // the listed mods' data added to this file, by where it ends and where it starts (the list doesn't change while
        // trimming): one lookup per cut, not a walk of every range
        var listed = Entries.SelectMany(x => x.Patches.Where(Here).Select(p => (Entry: x, Patch: p))).ToList();
        var byEnd = listed.GroupBy(t => t.Patch.Offset + t.Patch.Length).ToDictionary(g => g.Key, g => g.First());
        var starts = listed.Select(t => t.Patch.Offset).ToHashSet();
        // walk back from the end over what can go (each range checked as it is: the data its mod or an earlier save
        // added), then cut the file once: one truncation per file, not one per range (thousands for a package's logos)
        var cuts = new List<(ModEntry Entry, RangePatch Patch)>();
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1 << 16))
        {
            long length = fs.Length;
            while (true)
            {
                // a leftover whose place a listed mod uses now is not this file's tail data any more
                var last = byEnd.TryGetValue(length, out var hit) && hit.Patch.Offset < length ? hit
                    : _leftovers.Select(p => (Entry: (ModEntry)null, Patch: p))
                                .FirstOrDefault(t => Here(t.Patch) && !starts.Contains(t.Patch.Offset)
                                                     && t.Patch.Offset + t.Patch.Length == length && t.Patch.Offset < length);
                if (last.Patch == null) break;
                // a listed mod's data only when nothing of its mod points at it
                if (last.Entry != null && StillPoints(last.Entry)) break;
                if (Current(fs, last.Patch) != last.Patch.NewSha1) break;
                cuts.Add(last);
                length = last.Patch.Offset;
            }
            if (cuts.Count > 0)
            {
                fs.SetLength(length);
                fs.Flush(true);
                if (fs.Length != length) throw new IOException($"{rel} was not cut back to 0x{length:X}.");
            }
        }
        long now = new FileInfo(file).Length;
        foreach (var (entry, p) in cuts)
        {
            if (entry == null) _leftovers.Remove(p);
            log?.Invoke($"{p.Note ?? "range"}: cut {rel} back to 0x{p.Offset:X} ({entry?.Name ?? "left by an earlier save"})");
            cut++;
        }
        // leftovers that were cut, or whose place a listed mod uses now, are forgotten
        int dropped = _leftovers.RemoveAll(p => Here(p) && (p.Offset >= now || starts.Contains(p.Offset)));
        if (dropped > 0 || cuts.Any(c => c.Entry == null)) Save();
        return cut;
    }
}
