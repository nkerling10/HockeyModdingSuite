using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using NHL27Kit.Core;

namespace NHL27Kit.App;

/// <summary>
/// Portraits for the selected team's players who have none, from CapWages alone (PortraitSync): one CapWages team page
/// and one player page per player, no NHL Sync fetch. The NHL.com headshots, as downloaded and as fitted to the game's
/// 512×340 portrait, are kept in the mods folder (portraits/), where each texture's mod entry points.
/// </summary>
public partial class RosterPage
{
    private async void OnPortraits(object sender, RoutedEventArgs e) => await PortraitsAsync(write: true, ask: true);

    /// <summary>
    /// Looks up the selected team's missing portraits and, when <paramref name="write"/>, writes them: each into a free
    /// portrait slot (its texture replaced, as a mod entry the Mods page can restore), then the players' bios pointed at
    /// their slots. <paramref name="ask"/> shows who'll get one and asks first. Returns what it did, for the status line.
    /// </summary>
    public async Task<string> PortraitsAsync(bool write, bool ask)
    {
        if (_mods == null || _busy) return null;
        int team = (TeamFilter.SelectedItem as ComboBoxItem)?.Tag is int t ? t : -1;
        if (team < 0) return Say("Pick a team in the team list first: portraits are added one team at a time.");
        _busy = true;
        PortraitsButton.IsEnabled = false;
        try
        {
            var db = await _mods.OpenDbAsync(Roster.Database);
            var (abbr, teams) = PortraitSync.Scope(db, team);
            if (abbr == null) return Say("Portraits come from an NHL team's CapWages page: pick an NHL or AHL team.");

            Say($"Reading {abbr}'s CapWages page…");
            var cap = await Task.Run(() => CapWagesApi.TeamAsync(abbr));
            var found = PortraitSync.Candidates(db, _mods.Index, cap, teams);
            if (found.Count == 0) return Say($"Everyone CapWages lists on {TeamNames(db, teams)} already has a portrait.");

            string dir = Path.Combine(_mods.Store.Dir, "portraits");
            Directory.CreateDirectory(dir);
            int i = 0;
            foreach (var c in found)
            {
                Say($"Fetching photos from CapWages… {++i}/{found.Count} · {c.Player.Name}");
                try
                {
                    var shot = await CapWagesApi.HeadshotAsync(c.Cap.Slug);
                    if (shot is not { Nhl: true } s) { c.Problem = shot == null ? "no photo on CapWages" : "only a non-NHL photo (wrong framing)"; continue; }
                    c.HeadshotUrl = s.Url;
                    string raw = Path.Combine(dir, $"{Slug(c)}.headshot.png");
                    await File.WriteAllBytesAsync(raw, await CapWagesApi.BytesAsync(s.Url));
                    var (rgba, w, h) = await Task.Run(() => ImageLoader.Load(raw));
                    c.Portrait = await Task.Run(() => PortraitSync.Fit(rgba, w, h));
                    SavePng(c.Portrait, PortraitSync.Width, PortraitSync.Height, PortraitFile(dir, c));
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or InvalidOperationException or NotSupportedException or FileFormatException)
                {
                    c.Problem = ex.Message;
                }
                await Task.Delay(400);
            }
            var ready = found.Where(c => c.Portrait != null).ToList();
            string list = string.Join("\n", found.Select(c => $"  {c.Player.Name} ({c.TeamName})" + (c.Problem != null ? $": skipped, {c.Problem}" : "")));
            if (!write) return Say($"{ready.Count} of {found.Count} players without a portrait have an NHL.com headshot: "
                + string.Join(", ", found.Select(c => c.Player.Name + (c.Problem != null ? " (none)" : ""))) + $". Fitted portraits are in {dir}.");
            if (ready.Count == 0) return Say("None of them has an NHL.com headshot on CapWages:\n" + list);
            if (ModService.GameRunning) return Say("KytyPS5 is running. Close the game first: its files can't be changed while it reads them.");
            if (ask && MessageBox.Show(Window.GetWindow(this),
                    $"Add a portrait from NHL.com (via CapWages) for {ready.Count} player{(ready.Count == 1 ? "" : "s")}?\n\n{list}\n\n"
                    + "Each goes into a portrait slot no player uses; the Mods page can restore it.\n\n"
                    + "A roster saved in the game (or a franchise) keeps its own copy of the players, so it won't show them: "
                    + "load the game's default roster, and restart the game after deleting a saved one.",
                    "Add portraits", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return Say("Portraits not added.");

            // free slots: textures no bio uses, here or in the game modes' own rosters
            Say("Finding free portrait slots…");
            var others = new List<T3Db>();
            foreach (var name in PortraitSync.OtherRosters)
                try { others.Add(await _mods.OpenDbAsync(name)); } catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException) { }
            var free = new Queue<int>(await Task.Run(() => PortraitSync.FreeArtIds(_mods.Index, db, others)));

            var done = new List<PortraitCandidate>();
            foreach (var c in ready)
            {
                Say($"Writing {c.Player.Name}'s portrait into the game…");
                string file = PortraitFile(dir, c);
                while (free.Count > 0 && c.NewArtId < 0)
                {
                    int slot = free.Dequeue();
                    string tex = PortraitSync.TextureName(slot);
                    try
                    {
                        var plan = await _mods.PlanTextureAsync(tex, file, null);
                        var h = plan.Header;
                        var entry = _mods.Queue("texture", tex, file, $"{c.Player.Name}'s portrait (NHL.com headshot via CapWages), {h.Format.Name} {h.Width}×{h.Height}",
                            plan.Chunk.Patches, plan.Chunk.Notes.Where(n => n.StartsWith("left") || n.Contains("shared")).ToList());
                        await _mods.ApplyAsync(entry);
                        c.NewArtId = slot;
                    }
                    // a slot whose stored image other textures share, or that can't be rebuilt: try the next one
                    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { }
                }
                if (c.NewArtId < 0) c.Problem = "no free portrait slot could take it";
                else done.Add(c);
            }
            string summary = null;
            if (done.Count > 0)
                await _mods.SaveDbAsync(Roster.Database, d => summary = PortraitSync.Apply(d, done), "Portraits");
            await LoadRosterAsync();
            var failed = ready.Where(c => c.NewArtId < 0).ToList();
            return Say((summary ?? "No portraits written") + (failed.Count > 0 ? $"; not written: {string.Join(", ", failed.Select(c => c.Player.Name))}" : "")
                + ". They show with the game's default roster (a saved roster keeps its own copy of the players); the Mods page can restore them.");
        }
        catch (Exception ex)
        {
            return Say("Couldn't add portraits: " + ex.Message);
        }
        finally
        {
            _busy = false;
            PortraitsButton.IsEnabled = true;
        }
    }

    /// <summary>Selects a team in the team list by name or abbreviation (scripted runs); false when none matches.</summary>
    public bool SelectTeam(string name)
    {
        if (TeamFilter.ItemsSource is not IEnumerable<ComboBoxItem> items) return false;
        var item = items.FirstOrDefault(i => (i.Content as string)?.Contains(name, StringComparison.OrdinalIgnoreCase) == true);
        if (item == null) return false;
        TeamFilter.SelectedItem = item;
        return true;
    }

    private string Say(string text)
    {
        StatusChanged?.Invoke(text);
        return text;
    }

    private static string TeamNames(T3Db db, HashSet<int> teams)
    {
        var roster = new Roster(db);
        return string.Join(" and ", teams.Select(t => roster.Teams.TryGetValue(t, out var tm) && !string.IsNullOrEmpty(tm.Name) ? tm.Name : $"team {t}"));
    }

    private static string Slug(PortraitCandidate c) => string.IsNullOrEmpty(c.Cap.Slug) ? $"player-{c.Player.GameId}" : c.Cap.Slug;

    private static string PortraitFile(string dir, PortraitCandidate c) => Path.Combine(dir, $"{Slug(c)}.portrait.png");

    private static void SavePng(byte[] rgba, int w, int h, string file)
    {
        var bmp = TextureService.ToBitmap(rgba, w, h, Channel.Rgba);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
