namespace NHL27Kit.Core;

/// <summary>
/// The art ids a package gave its players on earlier installs, so a reinstall gives each the same one again. A franchise
/// save keeps its own copy of every player (art id included) while the portrait textures (p{art id}) and the NCAA code
/// mods' tables (commits, classes: by art id) live in the game files: if a reinstall handed out different ids (a photo
/// or a roster-file player added ahead of others shifts every later one), a save made before it would show other
/// players' faces and send the wrong players to school. Keyed by normalised first + last name and birth date.
/// One memory per kind of id (a created player's own id; a portrait slot): a player whose photo is taken out goes back
/// to his own id, not to a slot.
/// </summary>
public sealed class ArtIdMemory
{
    private readonly Dictionary<string, int> _old;
    private readonly HashSet<int> _reserved;
    private readonly Dictionary<string, int> _now = new();

    public ArtIdMemory(Dictionary<string, int> remembered = null)
    {
        _old = remembered ?? new Dictionary<string, int>();
        _reserved = _old.Values.ToHashSet();
        Max = _old.Count == 0 ? 0 : _old.Values.Max();
    }

    public static string Key(string first, string last, DateTime born) => $"{RosterSync.Norm(first)} {RosterSync.Norm(last)} {born:yyyy-MM-dd}";

    /// <summary>The id this player had last time, if any.</summary>
    public int? Remembered(string key) => _old.TryGetValue(key, out int a) ? a : null;

    /// <summary>An id some player had last time: not for a player new to the package.</summary>
    public bool IsReserved(int art) => _reserved.Contains(art);

    /// <summary>The highest remembered id (new ids go above it).</summary>
    public int Max { get; }

    /// <summary>Players given their remembered id this install, and players given a new one.</summary>
    public int Kept { get; private set; }
    public int Fresh { get; private set; }

    public void Record(string key, int art, bool kept)
    {
        _now[key] = art;
        if (kept) Kept++; else Fresh++;
    }

    /// <summary>What to remember for the next install: this install's ids over the earlier ones (a player not in the
    /// package this time keeps his, should he come back).</summary>
    public Dictionary<string, int> Merged()
    {
        var m = new Dictionary<string, int>(_old);
        foreach (var (k, v) in _now) m[k] = v;
        return m;
    }
}
