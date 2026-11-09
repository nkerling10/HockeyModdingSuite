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

    /// <summary>The roster database's bytes from a save's DATA.</summary>
    public static byte[] Unpack(byte[] data)
    {
        int at = data.AsSpan().IndexOf(Lz4Magic);
        if (at < 0) throw new InvalidDataException("This save holds no roster database (no LZ4 frame).");
        using var lz = LZ4Stream.Decode(new MemoryStream(data, at, data.Length - at));
        using var raw = new MemoryStream();
        // the frame is followed by the save's trailer, which the decoder takes for a second frame: stop at its end
        var buf = new byte[1 << 16];
        try { for (int n; (n = lz.Read(buf, 0, buf.Length)) > 0;) raw.Write(buf, 0, n); }
        catch (Exception e) when (raw.Length > 0 && e is InvalidDataException or InvalidOperationException or ArgumentException) { }
        return raw.ToArray();
    }
}
