using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using K4os.Compression.LZ4.Streams;

namespace NHL27Kit.Core;

/// <summary>One of the emulator's franchise save slots (BeAGM1…), with the summary the game writes beside it.</summary>
public sealed record FranchiseSlot(string Dir, string Name, string Title, string Team, string UserName, int Season, bool IsBackup, DateTime Modified)
{
    public string SeasonText => Season > 19000000 ? new DateTime(Season / 10000, Season / 100 % 100, Season % 100).ToString("MMMM d, yyyy") : "";
    public string Detail => string.Join("  ·  ", new[] { UserName, SeasonText }.Where(s => !string.IsNullOrEmpty(s)));
    public string Folder => Path.GetDirectoryName(Dir);
}

/// <summary>How a player-record field is stored.</summary>
public enum FranchiseFieldFormat { U8, S8, S16, U16, S32 }

/// <summary>Which player records a field is in: both, skaters (964 bytes) only or goalies (896 bytes) only.</summary>
public enum FranchiseFieldKind { Both, Skater, Goalie }

/// <summary>
/// A field of the franchise player record. Key is the game database's field name (exhibitionplayerbiotable,
/// exhibitionskaterai, exhibitiongoalieai); the value is stored as the database stores it, and Add / Scale give what
/// the game shows (height + 54 in, weight + 120 lb, years + 1900, salaries × 5,000, 0-based day and month + 1).
/// Confirmed marks offsets matched against the game database for every player that has a database row.
/// </summary>
public sealed record FranchiseField(string Key, string Group, int Offset, FranchiseFieldFormat Format, FranchiseFieldKind Kind = FranchiseFieldKind.Both,
                                    int Add = 0, int Scale = 1, bool Confirmed = true)
{
    public bool AppliesTo(bool goalie) => Kind == FranchiseFieldKind.Both || (Kind == FranchiseFieldKind.Goalie) == goalie;
}

/// <summary>A player record of the franchise's players block.</summary>
public sealed class FranchisePlayer
{
    private readonly byte[] _data;

    internal FranchisePlayer(byte[] data, int index, int offset, int size)
    {
        _data = data;
        Index = index;
        Offset = offset;
        Size = size;
    }

    /// <summary>The record number: the playerindex the league tables (rosters, draft picks) use.</summary>
    public int Index { get; }
    /// <summary>Where the record starts in the unpacked players block.</summary>
    public int Offset { get; }
    public int Size { get; }
    public bool IsGoalie => Size == FranchiseSave.GoalieSize;

    public string FirstName => Text(0x28, 25);
    public string LastName => Text(0x41, 25);
    public string Name => $"{FirstName} {LastName}".Trim();
    public string Birthplace => Text(0x00, 40);
    public string Nickname => Text(0x5A, 25);
    /// <summary>The team row (leagueteams) the player is on now, AHL assignments included; -1 for none.</summary>
    public int CurrentTeam => BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(Offset + 0x11A));
    /// <summary>0 left, 1 right (catching hand for goalies).</summary>
    public int Handedness => _data[Offset + 0x15E];
    public int Raw(FranchiseField f) => Read(f.Offset, f.Format);
    /// <summary>The value as the game shows it (see <see cref="FranchiseField"/>).</summary>
    public long Value(FranchiseField f) => (long)Raw(f) * f.Scale + f.Add;
    public int Value(string key) => FranchiseSave.Field(key, IsGoalie) is { } f ? (int)Value(f) : 0;
    /// <summary>The record's bytes (a copy), for the raw view.</summary>
    public byte[] Bytes() => _data.AsSpan(Offset, Size).ToArray();

    private string Text(int at, int len)
    {
        var s = _data.AsSpan(Offset + at, len);
        int end = s.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? s : s[..end]);
    }

    private int Read(int at, FranchiseFieldFormat f)
    {
        var s = _data.AsSpan(Offset + at);
        return f switch
        {
            FranchiseFieldFormat.U8 => s[0],
            FranchiseFieldFormat.S8 => (sbyte)s[0],
            FranchiseFieldFormat.S16 => BinaryPrimitives.ReadInt16LittleEndian(s),
            FranchiseFieldFormat.U16 => BinaryPrimitives.ReadUInt16LittleEndian(s),
            _ => BinaryPrimitives.ReadInt32LittleEndian(s),
        };
    }
}

/// <summary>A block of the franchise stream (see <see cref="FranchiseSave"/>).</summary>
public sealed record FranchiseBlock(int Index, uint Id, string Name, string Kind, int Offset, int StoredSize, int Size, string Notes);

/// <summary>
/// A franchise save from KytyPS5's save folder (unencrypted), read only. The container is Be A Pro's (see
/// <see cref="BeAProSave"/> and docs/beapro/README.md): INDEX lists the files packed into DATA; the franchise file is
/// u64 length, an 8-byte save id, the byte 1, an LZ4 frame and the u32 length it unpacks to; that stream is blocks and
/// a table of (u32 id, u32 offset, u32 stored size, u32 unpacked size) entries with a u16 count. The players block is
/// u32 byte length, u32 count, then records of 964 bytes (skaters) or 896 (goalies, byte 0x1AC == 4). The league block
/// is a t3db with the 33 league* tables of nhl_game.db (read with nhl_game-meta.xml for its names).
/// </summary>
public sealed class FranchiseSave
{
    public const int SkaterSize = 964, GoalieSize = 896;

    private static readonly Dictionary<uint, (string Name, string Notes)> BlockNames = new()
    {
        [0x1B9A22BB] = ("Franchise state", "Numbered settings, people and history (the Be A Pro career-state block)"),
        [BeAProSave.PlayersBlock] = ("Players", "Player records: bio, contract, draft, ratings, X-Factors"),
        [BeAProSave.LeagueBlock] = ("League database", "t3db: the league* tables of nhl_game (teams, rosters, GMs, picks, schedule…)"),
        [0x5858B61E] = ("Key/value table", "(u32 value, u32 key) pairs"),
        [0xD1EC0169] = ("Standings?", "Ten-byte records, likely standings"),
        [0xAFBC0632] = ("Mostly empty", ""),
    };

    public string Dir { get; }
    public FranchiseSlot Slot { get; }
    public List<FranchiseBlock> Blocks { get; } = new();
    public List<FranchisePlayer> Players { get; } = new();
    /// <summary>The league database, named by the meta XML when one was given.</summary>
    public T3Db League { get; }
    /// <summary>The unpacked bytes of a block by id (null when the save has none).</summary>
    public byte[] BlockData(uint id) => _data.TryGetValue(id, out var d) ? d : null;

    private readonly Dictionary<uint, byte[]> _data = new();

    // ================================================================ slots

    /// <summary>The franchise slots in a save folder (…\_SaveData\PPSA34063), newest first.</summary>
    public static List<FranchiseSlot> Slots(string savesDir)
    {
        var list = new List<FranchiseSlot>();
        if (!Directory.Exists(savesDir)) return list;
        foreach (var dir in Directory.GetDirectories(savesDir))
            if (ReadSlot(dir) is { } s) list.Add(s);
        return list.OrderByDescending(s => s.Modified).ToList();
    }

    /// <summary>A slot's summary when it's a franchise save (param.bin says Franchise, or the slot is named BeAGM…), else null.</summary>
    public static FranchiseSlot ReadSlot(string dir)
    {
        try
        {
            string data = Path.Combine(dir, "DATA"), index = Path.Combine(dir, "INDEX");
            if (!File.Exists(data) || !File.Exists(index)) return null;
            string name = Path.GetFileName(dir);
            string title = ParamTitle(dir);
            bool franchise = title.Contains("Franchise", StringComparison.OrdinalIgnoreCase) || name.StartsWith("BeAGM", StringComparison.OrdinalIgnoreCase);
            if (!franchise || name.StartsWith("BeAPro", StringComparison.OrdinalIgnoreCase)) return null;
            var meta = ReadPacked(dir).FirstOrDefault(f => f.Name == "__SaveLoadMetaData").Bytes;
            string team = "", user = "";
            int season = 0;
            bool backup = false;
            if (meta != null && meta.Length > 16)
            {
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(meta, 16, meta.Length - 16).TrimEnd('\0'));
                var r = doc.RootElement;
                string S(string k) => r.TryGetProperty(k, out var v) ? v.ToString() : "";
                team = S("TeamFullName");
                user = S("UserName");
                int.TryParse(S("SeasonYear"), out season);
                backup = S("IsBackup").Equals("True", StringComparison.OrdinalIgnoreCase);
            }
            return new FranchiseSlot(dir, name, title, team, user, season, backup, File.GetLastWriteTime(data));
        }
        catch
        {
            return null; // not a save we can read
        }
    }

    /// <summary>The title in sce_sys/param.bin ("Franchise Mode 2"), or "".</summary>
    private static string ParamTitle(string dir)
    {
        string p = Path.Combine(dir, "sce_sys", "param.bin");
        if (!File.Exists(p)) return "";
        byte[] b = File.ReadAllBytes(p);
        int end = Array.IndexOf(b, (byte)0, 0, Math.Min(b.Length, 0x80));
        return Encoding.UTF8.GetString(b, 0, end < 0 ? Math.Min(b.Length, 0x80) : end);
    }

    /// <summary>
    /// The files INDEX lists, in the order it lists them (which varies: the franchise file isn't always first). An entry
    /// is the name (0x20 bytes), u64 extent count, u64 size, then per extent u64 start, u64 end and two u64s; a file that
    /// outgrew its place in DATA is split into extents, joined in order (seen once a season is under way).
    /// </summary>
    private static List<(string Name, byte[] Bytes)> ReadPacked(string dir)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(dir, "DATA")), index = File.ReadAllBytes(Path.Combine(dir, "INDEX"));
        var list = new List<(string, byte[])>();
        for (int e = 0; e + 0x30 <= index.Length; )
        {
            string name = Encoding.ASCII.GetString(index, e, 0x20).TrimEnd('\0');
            long count = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(e + 0x20)), size = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(e + 0x28));
            if (count < 0 || count > 64 || e + 0x30 + count * 0x20 > index.Length) throw new InvalidDataException($"INDEX entry {name} has {count} extents");
            var bytes = new MemoryStream();
            for (int x = 0; x < count; x++)
            {
                int at = e + 0x30 + x * 0x20;
                long start = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(at)), end = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(at + 8));
                if (start < 0 || end > data.Length || end < start) throw new InvalidDataException($"INDEX entry {name} is out of range");
                bytes.Write(data, (int)start, (int)(end - start));
            }
            if (bytes.Length != size) throw new InvalidDataException($"INDEX entry {name}: its extents hold {bytes.Length:N0} bytes, not {size:N0}");
            if (name.Length > 0) list.Add((name, bytes.ToArray()));
            e += 0x30 + (int)count * 0x20;
        }
        return list;
    }

    // ================================================================ open

    /// <summary>Reads a franchise slot. <paramref name="metaXml"/> (nhl_game-meta.xml) names the league tables.</summary>
    public static FranchiseSave Open(string dir, string metaXml) => new(dir, metaXml);

    private FranchiseSave(string dir, string metaXml)
    {
        Dir = dir;
        Slot = ReadSlot(dir) ?? new FranchiseSlot(dir, Path.GetFileName(dir), "", "", "", 0, false, DateTime.MinValue);
        var file = ReadPacked(dir).FirstOrDefault(f => !f.Name.StartsWith("__")).Bytes ?? throw new InvalidDataException("No franchise file in this save");
        int rawLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(file.Length - 4));
        byte[] stream = Unpack(file.AsSpan(16, file.Length - 20), rawLength);

        int count = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(stream.Length - 2));
        int table = stream.Length - 2 - count * 16;
        for (int i = 0; i < count; i++)
        {
            var e = stream.AsSpan(table + i * 16);
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(e);
            int off = BinaryPrimitives.ReadInt32LittleEndian(e[4..]), stored = BinaryPrimitives.ReadInt32LittleEndian(e[8..]), raw = BinaryPrimitives.ReadInt32LittleEndian(e[12..]);
            var bytes = stream.AsSpan(off, stored);
            byte[] data = raw == 0 ? bytes.ToArray() : Unpack(bytes, raw);
            _data[id] = data;
            string kind = raw == 0 ? "stored" : bytes[0] == 1 ? "LZ4" : "raw";
            var (name, notes) = BlockNames.GetValueOrDefault(id, ("", ""));
            Blocks.Add(new FranchiseBlock(i, id, name, kind, off, stored, data.Length, notes));
        }

        if (BlockData(BeAProSave.PlayersBlock) is { } players) ReadPlayers(players);
        if (BlockData(BeAProSave.LeagueBlock) is { } league) League = new T3Db(league, metaXml);
    }

    private void ReadPlayers(byte[] d)
    {
        int length = BinaryPrimitives.ReadInt32LittleEndian(d), count = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(4));
        int p = 8, end = 4 + length;
        for (int i = 0; i < count && p + GoalieSize <= end; i++)
        {
            int size = d[p + 0x1AC] == 4 ? GoalieSize : SkaterSize;
            Players.Add(new FranchisePlayer(d, i, p, size));
            p += size;
        }
        if (p != end) throw new InvalidDataException($"The players block didn't add up: {Players.Count:N0} records end at {p:N0}, not {end:N0}.");
    }

    /// <summary>Unpacks a tagged block (0 = bytes as they are, 1 = an LZ4 frame).</summary>
    private static byte[] Unpack(ReadOnlySpan<byte> tagged, int raw)
    {
        if (tagged[0] == 0) return tagged[1..].ToArray();
        if (tagged[0] != 1) throw new InvalidDataException($"Unknown block packing {tagged[0]}");
        using var src = LZ4Stream.Decode(new MemoryStream(tagged[1..].ToArray()));
        var dst = new byte[raw];
        int n = 0, r;
        while (n < raw && (r = src.Read(dst, n, raw - n)) > 0) n += r;
        if (n != raw) throw new InvalidDataException($"A block unpacked to {n:N0} bytes, not {raw:N0}");
        return dst;
    }

    // ================================================================ player fields

    /// <summary>A franchise date (days since January 1, 1900: leaguestates key 0, the player's injury dates…), or null for 0.</summary>
    public static DateTime? Date(int days) => days > 0 ? new DateTime(1900, 1, 1).AddDays(days) : null;

    /// <summary>
    /// The game's 45 injuries (injurytype), from the injury table in eboot.bin (file 0xFAFB850): each has three names,
    /// most serious first, and which one the injury screen shows depends on the injury and its length (20 days of knee
    /// is "an MCL sprain", 20 days of shoulder "a sore shoulder"). Ids 0-29 are skater injuries and 30-44 goalie ones;
    /// a goalie injury on a skater crashes the injury report (its ratings penalty isn't there).
    /// </summary>
    public static readonly string[][] InjuryNames =
    {
        new[] { "The flu", "The flu", "The flu" },
        new[] { "A hernia", "An abdominal strain", "An injured abdomen" },
        new[] { "A broken rib", "Bruised ribs", "Injured ribs" },
        new[] { "A herniated disc", "Back spasms", "An injured back" },
        new[] { "A separated shoulder", "A sore shoulder", "An injured shoulder" },
        new[] { "A separated shoulder", "A sore shoulder", "An injured shoulder" },
        new[] { "A fractured collarbone", "A neck strain", "An injured neck" },
        new[] { "Post-concussion syndrome", "An upper body injury", "A mild concussion" },
        new[] { "A broken nose", "A broken nose", "A broken nose" },
        new[] { "A fractured jaw", "A fractured jaw", "A fractured jaw" },
        new[] { "The flu", "The flu", "The flu" },
        new[] { "A broken left foot", "A sore foot", "An injured foot" },
        new[] { "A broken right foot", "A sore foot", "An injured foot" },
        new[] { "A fractured ankle", "A sprained ankle", "An injured ankle" },
        new[] { "A fractured ankle", "A sprained ankle", "An injured ankle" },
        new[] { "An MCL sprain", "A sore knee", "An injured knee" },
        new[] { "An MCL sprain", "A sore knee", "An injured knee" },
        new[] { "A broken leg", "A strained hamstring", "An injured leg" },
        new[] { "A broken leg", "A strained hamstring", "An injured leg" },
        new[] { "A pulled groin", "A pulled groin", "An injured groin" },
        new[] { "A hip pointer", "A sore hip", "An injured hip" },
        new[] { "A hip pointer", "A sore hip", "An injured hip" },
        new[] { "A lacerated arm", "A bruised arm", "An injured arm" },
        new[] { "A lacerated arm", "A bruised arm", "An injured arm" },
        new[] { "A broken wrist", "A wrist sprain", "An injured wrist" },
        new[] { "A broken wrist", "A wrist sprain", "An injured wrist" },
        new[] { "Elbow surgery", "A fractured left elbow", "An injured elbow" },
        new[] { "Elbow surgery", "A fractured right elbow", "An injured elbow" },
        new[] { "A broken hand", "A bruised hand", "An injured hand" },
        new[] { "A broken hand", "A bruised hand", "An injured hand" },
        // goalies
        new[] { "A torn groin", "A pulled groin", "An injured groin" },
        new[] { "A hip pointer", "A bruised hip", "An injured hip" },
        new[] { "A broken collarbone", "A fractured collarbone", "An injured arm" },
        new[] { "A herniated disc", "Back spasms", "An injured back" },
        new[] { "Post-concussion syndrome", "A mild concussion", "An upper body injury" },
        new[] { "A separated shoulder", "A bruised shoulder", "An injured shoulder" },
        new[] { "An MCL sprain", "A swollen knee", "An injured knee" },
        new[] { "A broken ankle", "A sprained ankle", "An injured ankle" },
        new[] { "A broken toe", "A broken toe", "An injured toe" },
        new[] { "A broken foot", "A bruised foot", "An injured foot" },
        new[] { "A torn hamstring", "A strained hamstring", "An injured hamstring" },
        new[] { "A broken finger", "A sprained finger", "An injured finger" },
        new[] { "A broken wrist", "A sprained wrist", "An injured wrist" },
        new[] { "A broken thumb", "A sprained thumb", "An injured thumb" },
        new[] { "A broken hand", "A bruised hand", "An injured hand" },
    };

    /// <summary>First goalie injury id: 0-29 are for skaters, 30-44 for goalies.</summary>
    public const int FirstGoalieInjury = 30;

    /// <summary>"A sore knee / an MCL sprain…" for an injury id, or "type N".</summary>
    public static string InjuryName(int id) => id >= 0 && id < InjuryNames.Length
        ? string.Join(" / ", InjuryNames[id].Distinct()) : $"type {id}";

    /// <summary>A field by its database name for a skater's or a goalie's record (X-Factors sit at different places), or null.</summary>
    public static FranchiseField Field(string key, bool goalie) =>
        Fields.FirstOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && f.AppliesTo(goalie));

    /// <summary>
    /// The player-record fields worked out so far: matched by game_id against the game database's player tables (545
    /// skaters and 99 goalies; see franchise-patch-research/FINDINGS.md). Skaters and goalies share everything up to
    /// 0x1D5; after that the two records differ. Ratings at 0x1BE-0x1C7 and 0x21A-0x22B keep base copies elsewhere in
    /// the record (+0x2B, and +0x12 / +0x4F), which an editor has to move together.
    /// </summary>
    public static readonly FranchiseField[] Fields = Build();

    private static FranchiseField[] Build()
    {
        const FranchiseFieldFormat u8 = FranchiseFieldFormat.U8, s8 = FranchiseFieldFormat.S8, s16 = FranchiseFieldFormat.S16, s32 = FranchiseFieldFormat.S32;
        const FranchiseFieldKind sk = FranchiseFieldKind.Skater, gk = FranchiseFieldKind.Goalie;
        var f = new List<FranchiseField>
        {
            // identity and ids
            new("game_id", "Identity", 0x074, s32), new("headid", "Identity", 0x078, s32), new("surnameid", "Identity", 0x090, s16),
            new("artid", "Identity", 0x09C, s16), new("audioid", "Identity", 0x09E, s16), new("personalitytype", "Identity", 0x084, s8),
            new("jersey", "Identity", 0x175, s8), new("intlcountry", "Identity", 0x16A, s8),
            // bio
            new("height", "Bio", 0x160, s8, Add: 54), new("weight", "Bio", 0x1BA, u8, Add: 120),
            new("day", "Bio", 0x132, s8, Add: 1), new("month", "Bio", 0x133, s8, Add: 1), new("year", "Bio", 0x135, s8, Add: 1900),
            new("state", "Bio", 0x134, s8), new("handedness", "Bio", 0x15E, u8),
            // position and teams
            new("position", "Position", 0x1AC, s8), new("positionprimary", "Position", 0x1AD, s8), new("positionsecondary", "Position", 0x1AE, s8),
            new("currentteam", "Position", 0x11A, s16), new("proteam", "Position", 0x1AF, u8),
            // contract
            new("contractdollars", "Contract", 0x0A0, s16, Scale: 5000), new("contractlength", "Contract", 0x14C, s8),
            new("extensiondollars", "Contract", 0x0A6, s16, Scale: 5000), new("extensionlength", "Contract", 0x157, s8),
            new("yearfirstsigned", "Contract", 0x128, s8, Add: 1900), new("yearwaivereligible", "Contract", 0x12C, u8, Add: 1900),
            new("yearufa", "Contract", 0x1BB, u8, Add: 1900), new("expansiondraftstatus", "Contract", 0x155, s8),
            // draft
            new("draftyear", "Draft", 0x154, u8, Add: 1900), new("draftround", "Draft", 0x152, s8), new("draftposition", "Draft", 0x0A4, u8),
            new("draftteam", "Draft", 0x153, s8), new("juniorleague", "Draft", 0x178, s8),
            // career
            new("yearprodebut", "Career", 0x12A, s8, Add: 1900), new("nhlgamesplayedcareer", "Career", 0x10C, s16),
            new("nhlcareergamesplayedregularseason", "Career", 0x108, s16), new("nhlcareergoalsregularseason", "Career", 0x10A, s16),
            new("progamesplayedcareer", "Career", 0x114, s16), new("nhlgamesplayedlastseason", "Career", 0x196, s8),
            new("nhlgamesplayedtwoseasonsago", "Career", 0x19A, s8), new("nhlgamesplayedthreeseasonsago", "Career", 0x199, s8),
            new("cupswon", "Career", 0x14E, s8), new("cupsyearlastwon", "Career", 0x0A2, s8),
            // ratings both kinds share
            new("skill_overall", "Ratings", 0x1C3, s8), new("c_aggression", "Ratings", 0x1BE, s8), new("c_agility", "Ratings", 0x1BF, s8),
            new("c_durability", "Ratings", 0x1C0, s8), new("c_endurance", "Ratings", 0x1C1, s8), new("c_fighting", "Ratings", 0x1C2, s8),
            new("c_passing", "Ratings", 0x1C4, s8), new("c_poise", "Ratings", 0x1C5, s8), new("c_speed", "Ratings", 0x1C7, s8),
            new("growthletter", "Growth", 0x1D2, s8), new("growthspeed", "Growth", 0x1D4, s8), new("growthtier", "Growth", 0x1D5, s8),
            // appearance
            new("skincolor", "Appearance", 0x1B3, s8), new("hairid", "Appearance", 0x0CE, s8), new("haircolorcode", "Appearance", 0x15D, s8),
            new("eyecolorcode", "Appearance", 0x15B, s8), new("eyebrowid", "Appearance", 0x15A, s8), new("eyebrowcolorcode", "Appearance", 0x159, s8),
            new("swagger", "Appearance", 0x1B4, s8), new("playoffbeardtype", "Appearance", 0x1AB, s8), new("playoffbeardcolor", "Appearance", 0x1A7, s8),
            new("playoffbeardgrowthrate", "Appearance", 0x1A8, s8), new("playoffbeardstart", "Appearance", 0x1AA, s8), new("playoffbeardmax", "Appearance", 0x1A9, s8),
            // found by comparing saves (no database counterpart, so the names are the kit's): see FINDINGS.md §8-10
            new("contractis2way", "Contract", 0x16C, u8),
            new("extensioninterest", "Extension talks", 0x158, u8), // the EXT column: 1 No, 3 and 6 Yes (2, 4, 5 not seen on screen); 0 no talks
            new("extensionask", "Extension talks", 0x0A8, FranchiseFieldFormat.U16, Scale: 5000),
            new("extensionaskdate", "Extension talks", 0x0AA, FranchiseFieldFormat.U16),
            new("extensionaskyears", "Extension talks", 0x156, u8),
            new("injurytype", "Injury", 0x169, u8), new("injuryseverity", "Injury", 0x168, u8),
            new("injurydate", "Injury", 0x0D2, FranchiseFieldFormat.U16), new("injuryreturndate", "Injury", 0x0D4, FranchiseFieldFormat.U16),
            new("injurylength", "Injury", 0x166, u8), new("injurydaysleft", "Injury", 0x162, u8),
            new("injuryevaluation", "Injury", 0x163, u8), // non-zero while "pending evaluation"
            new("injurygamesmissed", "Injury", 0x165, u8, Confirmed: false),
            new("seasongamesplayed", "Career", 0x198, u8),
            new("retirementage", "Career", 0x1D1, u8, Confirmed: false),
        };
        // trade protection: one clause and team list per season
        for (int i = 0; i < 9; i++)
        {
            f.Add(new($"contractclause{i}", "Trade protection", 0x137 + i, s8));
            f.Add(new($"contractclausemntcnumteams{i}", "Trade protection", 0x141 + i, s8));
        }
        // skater ratings, in the record's order
        string[] skater = { "c_acceleration", "c_balance", "c_bodychecking", "c_defawareness", "c_deking", "c_discipline", "c_faceoffs", "c_fightingskill",
            "c_handeye", "c_offawareness", "c_puckcontrol", "c_shotblocking", "c_slapaccuracy", "c_slapshotpower", "c_stickchecking", "c_strength",
            "c_wristaccuracy", "c_wristshotpower" };
        for (int i = 0; i < skater.Length; i++) f.Add(new(skater[i], "Ratings", 0x21A + i, s8, sk));
        string[] category = { "skill_defense", "skill_physical", "skill_puck", "skill_sense", "skill_shoot", "skill_skate" };
        for (int i = 0; i < category.Length; i++) f.Add(new(category[i], "Category ratings", 0x28B + i, s8, sk));
        f.Add(new("playerstyle", "Style", 0x1DF, s8, sk));
        f.Add(new("off_defbias", "Style", 0x253, s8, sk));
        f.Add(new("pass_carrybias", "Style", 0x255, s8, sk));
        f.Add(new("shoot_passbias", "Style", 0x268, s8, sk));
        f.Add(new("slapshot", "Style", 0x2A0, s8, sk));
        f.Add(new("goalcelebration", "Style", 0x294, s16, sk));
        f.Add(new("skatingstyle", "Style", 0x2A8, s32, sk));
        for (int i = 0; i <= 14; i++)
            if (i != 9) f.Add(new($"skatingstyle{i}", "Skating style", 0x2AC + i * 4, s32, sk)); // 9 shares 7's slot in the shipped roster: not placed
        f.Add(new("trait0", "Traits", 0x1F2, s8));
        for (int i = 1; i <= 13; i++) f.Add(new($"trait{i}", "Traits", 0x1F2 + i, s8, sk));
        for (int i = 0; i < 3; i++)
        {
            f.Add(new($"xfactor{i + 1}ability", "X-Factors", 0x2E8 + i * 4, s32, sk));
            f.Add(new($"xfactor{i + 1}tier", "X-Factors", 0x2F4 + i * 4, s32, sk));
        }
        // goalie ratings
        (string Key, int Off)[] goalie = { ("c_5hole", 0x21A), ("c_breakaway", 0x21B), ("c_consistency", 0x21C), ("c_gsh", 0x21D), ("c_gsl", 0x21E),
            ("c_pokecheck", 0x220), ("c_positioning", 0x221), ("c_puckplayfrequency", 0x222), ("c_reboundcontrol", 0x223), ("c_shotrecover", 0x224),
            ("c_ssh", 0x225), ("c_ssl", 0x226), ("c_vision", 0x227) };
        foreach (var (key, off) in goalie) f.Add(new(key, "Ratings", off, s8, gk));
        f.Add(new("skill_athletics", "Category ratings", 0x25D, s8, gk));
        f.Add(new("skill_control", "Category ratings", 0x25E, s8, gk));
        f.Add(new("skill_reflex", "Category ratings", 0x25F, s8, gk));
        f.Add(new("stance", "Style", 0x260, s8, gk));
        f.Add(new("xfactor1ability", "X-Factors", 0x264, s32, gk));
        f.Add(new("xfactor1tier", "X-Factors", 0x270, s32, gk));
        // likely, by the layout of slot 1 (not matched: few goalies have a second or third X-Factor)
        f.Add(new("xfactor2ability", "X-Factors", 0x268, s32, gk, Confirmed: false));
        f.Add(new("xfactor3ability", "X-Factors", 0x26C, s32, gk, Confirmed: false));
        f.Add(new("xfactor2tier", "X-Factors", 0x274, s32, gk, Confirmed: false));
        f.Add(new("xfactor3tier", "X-Factors", 0x278, s32, gk, Confirmed: false));
        return f.ToArray();
    }
}
