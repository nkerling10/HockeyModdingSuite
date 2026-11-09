namespace NHL27Kit.Core;

/// <summary>A game player with no portrait whom CapWages lists with the team, and his photo once looked up.</summary>
public sealed class PortraitCandidate
{
    public RosterPlayer Player { get; init; }
    public CapPlayer Cap { get; init; }
    /// <summary>The team he's on in the game ("Iowa Wild").</summary>
    public string TeamName { get; init; }
    /// <summary>His NHL.com headshot (via his CapWages page); null until looked up, or when CapWages has none.</summary>
    public string HeadshotUrl { get; set; }
    /// <summary>Why he can't get a portrait (no NHL.com headshot, lookup failed); null when he can.</summary>
    public string Problem { get; set; }
    /// <summary>The 512×340 portrait to write (RGBA), once fitted.</summary>
    public byte[] Portrait { get; set; }
    /// <summary>The art id his bio will point at: a free portrait slot (see PortraitSync.FreeArtIds).</summary>
    public int NewArtId { get; set; } = -1;
}

/// <summary>
/// Portraits for players who have none, from CapWages, one NHL team at a time (its page also lists its AHL affiliate's
/// players), so it needs neither the NHL Sync's full fetch nor the other 31 teams.
///
/// How the game shows a portrait: the bio's artid names a texture, ui/textures/common/playerportraits/p{artid} (512×340,
/// BC3, transparent background), and hasportrait says to show it. A player the sync created (or a stock player without a
/// photo) has an art id with no texture, and HMS can replace a texture but not add one. The game ships 16,964 portraits
/// and about 9,500 of them belong to nobody (retired players from earlier games): no bio in nhl_game.db or in the game
/// modes' own rosters (entry_draft, expansion_draft, initial_experience, threes_campaign) uses them. A new portrait goes
/// into one of those: its image is replaced and the player's artid points at it.
///
/// The photo is NHL.com's headshot (the CapWages page's "headshot", 336×336), the same shoot and framing the game's
/// portraits come from. Fitted against Kirill Kaprizov's portrait in the game: the headshot drawn 581 px square with its
/// top-left at (-47, -92) on the 512×340 canvas puts the face, eye line and shoulders where the game's are.
/// </summary>
public static class PortraitSync
{
    public const string Prefix = "ui/textures/common/playerportraits/p";
    public const int Width = 512, Height = 340;
    /// <summary>The fitted placement of a 336-px NHL headshot: drawn this size, at this offset.</summary>
    public const int HeadshotSize = 336, DrawnSize = 581, OffsetX = -47, OffsetY = -92;
    /// <summary>The game modes' own roster databases, whose players' art ids aren't free either.</summary>
    public static readonly string[] OtherRosters =
    {
        "gamemodes/roster/entry_draft.db", "gamemodes/roster/expansion_draft.db",
        "gamemodes/roster/initial_experience.db", "gamemodes/roster/threes_campaign.db",
    };

    public static string TextureName(int artId) => Prefix + artId;

    /// <summary>The player has no portrait to show: the flag is off, or his art id has no texture.</summary>
    public static bool Missing(RosterPlayer p, AssetIndex index) => !p.HasPortrait || index.GetRes(p.Portrait) == null;

    /// <summary>
    /// The NHL team whose CapWages page covers <paramref name="team"/>, and the game teams it covers: an NHL team covers
    /// itself and its AHL affiliate; an AHL team is covered by its NHL parent's page, for itself only. (-1, empty) when
    /// the team is neither (junior, European, national).
    /// </summary>
    public static (string Abbr, HashSet<int> Teams) Scope(T3Db db, int team)
    {
        var abbrOf = RosterSync.TeamIndex(db).GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
        var affiliates = RosterSync.AhlAffiliates(db);
        if (abbrOf.TryGetValue(team, out var abbr))
            return (abbr, affiliates.TryGetValue(team, out int ahl) ? new HashSet<int> { team, ahl } : new HashSet<int> { team });
        foreach (var (nhl, ahl) in affiliates)
            if (ahl == team && abbrOf.TryGetValue(nhl, out var parent)) return (parent, new HashSet<int> { team });
        return (null, new HashSet<int>());
    }

    /// <summary>The players on <paramref name="teams"/> in the game with no portrait whom the team's CapWages page lists
    /// (NHL contracts and AHL contracts), matched by name and birth date like the sync.</summary>
    public static List<PortraitCandidate> Candidates(T3Db db, AssetIndex index, CapTeam cap, ISet<int> teams)
    {
        var roster = new Roster(db);
        var matcher = new PlayerMatcher(db, roster);
        var found = new List<PortraitCandidate>();
        var seen = new HashSet<int>();
        foreach (var cp in cap.Players.Concat(cap.AhlPlayers))
        {
            var (p, nameOnly) = matcher.Find(cp.First, cp.Last, cp.Born);
            if (p == null || nameOnly || !teams.Contains(p.Team) || !Missing(p, index) || !seen.Add(p.GameId)) continue;
            found.Add(new PortraitCandidate
            {
                Player = p, Cap = cp,
                TeamName = roster.Teams.TryGetValue(p.Team, out var tm) && !string.IsNullOrEmpty(tm.Name) ? tm.Name : cap.Name,
            });
        }
        return found.OrderBy(c => c.TeamName).ThenBy(c => c.Player.LastName).ToList();
    }

    /// <summary>
    /// Portrait slots nobody uses, lowest first: every p{n} texture whose n is no player's artid in
    /// <paramref name="main"/> (nhl_game.db) or in <paramref name="others"/> (the game modes' rosters, read by the same
    /// field position, since their table and field names are scrambled but the bio layout is the same).
    /// </summary>
    public static List<int> FreeArtIds(AssetIndex index, T3Db main, IEnumerable<T3Db> others)
    {
        var used = new HashSet<int>();
        var bio = main.Table(Roster.BioTable);
        var art = bio.Fields.First(f => f.Name == "artid");
        for (int r = 0; r < bio.WrittenRecords; r++)
            if (!main.IsInvalid(bio, r)) used.Add((int)main.Get(bio, r, art));
        foreach (var db in others)
            foreach (var t in db.Tables.Where(t => t.RecordSize == bio.RecordSize && t.Fields.Count == bio.Fields.Count))
            {
                var f = t.Fields.FirstOrDefault(x => x.BitOffset == art.BitOffset && x.Depth == art.Depth);
                if (f == null) continue;
                for (int r = 0; r < t.WrittenRecords; r++)
                    if (!db.IsInvalid(t, r)) used.Add((int)db.Get(t, r, f));
            }
        var free = new List<int>();
        foreach (var name in index.Res.Keys)
            if (name.StartsWith(Prefix, StringComparison.Ordinal) && int.TryParse(name.AsSpan(Prefix.Length), out int n) && n > 0 && !used.Contains(n))
                free.Add(n);
        free.Sort();
        return free;
    }

    /// <summary>
    /// The game's 512×340 portrait from an NHL.com headshot (RGBA, any size; 336×336 as NHL.com serves it): scaled by
    /// 581/336 and placed at (-47, -92) scaled the same way, on a transparent canvas.
    /// </summary>
    public static byte[] Fit(byte[] rgba, int w, int h)
    {
        // drawn 581 px wide (whatever size NHL.com serves), the height kept in proportion
        int nw = DrawnSize, nh = (int)Math.Round((double)h * DrawnSize / w);
        // Resized premultiplied: the headshot's transparent pixels are white underneath, and resizing them as they are
        // bleeds a light halo into the hair and shoulders on the game's dark backgrounds.
        var pre = (byte[])rgba.Clone();
        for (int i = 0; i < pre.Length; i += 4)
            for (int c = 0; c < 3; c++) pre[i + c] = (byte)((pre[i + c] * pre[i + 3] + 127) / 255);
        var scaled = MipChain.Resize(pre, w, h, nw, nh, srgb: false);
        for (int i = 0; i < scaled.Length; i += 4)
        {
            int a = scaled[i + 3];
            for (int c = 0; c < 3; c++) scaled[i + c] = a == 0 ? (byte)0 : (byte)Math.Min(255, (scaled[i + c] * 255 + a / 2) / a);
        }
        int ox = (int)Math.Round(OffsetX * (double)nw / DrawnSize), oy = (int)Math.Round(OffsetY * (double)nw / DrawnSize);
        var dst = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            int sy = y - oy;
            if (sy < 0 || sy >= nh) continue;
            for (int x = 0; x < Width; x++)
            {
                int sx = x - ox;
                if (sx < 0 || sx >= nw) continue;
                Buffer.BlockCopy(scaled, (sy * nw + sx) * 4, dst, (y * Width + x) * 4, 4);
            }
        }
        return dst;
    }

    /// <summary>Points each player's bio at his new portrait: artid = the slot, hasportrait = 1. audioid (his voice and
    /// name lines) is left alone.</summary>
    public static string Apply(T3Db db, IEnumerable<PortraitCandidate> done)
    {
        using var batch = db.Batch();
        var bio = db.Table(Roster.BioTable);
        var f = bio.Fields.ToDictionary(x => x.Name);
        int n = 0;
        foreach (var c in done.Where(c => c.NewArtId > 0))
        {
            db.Set(bio, c.Player.BioRow, f["artid"], c.NewArtId);
            if (f.TryGetValue("hasportrait", out var hp)) db.Set(bio, c.Player.BioRow, hp, 1);
            n++;
        }
        return $"Portraits: {n} player{(n == 1 ? "" : "s")} given one ({string.Join(", ", done.Where(c => c.NewArtId > 0).Select(c => c.Player.Name).Take(6))})";
    }
}
