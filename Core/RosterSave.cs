using K4os.Compression.LZ4.Streams;

namespace NHL27Kit.Core;

/// <summary>
/// A roster saved in the game (a RosterCustomN save slot, such as one downloaded from the share center). Its DATA holds
/// a small JSON file, then the whole roster database (nhl_game.db's tables: bios, ratings, rosters, teams, lines, gear,
/// Create-a-Player players…) as one LZ4 frame; read with nhl_game-meta.xml, it's a T3Db like the game's own.
/// </summary>
public static class RosterSave
{
    private static readonly byte[] Lz4Magic = { 0x04, 0x22, 0x4D, 0x18 };

    /// <summary>The roster database in a RosterCustomN folder (or its DATA file). Throws when it holds none.</summary>
    public static T3Db Open(string path, string metaXml)
    {
        string data = Directory.Exists(path) ? Path.Combine(path, "DATA") : path;
        return new T3Db(Unpack(File.ReadAllBytes(data)), metaXml);
    }

    /// <summary>When the roster was saved and the game version it was saved with, from the save's metadata ("" when absent).</summary>
    public static (string Date, string Version) Info(string path)
    {
        string data = Directory.Exists(path) ? Path.Combine(path, "DATA") : path;
        string text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(data));
        var d = System.Text.RegularExpressions.Regex.Match(text, "\"FileCreateDate\":\"(\\d{4})(\\d{2})(\\d{2})(\\d{2})(\\d{2})(\\d{2})\"");
        var v = System.Text.RegularExpressions.Regex.Matches(text, "(\\d\\.\\d{3})\0").LastOrDefault();
        return (d.Success ? $"{d.Groups[1]}-{d.Groups[2]}-{d.Groups[3]} {d.Groups[4]}:{d.Groups[5]}:{d.Groups[6]}" : "", v?.Groups[1].Value ?? "");
    }

    /// <summary>
    /// The roster database's bytes from a save's DATA: its LZ4 frame (and any frames right after it), each decoded to
    /// exactly its own length from the block headers. The frames are followed by the save's trailer, which the decoder
    /// used to be run into and stopped by the error, keeping whatever had come out by then: a large roster could come
    /// back short without a word (2026-11, a deleted RosterCustom save). Now a frame that runs past the end of the file,
    /// or a database shorter than its own header's size, is refused.
    /// </summary>
    public static byte[] Unpack(byte[] data)
    {
        int at = data.AsSpan().IndexOf(Lz4Magic);
        if (at < 0) throw new InvalidDataException("This save holds no roster database (no LZ4 frame).");
        using var raw = new MemoryStream();
        int frames = 0;
        while (at >= 0 && at + 4 <= data.Length && data.AsSpan(at, 4).SequenceEqual(Lz4Magic))
        {
            int length = FrameLength(data, at);
            if (length < 0) throw new InvalidDataException("This save's roster database is cut short (its LZ4 frame runs past the end of the file).");
            using (var lz = LZ4Stream.Decode(new MemoryStream(data, at, length))) lz.CopyTo(raw);
            at += length;
            frames++;
        }
        var db = raw.ToArray();
        // the database's header ("DB", version, then its whole size at 8) says how long it must be
        if (db.Length >= 12 && db[0] == (byte)'D' && db[1] == (byte)'B'
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(db.AsSpan(8)) is var size && size > db.Length)
            throw new InvalidDataException($"This save's roster database is cut short: {db.Length:N0} of its {size:N0} bytes ({frames} LZ4 frame(s)).");
        return db;
    }

    /// <summary>
    /// The length in bytes of the LZ4 frame at <paramref name="at"/> (frame header, blocks, end mark, content checksum),
    /// from its block headers; -1 when the data ends first or it isn't an LZ4 frame.
    /// </summary>
    public static int FrameLength(ReadOnlySpan<byte> d, int at)
    {
        if (at < 0 || at + 7 > d.Length || !d.Slice(at, 4).SequenceEqual(Lz4Magic)) return -1;
        byte flg = d[at + 4];
        if (flg >> 6 != 1) return -1;                                   // frame version 01
        long p = at + 6;                                                // magic, FLG, BD
        if ((flg & 0x08) != 0) p += 8;                                  // content size
        if ((flg & 0x01) != 0) p += 4;                                  // dictionary id
        p += 1;                                                         // header checksum
        bool blockSum = (flg & 0x10) != 0, contentSum = (flg & 0x04) != 0;
        while (true)
        {
            if (p + 4 > d.Length) return -1;
            uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(d.Slice((int)p, 4));
            p += 4;
            if (size == 0) break;                                       // end mark
            p += (size & 0x7FFFFFFF) + (blockSum ? 4 : 0);
            if (p > d.Length) return -1;
        }
        if (contentSum) p += 4;
        return p > d.Length ? -1 : (int)(p - at);
    }
}
