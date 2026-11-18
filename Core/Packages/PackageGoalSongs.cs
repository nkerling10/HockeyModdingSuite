using System.Buffers.Binary;

namespace NHL27Kit.Core.Packages;

/// <summary>
/// Teams' own goal songs. A team's goal song is exhibitionteams.goalsongid = an audioid from the Creation Zone song list
/// (gamemodes/creation_zone_csv/songs.csv). The songs' audio is one chunk of 425 ATRAC9 streams back to back
/// (sound/music_fb/be/cz/nhl_music_be_cz_wave); its SBle table maps an audioid to a stream (u32 base at 0x110, the
/// sorted keys audioid - base after the u16 sorted-index → stream list at 0x120, stream start offsets at 0x2e6c) and
/// holds no lengths, so a stream rewritten in place, same start and size, needs no table change.
/// A file &lt;goalSongs folder&gt;/&lt;team code&gt;.wav (or .mp3/.flac/… through ffmpeg) takes over one Creation Zone song
/// (a "donor": the package's own list first, then the Misc genre in list order, never the NHL 94 themes, the anthems
/// or a song another team already uses), shortest donor that holds it first. The audio is brought to the game songs'
/// loudness (Loudness.Match, default -10 LUFS) and encoded with the donor's own ATRAC9 settings. In the song picker
/// the donor keeps its name.
/// </summary>
public static class PackageGoalSongs
{
    public const string Bank = "sound/music_fb/be/cz/nhl_music_be_cz_wave";
    public const string SongsCsv = "gamemodes/creation_zone_csv/songs.csv";
    private static readonly HashSet<string> NeverDonate = new(StringComparer.OrdinalIgnoreCase)
        { "NHL94Theme01", "NHL94Theme02", "NHL94Theme03", "StarSpangledBanner", "ScotlandTheBrave" };
    private const int MiscGenre = 8;

    public sealed class Song
    {
        public string Code { get; init; }
        public string File { get; init; }
        public string Donor { get; init; }
        public int AudioId { get; init; }
        public SoundStream Stream { get; init; }
        public double Seconds { get; init; }
    }

    /// <summary>The songs folder (relative to the package), when set and present.</summary>
    public static string Folder(PackageSpec spec)
    {
        if (spec.GoalSongs?.Folder is not { } f) return null;
        string dir = Path.Combine(spec.Folder, f);
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>
    /// Step 3 (database), before the teams step: picks a donor for every team with a song file and sets the team's
    /// goalsongid to it (over a "team" goalsongid setting, with a warning).
    /// </summary>
    public static List<Song> Assign(AssetIndex index, Legacy legacy, T3Db db, PackageSpec spec, PackageInstaller.Result r)
    {
        var list = new List<Song>();
        string dir = Folder(spec);
        if (dir == null)
        {
            if (spec.GoalSongs?.Folder != null) r.Warnings.Add($"goal songs: no folder {spec.GoalSongs.Folder}");
            return list;
        }
        var files = new List<(TeamSpec Team, string File)>();
        foreach (var t in spec.AllTeams)
        {
            var f = AudioFiles.Extensions.Select(e => Path.Combine(dir, t.Code + e)).FirstOrDefault(System.IO.File.Exists);
            if (f != null) files.Add((t, f));
        }
        var known = spec.AllTeams.Select(t => t.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(dir).Where(f => AudioFiles.Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
            if (!known.Contains(Path.GetFileNameWithoutExtension(f)))
                r.Warnings.Add($"goal songs: {Path.GetFileName(f)} isn't named after one of the package's teams (team code, e.g. MINN.wav); left out");
        if (files.Count == 0) return list;

        // the bank: streams, and audioid → stream from its table
        var wave = index.GetEbx(Bank) ?? throw new InvalidDataException($"No sound {Bank}.");
        var streams = Audio.Variations(index, wave);
        var byAudio = StreamsByAudioId(index.Read(index.GetRes(Bank) ?? throw new InvalidDataException($"No table for {Bank}.")), streams);

        // songs.csv: identifier, audioid, genre, isgoalsong (latin-1)
        var rows = new List<(string Id, int Audio, int Genre, bool Goal)>();
        var lines = System.Text.Encoding.Latin1.GetString(legacy.Read(SongsCsv)).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        var head = lines[0].Split(',');
        int Col(string n) => Array.IndexOf(head, n);
        int cId = Col("identifier"), cAudio = Col("audioid"), cGenre = Col("genre"), cGoal = Col("isgoalsong");
        foreach (var l in lines.Skip(1))
        {
            var c = l.Split(',');
            if (c.Length <= Math.Max(cGoal, cGenre) || !int.TryParse(c[cAudio], out int a) || a <= 0) continue;
            rows.Add((c[cId], a, int.TryParse(c[cGenre], out int g) ? g : -1, c[cGoal] == "1"));
        }

        // songs other teams already use
        var teams = db.Table("exhibitionteams");
        var gf = teams.Fields.First(x => x.Name == "goalsongid");
        var ours = spec.AllTeams.Select(t => t.Index).ToHashSet();
        var idx = teams.Fields.First(x => x.Name == "index");
        var taken = new HashSet<int>();
        for (int i = 0; i < teams.WrittenRecords; i++)
            if (!db.IsInvalid(teams, i) && !ours.Contains(Convert.ToInt32(db.Get(teams, i, idx))))
                taken.Add(Convert.ToInt32(db.Get(teams, i, gf)));

        bool Usable((string Id, int Audio, int Genre, bool Goal) s) =>
            s.Goal && byAudio.ContainsKey(s.Audio) && !NeverDonate.Contains(s.Id) && !taken.Contains(s.Audio);
        var pinned = (spec.GoalSongs.Donors ?? new()).Select(d => rows.FirstOrDefault(s => s.Id.Equals(d, StringComparison.OrdinalIgnoreCase) || s.Audio.ToString() == d)).ToList();
        foreach (var (d, s) in (spec.GoalSongs.Donors ?? new()).Zip(pinned))
            if (s.Id == null) r.Warnings.Add($"goal songs: donor \"{d}\" isn't in songs.csv");
            else if (!Usable(s)) r.Warnings.Add($"goal songs: donor {s.Id} can't be used (not a goal song in this bank, kept, or another team's song)");
        var pool = pinned.Where(s => s.Id != null && Usable(s))
            .Concat(rows.Where(s => s.Genre == MiscGenre && Usable(s)))
            .DistinctBy(s => s.Audio).ToList();

        foreach (var (t, file) in files)
        {
            var (pcm, ch, rate) = AudioFiles.Read(file);
            double secs = pcm.Length / (double)ch / rate;
            // the first pinned donor that holds it; then the shortest Misc donor that holds it; else the longest left
            var fits = pool.Where(s => byAudio[s.Audio].Header.Seconds >= secs - 0.05).ToList();
            var pick = fits.FirstOrDefault(s => pinned.Any(p => p.Audio == s.Audio));
            if (pick.Id == null) pick = fits.OrderBy(s => byAudio[s.Audio].Header.Seconds).FirstOrDefault();
            if (pick.Id == null)
            {
                pick = pool.OrderByDescending(s => byAudio[s.Audio].Header.Seconds).FirstOrDefault();
                if (pick.Id == null) { r.Warnings.Add($"goal songs: no Creation Zone song left for {t.Code}"); continue; }
                r.Warnings.Add($"goal songs: {t.Code}'s song ({secs:0.0} s) is longer than every donor left; it goes over {pick.Id} ({byAudio[pick.Audio].Header.Seconds:0.0} s) at a lower bit rate or cut short");
            }
            pool.Remove(pick);
            if (t.TeamFields?.ContainsKey("goalsongid") == true)
                r.Warnings.Add($"goal songs: {t.Code} has a goal song file and a \"team\" goalsongid; the file wins");
            t.TeamFields ??= new();
            t.TeamFields["goalsongid"] = pick.Audio;
            list.Add(new Song { Code = t.Code, File = file, Donor = pick.Id, AudioId = pick.Audio, Stream = byAudio[pick.Audio], Seconds = secs });
        }
        return list;
    }

    /// <summary>Step 4 (bytes): every song loudness-matched, encoded over its donor in one copy of the bank, planned once.</summary>
    public static void Plan(AssetIndex index, PackageSpec spec, List<Song> songs, LocationIndex locations, TocCache tocs, List<PatchData> patches, PackageInstaller.Result r)
    {
        if (songs.Count == 0) return;
        double target = spec.GoalSongs?.Loudness ?? -10;
        var chunkId = songs[0].Stream.ChunkId;
        var raw = index.Read(songs[0].Stream.Chunk);
        foreach (var s in songs)
        {
            var notes = new List<string>();
            var (pcm, ch, rate) = AudioFiles.Read(s.File);
            var mapped = Replacements.ToStreamLayout(pcm, ch, rate, s.Stream.Header, notes);
            var (loud, before, after, limit) = Loudness.Match(mapped, s.Stream.Header.Channels, target);
            var (sps, bitrate) = Replacements.EncodeAtrac9Into(raw, s.Stream, loud, s.Stream.Header.Channels, notes);
            r.Lines.Add($"goal song {s.Code}: {Path.GetFileName(s.File)} ({s.Seconds:0.0} s) over \"{s.Donor}\" (audioid {s.AudioId}, {s.Stream.Header.Seconds:0.0} s), " +
                        $"loudness {before:0.0} → {after:0.0} LUFS (limiter up to {limit:0.0} dB), ATRAC9 {bitrate / 1000} kbps/ch, {sps.Length:N0} of {s.Stream.Length:N0} bytes");
            foreach (var n in notes) r.Lines.Add($"  {s.Code}: {n}");
        }
        var plan = ChunkPatcher.Plan(index, chunkId, raw, locations, tocs);
        patches.AddRange(plan.Patches);
        r.Lines.Add($"goal songs: {songs.Count} song(s) in {Bank}, {plan.Patches.Count} byte range(s)");
    }

    /// <summary>The bank's table (SBle): audioid → stream, checked against the chunk's own streams.</summary>
    private static Dictionary<int, SoundStream> StreamsByAudioId(byte[] t, List<SoundStream> streams)
    {
        const int CountAt = 0xa8, BaseAt = 0x110, PermAt = 0x120, OffsetsAt = 0x2e6c;
        if (t.Length < OffsetsAt || t[0] != 'S' || t[1] != 'B' || t[2] != 'l' || t[3] != 'e')
            throw new InvalidDataException($"{Bank}'s table isn't the SBle layout this version knows.");
        int n = BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(CountAt));
        if (n != streams.Count || OffsetsAt + 4 * n > t.Length)
            throw new InvalidDataException($"{Bank}: the table lists {n} songs, the chunk holds {streams.Count} streams.");
        int keysAt = (PermAt + 2 * n + 3) / 4 * 4 + 4 * n;               // after the sorted-index list and one u32 list
        int baseId = BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(BaseAt));
        for (int i = 0; i < n; i++)
            if (BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(OffsetsAt + 4 * i)) != streams[i].Offset)
                throw new InvalidDataException($"{Bank}: the table's stream {i} start doesn't match the chunk; the layout isn't the one this version knows.");
        var map = new Dictionary<int, SoundStream>();
        for (int j = 0; j < n; j++)
        {
            int audio = baseId + BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(keysAt + 4 * j));
            int stream = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(PermAt + 2 * j));
            if (stream < n && streams[stream].Header != null) map[audio] = streams[stream];
        }
        return map;
    }
}
