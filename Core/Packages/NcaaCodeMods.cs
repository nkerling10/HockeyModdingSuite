using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace NHL27Kit.Core.Packages;

/// <summary>
/// Builds the "NCAA franchise rules" Kyty code mod for NHL 27 1.003.001 (franchise-patch-research/NCAA_ROADMAP.md,
/// same as eboot_tools/build_school_patch.py):
/// 1. generated prospects from the three US scouting regions pick a random team from the team-index block
///    <c>first..last</c> (league 32) instead of the generic USA team 337; the junior-team picker's WHL branch is
///    rewritten as a loop to free the space, and its candidate list grows from 30 to 96;
/// 2. the daily junior stat generator skips US regions (11-13) on days where (day of year mod 16) &gt;= keep, so they
///    play an NCAA-length season (keep 9: about 34-37 of 61-66 games).
/// 3. NCAA careers (pipeline P1): the yearly step that sends undrafted, still-draftable players to free agency at 21
///    (0x3adefe0) uses "NCAA seasons finished &gt;= seasons" for league-32 players instead of the age.
/// 4. New-franchise import: rightless NCAA players of any age join the prospect pool at their school (0x3ad1ba1).
/// 5. NCAA seniors graduate (P1 rule) when the NHL regular season ends: event 43 calls the release routine NCAA-only.
/// Every site is checked against the eboot (each "off" must occur exactly once), so a different game version fails here.
/// </summary>
public static class NcaaCodeMods
{
    private const int CodeOff = 0x608f0;            // eboot.bin file offset of VA 0
    private const int Push = 0x37f1de0, Cont = 0x3a3b2c6, R0 = 0x3a3b0fa, R1 = 0x3a3b2c6;
    private const int RegionCheck = 0x3a3b2a4, UsBranch = 0x3a3b2ad, CapacitySite = 0x3a3adf6;
    // The season hooks replace "je skip" (6 bytes) after "test bpl,1". Not the mov r9,[rcx+0x18]; cmp r15d,0xe after it:
    // Kyty rewrites those at load time, and a cheat whose "off" no longer matches live memory stops every cheat in the
    // file from applying (K7, 2026-11-10).
    private static readonly (int Je, int Skip)[] Hooks = { (0x38e5064, 0x38e4fd0), (0x38e5131, 0x38e50a0) };

    // P1: in 0x3adefe0(this, Y+2) the player copy is at rbp-0x468 (struct = save record + 8). Y = start year of the
    // season just finished (the step runs in the summer, after the draft).
    private const int P1Hook = 0x3adf17d, P1Skip = 0x3adf100, P1Release = 0x3adf18d;
    private const int UpdateJuniorLeague = 0x3afd750;   // LeaguePlayerInfoWriter::UpdateJuniorLeague(player, league)
    private const int PlayerCopy = -0x468, LeagueByte = 0x180, EntryByte = 0x87;   // entry: save record 0x7F
    private const int RightsAge = 22;   // drafted, unsigned: rights kept until the summer he turns 22 (rights byte live 0x1B7)
    // Part 4: in the new-franchise import of non-franchise clubs (0x3ad1280), "cmp ecx,21; setge al" decides that a
    // rightless player is too old for the prospect pool; the club's league is at [rbp-0x2288] (stored by mod 2's code).
    private const int ImportHook = 0x3ad1ba1, ImportNext = 0x3ad1ba7, ClubLeague = -0x2288;
    // Part 5: the calendar dispatcher's (0x3af3120) handler for event 43 = end of the regular season (leaguetasks type 43:
    // tier 0 Apr 9, tier 1 Apr 10 in 2027), entered with rdi = LeagueManager, edx = tier. The summer step's release
    // routine 0x3adefe0(this, year + 2) is called there with 0x1000 added to the year: the P1 cave's NCAA-only mode.
    private const int EventHook = 0x3af3b52, EventJs = 0x3af3d95, EventNext = 0x3af3b5a, ReleaseRoutine = 0x3adefe0;
    // Part 6: the signing window. Cave space from the OHL branch; the date is the LeagueManager's ([LeagueGlobal] word
    // +0x1c, days since 1900-01-01; league state 0 is the last game day and stops at the end of the regular season, so
    // it isn't used); the contract-offer response
    // handler 0x39ad4a0 checks the offer against the minimum at OfferHook and rejects at OfferMinRejection.
    private const int Ohl0 = 0x3a3af83, Ohl1 = 0x3a3b0fa, LeagueGlobal = 0xfa79880, GuardPtr = 0xec2d9f0;
    private const int OfferHook = 0x39ad5be, OfferAccept = 0x39ad5ee, OfferMinRejection = 0x39ad5c3, OfferJoin = 0x39ad7eb;
    private const int WindowOpen = 41, WindowClose = 167;   // day of a March-based year: Apr 11, Aug 15
    /// <summary>The language key the offer rejection shows; the package sets its text (an unused EA key, 42 bytes of room).</summary>
    public const string OfferRejectionKey = "TXT_CONVERSATION_ANSWER_20356";
    // Part 7: the import's rights-holder branch signs a no-longer-junior-age draftee to his club's minor team unless
    // `test r14b,r14b; jne Import2Draftee` (0x3ad1ed6, r14 = "junior age") sends him on as an unsigned draftee; the
    // player's junior team is a school (the club league can be the AHL's and the junior league slot zeroed). Cave space from the QMJHL branch.
    private const int Qm0 = 0x3a3ae30, Qm1 = 0x3a3af83, Import2Hook = 0x3ad1ed6, Import2Draftee = 0x3ad1f49, Import2Next = 0x3ad1edb;
    private const int JuniorTeamSlot = -0x1af0, JuniorLeagueSlot = -0x1a58;

    /// <summary>
    /// The loop imports junior clubs and AHL clubs (teams 68-97). A draftee on his AHL affiliate's list has the AHL as club
    /// league and his junior league slot [rbp-0x1a58] zeroed (0x3ad1eab), but his junior team slot [rbp-0x1af0] (struct+0xE8,
    /// word) is untouched: movzx eax,word [rbp-0x1af0]; sub eax,first; cmp eax,last-first; ja stock;
    /// keep: junior league 32 and the fields the "stays in junior" block 0x3ad1e01..0x3ad1e2d sets (a pick from an older
    /// draft whose record byte 0x14D is 0, e.g. Hage/Ziemer, misses that block and a later creation step signs him);
    /// jmp draftee; stock: test r14b,r14b; jne draftee (replaced); jmp next
    /// </summary>
    private static byte[] ImportCave2(int at, int first, int last)
    {
        var keep = new List<byte>();
        void SetB(int off, byte v) { keep.AddRange(new byte[] { 0xC6, 0x85 }); keep.AddRange(BitConverter.GetBytes(off)); keep.Add(v); }
        SetB(JuniorLeagueSlot, 0x20);                                    // his school's league
        SetB(-0x1a84, 0); SetB(-0x1a81, 1); SetB(-0x1a83, 1); SetB(-0x1a5e, 1); SetB(-0x1a79, 0);
        keep.AddRange(new byte[] { 0x66, 0xC7, 0x85 }); keep.AddRange(BitConverter.GetBytes(-0x1b2a)); keep.AddRange(new byte[] { 0, 0 });
        SetB(-0x1a61, 0);
        var b = new List<byte> { 0x0F, 0xB7, 0x85 }; b.AddRange(BitConverter.GetBytes(JuniorTeamSlot));
        b.Add(0x2D); b.AddRange(BitConverter.GetBytes(first));
        b.Add(0x3D); b.AddRange(BitConverter.GetBytes(last - first));
        b.AddRange(new byte[] { 0x77, (byte)(keep.Count + 5) });                                             // ja stock
        b.AddRange(keep);
        b.Add(0xE9); b.AddRange(Rel32(at + b.Count + 4, Import2Draftee));
        b.AddRange(new byte[] { 0x45, 0x84, 0xF6, 0x0F, 0x85 }); b.AddRange(Rel32(at + b.Count + 4, Import2Draftee));
        b.Add(0xE9); b.AddRange(Rel32(at + b.Count + 4, Import2Next));
        return b.ToArray();
    }

    /// <param name="entryAge">NCAA players without a stored entry year started college in the fall of birth year + this
    /// (19: the Gophers' 2026-27 roster has entry − birth year 18–21, median 19).</param>
    /// <param name="commits">Players committed to a school for a later season, already in the game: (art id, school
    /// team index, seasons after the roster season: 1 = next fall). Part 10 stamps the join year from the franchise's own year at its first Apr 11; they join at that summer step (part 8).</param>
    /// <param name="entries">NCAA classes per player (art id, offset: 0 Fr, 1 So, 2 Jr, 3 Sr, 4 Gr in the roster season);
    /// part 10 stores entry year = franchise season − offset on the live records of league-32 players without one
    /// (otherwise birth year + entryAge is assumed). Relative, so no year is built in.</param>
    public static JsonObject Build(string ebootPath, int first, int last, int keep = 9, int seasons = 4, int entryAge = 19,
                                   IReadOnlyList<(int ArtId, int Team, int Offset)> commits = null,
                                   IReadOnlyList<(int ArtId, int Offset)> entries = null,
                                   IReadOnlyList<string> abbrs = null,
                                   IReadOnlyList<(string Name, int Team, int Offset)> nameCommits = null,
                                   IReadOnlyList<string> schools = null)
    {
        if (entryAge < 16 || entryAge > 23) throw new ArgumentOutOfRangeException(nameof(entryAge), "entryAge is 16-23.");
        if (first > last) throw new ArgumentException("Empty team block.");
        if (keep < 1 || keep > 16) throw new ArgumentOutOfRangeException(nameof(keep), "keep is 1-16 game days per 16.");
        if (seasons < 1 || seasons > 8) throw new ArgumentOutOfRangeException(nameof(seasons), "seasons is 1-8.");
        byte[] eboot = File.ReadAllBytes(ebootPath);
        byte[] Unique(int va, int n)
        {
            var w = eboot.AsSpan(CodeOff + va, n);
            int a = eboot.AsSpan().IndexOf(w), b = eboot.AsSpan().LastIndexOf(w);
            if (a != CodeOff + va || b != a)
                throw new InvalidDataException($"The eboot doesn't match NHL 27 1.003.001 at {va:x} (patch site not unique). The code mods need rebuilding for this version.");
            return w.ToArray();
        }

        // part 1: the junior-team picker
        byte[] orig = Unique(R0, R1 - R0);
        var neu = new byte[orig.Length]; Array.Fill(neu, (byte)0xCC);
        var whl = Loop(R0, 136, 158, 13);
        whl.CopyTo(neu, 0);
        int ncaaAt = R0 + whl.Length;
        var ncaa = Loop(ncaaAt, first, last, 32);
        ncaa.CopyTo(neu, whl.Length);
        int keep0 = RegionCheck - R0;
        Array.Copy(orig, keep0, neu, keep0, 9);                       // region check: a jump target, unchanged
        Jmp(UsBranch, ncaaAt).CopyTo(neu, UsBranch - R0);

        // part 2: NCAA-length season, check code in the freed space
        var memory = new JsonArray();
        var hookEntries = new List<JsonObject>();
        int caveAt = (ncaaAt + ncaa.Length + 15) & ~15;
        foreach (var (je, skip) in Hooks)
        {
            const int ctx = 4;                                        // test bpl,1 before the je, for a distinctive entry
            byte[] off = Unique(je - ctx, ctx + 6);
            var expect = new byte[] { 0x40, 0xF6, 0xC5, 0x01, 0x0F, 0x84 }.Concat(Rel32(je + 6, skip)).ToArray();
            if (!off.AsSpan().SequenceEqual(expect)) throw new InvalidDataException($"Unexpected code at {je:x}.");
            var c = Cave(caveAt, skip, je + 6, keep);
            if (caveAt - R0 + c.Length > keep0) throw new InvalidOperationException("The season check doesn't fit.");
            c.CopyTo(neu, caveAt - R0);
            byte[] on = (byte[])off.Clone();
            Jmp(je, caveAt).CopyTo(on, ctx);
            on[ctx + 5] = 0x90;
            hookEntries.Add(Entry(je - ctx, on, off));
            caveAt = (caveAt + c.Length + 15) & ~15;
        }

        // part 3 (P1): NCAA careers
        {
            byte[] off = Unique(P1Hook, 16);
            var expect = new byte[] { 0x45, 0x89, 0xFE, 0x41, 0x29, 0xC7, 0x41, 0x83, 0xFF, 0x15, 0x0F, 0x8C }.Concat(Rel32(P1Hook + 16, P1Skip)).ToArray();
            if (!off.AsSpan().SequenceEqual(expect)) throw new InvalidDataException($"Unexpected code at {P1Hook:x}.");
            var c = P1Cave(caveAt, seasons, entryAge);
            if (caveAt - R0 + c.Length > keep0) throw new InvalidOperationException("The NCAA career check doesn't fit.");
            c.CopyTo(neu, caveAt - R0);
            var on = Jmp(P1Hook, caveAt).Concat(Enumerable.Repeat((byte)0x90, 11)).ToArray();
            hookEntries.Add(Entry(P1Hook, on, off));
            caveAt = (caveAt + c.Length + 15) & ~15;
        }

        // part 4: the new-franchise import keeps rightless NCAA players of any age (prospect pool, at their school)
        {
            byte[] off = Unique(ImportHook, 6);              // no memory-access context: Kyty may rewrite those
            if (!off.AsSpan().SequenceEqual(new byte[] { 0x83, 0xF9, 0x15, 0x0F, 0x9D, 0xC0 })) throw new InvalidDataException($"Unexpected code at {ImportHook:x}.");
            var c = ImportCave(caveAt);
            if (caveAt - R0 + c.Length > keep0) throw new InvalidOperationException("The NCAA import check doesn't fit.");
            c.CopyTo(neu, caveAt - R0);
            var on = Jmp(ImportHook, caveAt).Append((byte)0x90).ToArray();
            hookEntries.Add(Entry(ImportHook, on, off));
            caveAt = (caveAt + c.Length + 15) & ~15;
        }

        // part 5: NCAA seniors graduate when the NHL regular season ends (event 43, tier 0), not at the summer step
        {
            byte[] off = Unique(EventHook, 8);
            var expect = new byte[] { 0x85, 0xD2, 0x0F, 0x88 }.Concat(Rel32(EventNext, EventJs)).ToArray();
            if (!off.AsSpan().SequenceEqual(expect)) throw new InvalidDataException($"Unexpected code at {EventHook:x}.");
            var c = EventCave(caveAt);
            if (caveAt - R0 + c.Length > keep0) throw new InvalidOperationException("The NCAA graduation call doesn't fit.");
            c.CopyTo(neu, caveAt - R0);
            var on = Jmp(EventHook, caveAt).Concat(new byte[] { 0x90, 0x90, 0x90 }).ToArray();
            hookEntries.Add(Entry(EventHook, on, off));
            caveAt = (caveAt + c.Length + 15) & ~15;
        }

        // the candidate list's capacity: mov esi,0x1e → 0x60
        byte[] coff = Unique(CapacitySite, 17);
        if (coff[7] != 0xBE || coff[8] != 0x1E) throw new InvalidDataException($"Unexpected code at {CapacitySite:x}.");
        byte[] con = (byte[])coff.Clone(); con[8] = 0x60;

        // part 6: the NCAA signing window for the user's contract offers. Cave space: the picker's OHL branch
        // (0x3a3af83..0x3a3b0fa, 20 pushes of teams 98-117, then league 11) becomes a loop like the WHL one.
        {
            byte[] orig2 = Unique(Ohl0, Ohl1 - Ohl0);
            var new2 = new byte[orig2.Length]; Array.Fill(new2, (byte)0xCC);
            var ohl = Loop(Ohl0, 98, 117, 11);
            ohl.CopyTo(new2, 0);
            int win = (Ohl0 + ohl.Length + 15) & ~15;
            var w = Window(win);
            int gate = (win + w.Length + 15) & ~15;
            var g = OfferCave(gate, win);
            if (gate - Ohl0 + g.Length > new2.Length) throw new InvalidOperationException("The signing window doesn't fit.");
            w.CopyTo(new2, win - Ohl0);
            g.CopyTo(new2, gate - Ohl0);
            var offerOff = eboot.AsSpan(CodeOff + OfferHook, 5).ToArray();     // not unique in the eboot (only the first entry must be)
            if (!offerOff.AsSpan().SequenceEqual(new byte[] { 0x41, 0x39, 0xC7, 0x7D, 0x2B })) throw new InvalidDataException($"Unexpected code at {OfferHook:x}.");
            hookEntries.Add(Entry(Ohl0, new2, orig2));
            hookEntries.Add(Entry(OfferHook, Jmp(OfferHook, gate), offerOff));
        }

        // part 7: a new franchise's import doesn't sign NCAA draftees to their club's AHL team. Cave space: the picker's
        // QMJHL branch (0x3a3ae30..0x3a3af83, 18 pushes of teams 118-135, then league 12) becomes a loop.
        {
            byte[] orig3 = Unique(Qm0, Qm1 - Qm0);
            var new3 = new byte[orig3.Length]; Array.Fill(new3, (byte)0xCC);
            var qm = Loop(Qm0, 118, 135, 12);
            qm.CopyTo(new3, 0);
            int cave = (Qm0 + qm.Length + 15) & ~15;
            var c = ImportCave2(cave, first, last);
            c.CopyTo(new3, cave - Qm0);
            var off = eboot.AsSpan(CodeOff + Import2Hook, 5).ToArray();
            if (!off.AsSpan().SequenceEqual(new byte[] { 0x45, 0x84, 0xF6, 0x75, 0x6E })) throw new InvalidDataException($"Unexpected code at {Import2Hook:x}.");
            hookEntries.Add(Entry(Qm0, new3, orig3));
            hookEntries.Add(Entry(Import2Hook, Jmp(Import2Hook, cave), off));
        }

        // part 8: P2 (drafted seniors leave school at Apr 11; unsigned ones lose their rights at the end of August) and
        // commits (join their school in the summer of their commit year). Fixed code (NcaaPart8Code, generated by
        // eboot_tools/ncaa_part8.py) in an EA-SDK cleanup run turned into a loop; settings + commit table in a data
        // block after it. Its event-43 cave replaces part 5's (same release call, then mode 1).
        commits ??= Array.Empty<(int, int, int)>();
        {
            byte[] orig8 = Unique(NcaaPart8Code.LoopAt, NcaaPart8Code.RunEnd - NcaaPart8Code.LoopAt);
            var region = new byte[orig8.Length]; Array.Fill(region, (byte)0xCC);
            Convert.FromHexString(NcaaPart8Code.LoopHex).CopyTo(region, 0);
            Convert.FromHexString(NcaaPart8Code.CodeHex).CopyTo(region, NcaaPart8Code.CodeAt - NcaaPart8Code.LoopAt);
            var data = new List<byte>();
            foreach (int v in new[] { first, last - first, entryAge - 100, seasons - 1, commits.Count }) data.AddRange(BitConverter.GetBytes(v));
            foreach (var (art, team, off) in commits)
            {
                if (team < first || team > last || art <= 0 || art > 0xFFFF || off < 1 || off > 15)
                    throw new ArgumentException($"Bad commit entry (art id {art}, team {team}, offset {off}).");
                data.AddRange(BitConverter.GetBytes((ushort)art)); data.Add((byte)(team - first)); data.Add((byte)off);
            }
            // the data block lives in part 9's cave, after its code (room for ~170 commits)
            if (NcaaPart8Code.DataAt + data.Count > NcaaPart8Code.P9RunEnd) throw new InvalidOperationException($"Too many commits for the code cave ({commits.Count}).");
            byte[] Call(int at, int to) => new byte[] { 0xE8 }.Concat(Rel32(at + 5, to)).ToArray();
            byte[] At(int va, int n) => eboot.AsSpan(CodeOff + va, n).ToArray();
            if (!At(NcaaPart8Code.E33Call, 5).SequenceEqual(Call(NcaaPart8Code.E33Call, NcaaPart8Code.E33Fn))) throw new InvalidDataException($"Unexpected code at {NcaaPart8Code.E33Call:x}.");
            if (!At(NcaaPart8Code.YearCall, 5).SequenceEqual(Call(NcaaPart8Code.YearCall, NcaaPart8Code.ChlCleanup))) throw new InvalidDataException($"Unexpected code at {NcaaPart8Code.YearCall:x}.");
            var chlOff = At(NcaaPart8Code.ChlHook, 5);
            if (!chlOff.SequenceEqual(new byte[] { 0x83, 0xF8, 0x14, 0x7C, unchecked((byte)(NcaaPart8Code.ChlKeep - (NcaaPart8Code.ChlHook + 5))) })) throw new InvalidDataException($"Unexpected code at {NcaaPart8Code.ChlHook:x}.");
            hookEntries.RemoveAll(e => e["offset"]!.ToString() == $"0x{EventHook:x}");
            hookEntries.Add(Entry(NcaaPart8Code.LoopAt, region, orig8));
            // (no event-43 hook any more: graduation runs on Apr 11 from part 10's daily date check, calling NcaaPart8Code.Grad)
            hookEntries.Add(Entry(NcaaPart8Code.E33Call, Call(NcaaPart8Code.E33Call, NcaaPart8Code.Ev33), At(NcaaPart8Code.E33Call, 5)));
            hookEntries.Add(Entry(NcaaPart8Code.YearCall, Call(NcaaPart8Code.YearCall, NcaaPart8Code.Year), At(NcaaPart8Code.YearCall, 5)));
            hookEntries.Add(Entry(NcaaPart8Code.ChlHook, Jmp(NcaaPart8Code.ChlHook, NcaaPart8Code.Chl), chlOff));

            // part 9: the player card shows an NCAA player's class after his handedness ("Shoots: Right | Senior").
            // Fixed code in an EA destructor run turned into a loop; it reads part 8's data block; text buffer on a
            // zero page outside the module.
            byte[] orig9 = Unique(NcaaPart8Code.P9LoopAt, NcaaPart8Code.P9RunEnd - NcaaPart8Code.P9LoopAt);
            var region9 = new byte[orig9.Length]; Array.Fill(region9, (byte)0xCC);
            Convert.FromHexString(NcaaPart8Code.P9LoopHex).CopyTo(region9, 0);
            Convert.FromHexString(NcaaPart8Code.P9CodeHex).CopyTo(region9, NcaaPart8Code.P9CodeAt - NcaaPart8Code.P9LoopAt);
            data.CopyTo(region9, NcaaPart8Code.DataAt - NcaaPart8Code.P9LoopAt);
            hookEntries.Add(Entry(NcaaPart8Code.P9LoopAt, region9, orig9));
            foreach (var (site, stub) in new[] { (NcaaPart8Code.P9CardCall, NcaaPart8Code.P9Card), (NcaaPart8Code.P9ScoutCall, NcaaPart8Code.P9Scout) })
            {
                var siteOff = At(site, 5);               // regular card / scouting card: call SetString(view, "PlayerPreference", text)
                if (!siteOff.SequenceEqual(Call(site, NcaaPart8Code.P9SetString))) throw new InvalidDataException($"Unexpected code at {site:x}.");
                hookEntries.Add(Entry(site, Call(site, stub), siteOff));
            }
            hookEntries.Add(Entry(NcaaPart8Code.P9Buffer, new byte[16], new byte[16]));

            // part 10: signed NCAA players leave league 32 and per-player entry years (both called from part 8's walk),
            // over-age CHL draftees go to a random school, NCAA signing-window pop-ups. Fixed code in an EA destructor
            // run turned into a loop; the entry table after it.
            entries ??= Array.Empty<(int, int)>();
            byte[] orig10 = Unique(NcaaPart8Code.P10LoopAt, NcaaPart8Code.P10RunEnd - NcaaPart8Code.P10LoopAt);
            var region10 = new byte[orig10.Length]; Array.Fill(region10, (byte)0xCC);
            Convert.FromHexString(NcaaPart8Code.P10LoopHex).CopyTo(region10, 0);
            Convert.FromHexString(NcaaPart8Code.P10CodeHex).CopyTo(region10, NcaaPart8Code.P10CodeAt - NcaaPart8Code.P10LoopAt);
            var etab = new List<byte>(BitConverter.GetBytes(entries.Count));
            foreach (var (art, off) in entries)
            {
                if (art <= 0 || art > 0xFFFF || off < 0 || off > 7) throw new ArgumentException($"Bad class entry (art id {art}, offset {off}).");
                etab.AddRange(BitConverter.GetBytes((ushort)art)); etab.Add((byte)off); etab.Add(0);
            }
            // the stats table's TEAM cell: each team of the block's abbreviation, 8 bytes (empty = the game's)
            for (int i = 0; i <= last - first; i++)
            {
                string t = abbrs != null && i < abbrs.Count ? abbrs[i] ?? "" : "";
                var tb = System.Text.Encoding.ASCII.GetBytes(t);
                if (tb.Length > 7 || t.Any(c => c > 127)) throw new ArgumentException($"Team abbreviation '{t}' is longer than 7 ASCII characters.");
                etab.AddRange(tb); etab.AddRange(new byte[8 - tb.Length]);
            }
            // the schools' display names for the offer messages (part 11), 24 bytes each
            for (int i = 0; i <= last - first; i++)
            {
                var nb = System.Text.Encoding.UTF8.GetBytes(schools != null && i < schools.Count ? schools[i] ?? "" : "");
                if (nb.Length > 23) throw new ArgumentException($"School name '{schools[i]}' is longer than 23 bytes.");
                etab.AddRange(nb); etab.AddRange(new byte[24 - nb.Length]);
            }
            // (the tables live in part 11's cave, built below)
            if (NcaaPart8Code.P10EntryTable + etab.Count > NcaaPart8Code.P11RunEnd) throw new InvalidOperationException($"Too many classes / teams for the code cave ({entries.Count} classes, {last - first + 1} teams).");
            var chl10 = At(NcaaPart8Code.P10ChlHook, 7);
            if (Convert.ToHexString(chl10) != NcaaPart8Code.P10ChlHookOff) throw new InvalidDataException($"Unexpected code at {NcaaPart8Code.P10ChlHook:x}.");
            var pop10 = At(NcaaPart8Code.P10PopHook, 11);
            if (Convert.ToHexString(pop10) != NcaaPart8Code.P10PopHookOff) throw new InvalidDataException($"Unexpected code at {NcaaPart8Code.P10PopHook:x}.");
            var ev4 = At(NcaaPart8Code.P10Ev4Call, 5);
            if (!ev4.SequenceEqual(Call(NcaaPart8Code.P10Ev4Call, NcaaPart8Code.P10Ev4Fn))) throw new InvalidDataException($"Unexpected code at {NcaaPart8Code.P10Ev4Call:x}.");
            hookEntries.Add(Entry(NcaaPart8Code.P10LoopAt, region10, orig10));
            hookEntries.Add(Entry(NcaaPart8Code.P10ChlHook, Jmp(NcaaPart8Code.P10ChlHook, NcaaPart8Code.P10Chl).Concat(new byte[] { 0x90, 0x90 }).ToArray(), chl10));
            hookEntries.Add(Entry(NcaaPart8Code.P10PopHook, Jmp(NcaaPart8Code.P10PopHook, NcaaPart8Code.P10Pop).Concat(Enumerable.Repeat((byte)0x90, 6)).ToArray(), pop10));
            hookEntries.Add(Entry(NcaaPart8Code.P10Ev4Call, Call(NcaaPart8Code.P10Ev4Call, NcaaPart8Code.P10Ev4), ev4));
            var text10 = At(NcaaPart8Code.P10TextHook, 5);
            if (Convert.ToHexString(text10) != NcaaPart8Code.P10TextHookOff) throw new InvalidDataException($"Unexpected code at {NcaaPart8Code.P10TextHook:x}.");
            hookEntries.Add(Entry(NcaaPart8Code.P10TextHook, Call(NcaaPart8Code.P10TextHook, NcaaPart8Code.P10Text), text10));
            // junior stat lines keep a school's team index (the generator clamps team indexes below 500)
            foreach (var (site, stub, off, n) in new[] { (NcaaPart8Code.P10Clamp1, NcaaPart8Code.P10Clamp1Stub, NcaaPart8Code.P10Clamp1Off, 10),
                                                       (NcaaPart8Code.P10Clamp2, NcaaPart8Code.P10Clamp2Stub, NcaaPart8Code.P10Clamp2Off, 9) })
            {
                var siteOff = At(site, n);
                if (Convert.ToHexString(siteOff) != off) throw new InvalidDataException($"Unexpected code at {site:x}.");
                hookEntries.Add(Entry(site, Jmp(site, stub).Concat(Enumerable.Repeat((byte)0x90, n - 5)).ToArray(), siteOff));
            }
            var cell = At(NcaaPart8Code.P10TeamCell, 8);
            if (!cell.SequenceEqual(new byte[] { 0x4C, 0x89, 0xE2 }.Concat(Call(NcaaPart8Code.P10TeamCell + 3, NcaaPart8Code.P10TeamCellCall))))
                throw new InvalidDataException($"Unexpected code at {NcaaPart8Code.P10TeamCell:x}.");
            hookEntries.Add(Entry(NcaaPart8Code.P10TeamCell, Jmp(NcaaPart8Code.P10TeamCell, NcaaPart8Code.P10TeamCellStub).Concat(new byte[] { 0x90, 0x90, 0x90 }).ToArray(), cell));
            hookEntries.Add(Entry(NcaaPart8Code.P10Marks, new byte[16], new byte[16]));

            // part 11: another destructor run turned into a loop; holds name_commit (commits the game generated after
            // install, matched by name), the name table and part 10's class/abbreviation tables
            byte[] orig11 = Unique(NcaaPart8Code.P11LoopAt, NcaaPart8Code.P11RunEnd - NcaaPart8Code.P11LoopAt);
            var region11 = new byte[orig11.Length]; Array.Fill(region11, (byte)0xCC);
            Convert.FromHexString(NcaaPart8Code.P11LoopHex).CopyTo(region11, 0);
            Convert.FromHexString(NcaaPart8Code.P11CodeHex).CopyTo(region11, NcaaPart8Code.P11CodeAt - NcaaPart8Code.P11LoopAt);
            var seen = new Dictionary<uint, (int Team, int Offset)>();
            foreach (var (name, team, off) in nameCommits ?? Array.Empty<(string, int, int)>())
            {
                if (team < first || team > last || off < 0 || off > 15) throw new ArgumentException($"Bad commit by name ({name}, team {team}, offset {off}).");
                seen.TryAdd(NameHash(name), (team, off));
            }
            var ntab = new List<byte>(BitConverter.GetBytes(seen.Count));
            foreach (var (h, (team, off)) in seen) { ntab.AddRange(BitConverter.GetBytes(h)); ntab.Add((byte)(team - first)); ntab.Add((byte)off); ntab.AddRange(new byte[2]); }
            if (ntab.Count > NcaaPart8Code.P11NameRoom) throw new InvalidOperationException($"Too many commits by name ({seen.Count}).");
            ntab.CopyTo(region11, NcaaPart8Code.P11NameTable - NcaaPart8Code.P11LoopAt);
            etab.CopyTo(region11, NcaaPart8Code.P10EntryTable - NcaaPart8Code.P11LoopAt);
            hookEntries.Add(Entry(NcaaPart8Code.P11LoopAt, region11, orig11));
            var resp = At(NcaaPart8Code.P11RespHook, 5);
            if (!resp.SequenceEqual(Call(NcaaPart8Code.P11RespHook, NcaaPart8Code.P11RespCall))) throw new InvalidDataException($"Unexpected code at {NcaaPart8Code.P11RespHook:x}.");
            hookEntries.Add(Entry(NcaaPart8Code.P11RespHook, Call(NcaaPart8Code.P11RespHook, NcaaPart8Code.P11Resp), resp));
            // the prospect card's team label: "<team> → <school> '<yy>" for commits not at school yet
            var card = At(NcaaPart8Code.P11CardHook, 5);
            if (!card.SequenceEqual(Call(NcaaPart8Code.P11CardHook, NcaaPart8Code.P11SetString))) throw new InvalidDataException($"Unexpected code at {NcaaPart8Code.P11CardHook:x}.");
            hookEntries.Add(Entry(NcaaPart8Code.P11CardHook, Call(NcaaPart8Code.P11CardHook, NcaaPart8Code.P11Card), card));
            // AI teams' summer signing of their picks skips commits who haven't played their NCAA season
            var ai = At(NcaaPart8Code.P11AiHook, 7);
            if (Convert.ToHexString(ai) != NcaaPart8Code.P11AiHookOff) throw new InvalidDataException($"Unexpected code at {NcaaPart8Code.P11AiHook:x}.");
            hookEntries.Add(Entry(NcaaPart8Code.P11AiHook, Jmp(NcaaPart8Code.P11AiHook, NcaaPart8Code.P11Ai).Concat(new byte[] { 0x90, 0x90 }).ToArray(), ai));
            // ...and their re-sign phase / free-agent signing (OrgRosterBrain's sign routine) returns "no deal" for them
            var orb = At(NcaaPart8Code.P11OrbHook, 6);
            if (Convert.ToHexString(orb) != NcaaPart8Code.P11OrbHookOff) throw new InvalidDataException($"Unexpected code at {NcaaPart8Code.P11OrbHook:x}.");
            hookEntries.Add(Entry(NcaaPart8Code.P11OrbHook, Jmp(NcaaPart8Code.P11OrbHook, NcaaPart8Code.P11Orb).Concat(new byte[] { 0x90 }).ToArray(), orb));
            var orb2 = At(NcaaPart8Code.P11Orb2Hook, 6);   // its sibling sign routine (the re-sign phase's first-rounder call)
            if (Convert.ToHexString(orb2) != NcaaPart8Code.P11Orb2HookOff) throw new InvalidDataException($"Unexpected code at {NcaaPart8Code.P11Orb2Hook:x}.");
            hookEntries.Add(Entry(NcaaPart8Code.P11Orb2Hook, Jmp(NcaaPart8Code.P11Orb2Hook, NcaaPart8Code.P11Orb2).Concat(new byte[] { 0x90 }).ToArray(), orb2));
            // draft rights last until the player turns 22 (or the stock 2 / 4 years if longer)
            var rights = At(NcaaPart8Code.P11RightsHook, 5);
            if (Convert.ToHexString(rights) != NcaaPart8Code.P11RightsHookOff) throw new InvalidDataException($"Unexpected code at {NcaaPart8Code.P11RightsHook:x}.");
            hookEntries.Add(Entry(NcaaPart8Code.P11RightsHook, Jmp(NcaaPart8Code.P11RightsHook, NcaaPart8Code.P11Rights), rights));
        }

        memory.Add(Entry(CapacitySite, con, coff));
        memory.Add(Entry(R0, neu, orig));
        foreach (var e in hookEntries) memory.Add(e);
        return new JsonObject
        {
            ["name"] = $"NCAA franchise rules: US prospects get a random NCAA school (teams {first}-{last}, league 32); US regions play an NCAA-length season ({keep} of 16 game days); undrafted NCAA players stay at school for {seasons} seasons, graduate (leaving school) when the NHL regular season ends and import at any age; NCAA players can be signed Apr 1 - Aug 15 (none signed at franchise creation); drafted seniors leave school Apr 11 and unsigned ones become free agents at the end of August; commits join their school in the summer of their commit year ({commits.Count} commits); player cards show NCAA players' class; signed NCAA players keep their NCAA history; over-age CHL draftees join a random school; signing-window pop-ups (Apr 1, Aug 9); NCAA stat lines show the school; generated commits join their school; ex-pros skip the NCAA window; commits can't sign before their NCAA season (user or AI); prospect cards show commits",
            ["type"] = "checkbox",
            ["memory"] = memory,
        };
    }

    /// <summary>FNV-1a over the first name's UTF-8 bytes, a space, the last name's (25 bytes max each), as part 11
    /// hashes the record's name fields. "First Last" splits at the first space.</summary>
    public static uint NameHash(string name)
    {
        name = name.Trim();
        int sp = name.IndexOf(' ');
        string first = sp < 0 ? name : name[..sp], last = sp < 0 ? "" : name[(sp + 1)..].Trim();
        uint h = 0x811C9DC5;
        foreach (byte c in System.Text.Encoding.UTF8.GetBytes(first).Take(25)) h = (h ^ c) * 0x01000193;
        h = (h ^ 0x20) * 0x01000193;
        foreach (byte c in System.Text.Encoding.UTF8.GetBytes(last).Take(25)) h = (h ^ c) * 0x01000193;
        return h;
    }

    private static JsonObject Entry(int va, byte[] on, byte[] off) => new()
    {
        ["offset"] = $"0x{va:x}",
        ["on"] = Convert.ToHexString(on),
        ["off"] = Convert.ToHexString(off),
    };

    private static byte[] Rel32(int from, int to) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, to - from); return b; }
    private static byte[] Jmp(int at, int to) => new byte[] { 0xE9 }.Concat(Rel32(at + 5, to)).ToArray();

    /// <summary>mov r14d,first; L: lea rdi,[rbp-0x50]; mov esi,r14d; mov edx,5; call push; inc r14d; cmp r14d,last+1; jne L; mov r14d,league; jmp Cont</summary>
    private static byte[] Loop(int at, int first, int last, int league)
    {
        var b = new List<byte> { 0x41, 0xBE }; b.AddRange(BitConverter.GetBytes(first));
        int l = at + b.Count;
        b.AddRange(new byte[] { 0x48, 0x8D, 0x7D, 0xB0, 0x44, 0x89, 0xF6, 0xBA, 0x05, 0x00, 0x00, 0x00 });
        b.Add(0xE8); b.AddRange(Rel32(at + b.Count + 4, Push));
        b.AddRange(new byte[] { 0x41, 0xFF, 0xC6, 0x41, 0x81, 0xFE }); b.AddRange(BitConverter.GetBytes(last + 1));
        b.Add(0x75); b.Add(unchecked((byte)(sbyte)(l - (at + b.Count + 1))));
        b.AddRange(new byte[] { 0x41, 0xBE }); b.AddRange(BitConverter.GetBytes(league));
        b.Add(0xE9); b.AddRange(Rel32(at + b.Count + 4, Cont));
        return b.ToArray();
    }

    /// <summary>
    /// mov r14d,r15d; sub r15d,eax (replaced code: r15d = age at Y+2); cmp byte [copy+league],32; jne stock;
    /// movzx ecx,byte [copy+entry]; test ecx,ecx; jnz have; lea ecx,[rax+entryAge-100] (birth + entryAge - 2000);
    /// have: mov edx,r14d; and edx,0xfff (drop the NCAA-only mode bit); sub edx,102 (Y - 2000); sub edx,ecx;
    /// cmp edx,seasons-1; jl skip; jmp release;
    /// stock: test r14d,0x1000; jnz skip (NCAA-only call from part 5); cmp r15d,21; jl skip; jmp release
    /// </summary>
    private static byte[] P1Cave(int at, int seasons, int entryAge)
    {
        var b = new List<byte> { 0x45, 0x89, 0xFE, 0x41, 0x29, 0xC7, 0x80, 0xBD };
        b.AddRange(BitConverter.GetBytes(PlayerCopy + LeagueByte)); b.Add(0x20);
        int jne = b.Count; b.AddRange(new byte[] { 0x75, 0x00 });
        b.AddRange(new byte[] { 0x0F, 0xB6, 0x8D }); b.AddRange(BitConverter.GetBytes(PlayerCopy + EntryByte));
        b.AddRange(new byte[] { 0x83, 0xE1, 0x7F });                        // and ecx,0x7f (bit 0x80 = graduated drafted senior, part 8)
        b.AddRange(new byte[] { 0x85, 0xC9, 0x75, 0x03, 0x8D, 0x48, unchecked((byte)(entryAge - 100)) });
        b.AddRange(new byte[] { 0x44, 0x89, 0xF2, 0x81, 0xE2, 0xFF, 0x0F, 0x00, 0x00, 0x83, 0xEA, 0x66 });
        b.AddRange(new byte[] { 0x29, 0xCA, 0x83, 0xFA, (byte)(seasons - 1) });
        b.Add(0x0F); b.Add(0x8C); b.AddRange(Rel32(at + b.Count + 4, P1Skip));
        // graduated: he leaves school first (junior league 1, as the CHL cleanup does for over-age juniors), so the NCAA
        // rules stop applying to him: UpdateJuniorLeague(player = r13d, 1)
        b.AddRange(new byte[] { 0x44, 0x89, 0xEE, 0xBA, 0x01, 0x00, 0x00, 0x00, 0xE8 }); b.AddRange(Rel32(at + b.Count + 4, UpdateJuniorLeague));
        b.Add(0xE9); b.AddRange(Rel32(at + b.Count + 4, P1Release));
        b[jne + 1] = (byte)(b.Count - (jne + 2));
        b.AddRange(new byte[] { 0x41, 0xF7, 0xC6, 0x00, 0x10, 0x00, 0x00, 0x0F, 0x85 }); b.AddRange(Rel32(at + b.Count + 4, P1Skip));
        // undrafted: free agent at 21 (stock); drafted with rights held: the club keeps them until the summer he turns
        // RightsAge (the new CBA's age-22 retention; keeps commits with their club through college)
        b.AddRange(new byte[] { 0xB9, 0x15, 0x00, 0x00, 0x00, 0x80, 0xBD }); b.AddRange(BitConverter.GetBytes(PlayerCopy + 0x1B7)); b.Add(0x00);
        b.AddRange(new byte[] { 0x78, 0x05, 0xB9 }); b.AddRange(BitConverter.GetBytes(RightsAge + 1));
        b.AddRange(new byte[] { 0x41, 0x39, 0xCF });
        b.Add(0x0F); b.Add(0x8C); b.AddRange(Rel32(at + b.Count + 4, P1Skip));
        b.Add(0xE9); b.AddRange(Rel32(at + b.Count + 4, P1Release));
        return b.ToArray();
    }

    /// <summary>
    /// window(): al = 1 if NCAA players may be signed today: the league date (LeagueManager +0x1c, days since
    /// 1900-01-01) in Apr 1..Aug 15: (days - 59) % 1461, / 365 (4 → Feb 29) gives the day of a March-based year. A new
    /// franchise starts in the preseason, outside the window.
    /// </summary>
    private static byte[] Window(int at)
    {
        var b = new List<byte> { 0x53, 0x48, 0x83, 0xEC, 0x20 };
        var closedJumps = new List<int>();
        void JumpClosed(byte cc) { b.Add(0x0F); b.Add(cc); closedJumps.Add(b.Count); b.AddRange(new byte[4]); }
        b.AddRange(new byte[] { 0x48, 0x8B, 0x05 }); b.AddRange(Rel32(at + b.Count + 4, LeagueGlobal));   // mov rax,[LeagueManager]
        b.AddRange(new byte[] { 0x48, 0x85, 0xC0 }); JumpClosed(0x84);                              // test rax,rax; je
        b.AddRange(new byte[] { 0x0F, 0xB7, 0x40, 0x1C, 0x83, 0xE8, 0x3B }); JumpClosed(0x82);     // movzx eax,word [rax+0x1c]; sub eax,59; jb
        b.AddRange(new byte[] { 0x31, 0xD2, 0xB9 }); b.AddRange(BitConverter.GetBytes(1461)); b.AddRange(new byte[] { 0xF7, 0xF1 });
        b.AddRange(new byte[] { 0x89, 0xD0, 0x31, 0xD2, 0xB9 }); b.AddRange(BitConverter.GetBytes(365)); b.AddRange(new byte[] { 0xF7, 0xF1 });
        b.AddRange(new byte[] { 0x83, 0xF8, 0x04, 0x72, 0x06, 0x81, 0xC2 }); b.AddRange(BitConverter.GetBytes(365));
        b.AddRange(new byte[] { 0x83, 0xFA, (byte)WindowOpen }); JumpClosed(0x82);                 // jb
        b.AddRange(new byte[] { 0x81, 0xFA }); b.AddRange(BitConverter.GetBytes(WindowClose)); JumpClosed(0x87);  // ja
        b.AddRange(new byte[] { 0xB8, 0x01, 0, 0, 0, 0xEB, 0x02 });
        int closed = b.Count; b.AddRange(new byte[] { 0x31, 0xC0, 0x48, 0x83, 0xC4, 0x20, 0x5B, 0xC3 });
        foreach (int j in closedJumps) { var d = BitConverter.GetBytes(closed - (j + 4)); for (int i = 0; i < 4; i++) b[j + i] = d[i]; }
        return b.ToArray();
    }

    /// <summary>
    /// cmp r15d,eax; jl min-rejection (the replaced code); jmp part 11's offer rules (NCAA window, ex-pro, commitment
    /// lock, with the school's name in the rejection message).
    /// </summary>
    private static byte[] OfferCave(int at, int window)
    {
        if (window != NcaaPart8Code.P11WindowAt) throw new InvalidOperationException($"window() is at {window:x}, part 11 expects {NcaaPart8Code.P11WindowAt:x}.");
        var b = new List<byte> { 0x41, 0x39, 0xC7, 0x0F, 0x8C }; b.AddRange(Rel32(at + b.Count + 4, OfferMinRejection));
        b.Add(0xE9); b.AddRange(Rel32(at + b.Count + 4, NcaaPart8Code.P11Offer));
        return b.ToArray();
    }

    /// <summary>push rdi,rsi,rdx,rcx,r8,r9 (48 bytes: rsp stays 16-aligned); test edx,edx; jnz done; mov esi,[rdi+0x18];
    /// add esi,0x1002; call release routine; done: pop r9..rdi; test edx,edx; js orig; jmp next</summary>
    private static byte[] EventCave(int at)
    {
        var b = new List<byte> { 0x57, 0x56, 0x52, 0x51, 0x41, 0x50, 0x41, 0x51, 0x85, 0xD2 };
        int jnz = b.Count; b.AddRange(new byte[] { 0x75, 0x00 });
        b.AddRange(new byte[] { 0x8B, 0x77, 0x18, 0x81, 0xC6, 0x02, 0x10, 0x00, 0x00 });
        b.Add(0xE8); b.AddRange(Rel32(at + b.Count + 4, ReleaseRoutine));
        b[jnz + 1] = (byte)(b.Count - (jnz + 2));
        b.AddRange(new byte[] { 0x41, 0x59, 0x41, 0x58, 0x59, 0x5A, 0x5E, 0x5F, 0x85, 0xD2, 0x0F, 0x88 });
        b.AddRange(Rel32(at + b.Count + 4, EventJs));
        b.Add(0xE9); b.AddRange(Rel32(at + b.Count + 4, EventNext));
        return b.ToArray();
    }

    /// <summary>cmp ecx,21; setge al (replaced code); cmp dword [rbp-0x2288],32; jne +2; xor eax,eax; jmp next</summary>
    private static byte[] ImportCave(int at)
    {
        var b = new List<byte> { 0x83, 0xF9, 0x15, 0x0F, 0x9D, 0xC0, 0x83, 0xBD };
        b.AddRange(BitConverter.GetBytes(ClubLeague)); b.Add(0x20);
        b.AddRange(new byte[] { 0x75, 0x02, 0x31, 0xC0 });
        b.Add(0xE9); b.AddRange(Rel32(at + b.Count + 4, ImportNext));
        return b.ToArray();
    }

    /// <summary>je skip; lea eax,[r14-3]; cmp eax,2; ja next; lea eax,[r8+r10]; and eax,15; cmp eax,keep; jae skip; next: jmp ret
    /// (eax is dead there: the code after the hook writes it before reading it)</summary>
    private static byte[] Cave(int at, int skip, int ret, int keep)
    {
        var b = new List<byte> { 0x0F, 0x84 }; b.AddRange(Rel32(at + 6, skip));
        b.AddRange(new byte[] { 0x41, 0x8D, 0x46, 0xFD, 0x83, 0xF8, 0x02, 0x77, 0x10, 0x43, 0x8D, 0x04, 0x10, 0x83, 0xE0, 0x0F, 0x83, 0xF8, (byte)keep });
        b.Add(0x0F); b.Add(0x83); b.AddRange(Rel32(at + b.Count + 4, skip));
        b.Add(0xE9); b.AddRange(Rel32(at + b.Count + 4, ret));
        return b.ToArray();
    }
}
