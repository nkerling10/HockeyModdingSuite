namespace NHL27Kit.Core;

/// <summary>
/// Read handles kept open for a planning pass (<see cref="Keep"/>). Planning a package reads the same TOC and CAS files
/// thousands of times (four bytes of a TOC entry, a bundle record, a stored chunk), and opening the file for each read
/// cost more than the read. Outside a <see cref="Keep"/> scope, and on other threads than the one that opened it, every
/// read opens its own handle as before. The handles are read-only and shared, and the scope must end before anything is
/// written to the files (a handle's buffer would not see the write).
/// </summary>
public static class HeldFiles
{
    [ThreadStatic] private static Dictionary<string, FileStream> _open;

    /// <summary>Keeps read handles open on this thread until disposed (nested scopes keep the outer one).</summary>
    public static IDisposable Keep()
    {
        if (_open != null) return new Scope(false);
        _open = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        return new Scope(true);
    }

    private sealed class Scope : IDisposable
    {
        private bool _own;
        public Scope(bool own) { _own = own; }
        public void Dispose()
        {
            if (!_own) return;
            _own = false;
            foreach (var fs in _open.Values) fs.Dispose();
            _open = null;
        }
    }

    private static FileStream Held(string path)
    {
        if (!_open.TryGetValue(path, out var fs))
            _open[path] = fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return fs;
    }

    /// <summary>Reads <paramref name="into"/>'s length of bytes at <paramref name="pos"/>.</summary>
    public static void Read(string path, long pos, Span<byte> into)
    {
        if (_open == null)
        {
            using var once = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            once.Position = pos;
            once.ReadExactly(into);
            return;
        }
        var fs = Held(path);
        fs.Position = pos;
        fs.ReadExactly(into);
    }

    /// <summary>The bytes at <paramref name="pos"/>, or null when the file ends before them.</summary>
    public static byte[] ReadRange(string path, long pos, int length)
    {
        if (_open == null)
        {
            using var once = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (pos + length > once.Length) return null;
            once.Position = pos;
            var b1 = new byte[length];
            once.ReadExactly(b1);
            return b1;
        }
        var fs = Held(path);
        if (pos + length > fs.Length) return null;
        fs.Position = pos;
        var b = new byte[length];
        fs.ReadExactly(b);
        return b;
    }
}
