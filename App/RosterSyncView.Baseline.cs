using System.IO;
using System.Text;
using System.Windows;
using NHL27Kit.Core;

namespace NHL27Kit.App;

/// <summary>
/// Step 1 of NHL Sync: a shared roster (a RosterCustomN save folder) as the game's base roster (RosterBaseline). The
/// roster is picked once (remembered in the settings), previewed (tables, players added and dropped, Create-a-Player
/// players converted or merged, teams dropped), and applied as one roster mod entry the Mods page can restore. Until a
/// baseline is in the game, the first Fetch asks once whether to start from one.
/// </summary>
public partial class RosterSyncView
{
    private const string MetaFile = "gamemodes/database/t3db/nhl_game-meta.xml";
    private readonly Settings _settings = Settings.Load();
    private RosterBaseline.LayeredResult _baselinePlan;
    private bool _baselineBusy;

    /// <summary>The roster mod entry has a baseline in it and is in the game.</summary>
    private bool BaselineApplied => _mods?.Find(DbEditor.Kind, Roster.Database) is { } e && e.Summary.Contains("Baseline roster") && _mods.State(e) == ModState.Applied;

    /// <summary>The card's text and buttons for the current roster folder and game.</summary>
    private void ShowBaseline()
    {
        string dir = _settings.BaselineRoster;
        bool picked = !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
        BaselinePath.Text = picked ? dir : "No roster chosen. NHL Sync uses the game's own roster.";
        BaselineState.Text = BaselineApplied ? "· in the game" : picked ? "· not applied yet" : "";
        BaselinePreview.IsEnabled = picked && !_baselineBusy && !_busy;
        BaselineApply.IsEnabled = picked && !_baselineBusy && !_busy;
        BaselinePick.IsEnabled = !_baselineBusy && !_busy;
    }

    private async void OnBaselinePick(object sender, RoutedEventArgs e) => await PickBaselineAsync();

    /// <summary>
    /// Asks for a roster pack folder (manifest.json + tables\*.csv). A roster save (a RosterCustom folder, or a folder
    /// holding one) is offered as a pack instead: exported once, next to the game's other roster packs, and the pack used
    /// from then on. True when a pack was picked.
    /// </summary>
    private async Task<bool> PickBaselineAsync()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a roster pack (or a RosterCustom save to make one from)" };
        if (!string.IsNullOrEmpty(_settings.BaselineRoster) && Directory.Exists(_settings.BaselineRoster)) dlg.InitialDirectory = _settings.BaselineRoster;
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return false;
        string dir = dlg.FolderName;
        if (!RosterPack.IsPack(dir))
        {
            if (SaveFolder(dir) is not { } save)
            {
                MessageBox.Show(Window.GetWindow(this), "That folder isn't a roster pack (it has no manifest.json), and has no roster save in it either.",
                    "HMS", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            dir = await ExportPackAsync(save);
            if (dir == null) return false;
        }
        UsePack(dir);
        return true;
    }

    private void UsePack(string dir)
    {
        _settings.BaselineRoster = dir;
        _settings.BaselineDeclined = false;
        _settings.Save();
        _baselinePlan = null;
        BaselineDetails.Visibility = Visibility.Collapsed;
        ShowBaseline();
    }

    /// <summary>A roster save as a roster pack (asked first), in "roster-packs" in the mods folder. The pack's path, or null.</summary>
    private async Task<string> ExportPackAsync(string save)
    {
        var (date, version) = RosterSave.Info(save);
        string name = "roster-" + (date.Length >= 10 ? date[..10] : DateTime.Now.ToString("yyyy-MM-dd"));
        // in the mods folder, beside the mod entries (git-ignored): a pack is a user's own copy of someone's roster
        string root = Path.Combine(_mods.Store.Dir, "roster-packs");
        string dir = Path.Combine(root, name);
        if (MessageBox.Show(Window.GetWindow(this),
                $"That's a roster save, not a roster pack. Make a pack from it?\n\n{save}\n→ {dir}\n\n"
                + "A pack is a folder of CSV files, one per table (players, ratings, rosters, teams…), that HMS reads from then on; the save isn't needed again.",
                "Roster pack", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return null;
        _baselineBusy = true;
        ShowBaseline();
        BaselineDetails.Visibility = Visibility.Visible;
        BaselineDetails.Text = "Making the roster pack…";
        try
        {
            await Task.Run(() =>
            {
                string meta = Encoding.UTF8.GetString(new Legacy(_mods.Index).Read(MetaFile)).TrimStart((char)0xFEFF);
                RosterPack.Export(RosterSave.Open(save, meta), dir, new RosterPackManifest { RosterDate = date, GameVersion = version });
            });
            BaselineDetails.Text = $"Roster pack written to {dir}.";
            return dir;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            BaselineDetails.Text = "Couldn't make the roster pack: " + ex.Message;
            return null;
        }
        finally
        {
            _baselineBusy = false;
            BaselineProgressPanel.Visibility = Visibility.Collapsed;
            ShowBaseline();
        }
    }

    /// <summary>The roster save folder itself (it has a DATA file), or the first RosterCustom folder inside; null when neither.</summary>
    private static string SaveFolder(string dir)
    {
        if (File.Exists(Path.Combine(dir, "DATA"))) return dir;
        return Directory.GetDirectories(dir, "RosterCustom*").FirstOrDefault(d => File.Exists(Path.Combine(d, "DATA")));
    }

    private async void OnBaselinePreview(object sender, RoutedEventArgs e) => await PreviewBaselineAsync();

    /// <summary>The progress text and percent for a step of the layered import (the created players counted 60-90%).</summary>
    private static (string, int) Step(string name, int i, int n) => name switch
    {
        _ when n > 0 => ($"Converting Create-a-Player players {i}/{n} · {name}", 60 + i * 30 / n),
        "Reading the roster pack" => (name, 5),
        "Ratings and bios" => (name, 20),
        "Adding the roster's players the game doesn't have" => (name, 35),
        "Create-a-Player tables" => (name, 45),
        "Roster spots and lines" => (name, 55),
        "Checking every player and team reference" => (name, 95),
        _ => (name, 90),
    };

    /// <summary>
    /// Runs the layered import (RosterBaseline.ImportLayered) on a copy of the game's roster and shows what it would do:
    /// the same steps Use as base roster writes. Nothing is written.
    /// </summary>
    public async Task<RosterBaseline.LayeredResult> PreviewBaselineAsync()
    {
        if (_mods == null || string.IsNullOrEmpty(_settings.BaselineRoster)) return null;
        if (RosterEntryApplied() is { } applied)
        {
            // step 1 builds from the game's own roster; with an entry in the game, the preview can't see that roster
            BaselineDetails.Visibility = Visibility.Visible;
            BaselineDetails.Text = $"The game's roster has changes in it now ({Short(applied.Summary)}). Use as base roster restores the game's own roster first, then builds this one onto it. "
                + "To preview first, restore the roster entry on the Mods page.";
            return null;
        }
        _baselineBusy = true;
        ShowBaseline();
        BaselineDetails.Visibility = Visibility.Visible;
        BaselineDetails.Text = "Reading the roster…";
        try
        {
            string dir = _settings.BaselineRoster;
            var game = await _mods.OpenDbAsync(Roster.Database);
            var report = BaselineProgressReporter();
            var result = await Task.Run(() =>
            {
                string meta = Encoding.UTF8.GetString(new Legacy(_mods.Index).Read(MetaFile)).TrimStart((char)0xFEFF);
                var copy = new T3Db((byte[])game.Data.Clone(), meta);
                return RosterBaseline.ImportLayered(copy, dir, meta, (name, i, n) => report.Report(Step(name, i, n)));
            });
            _baselinePlan = result;
            BaselineDetails.Text = Describe(result, Path.GetFileName(dir));
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            BaselineDetails.Text = "Couldn't read that roster: " + ex.Message;
            return null;
        }
        finally
        {
            _baselineBusy = false;
            BaselineProgressPanel.Visibility = Visibility.Collapsed;
            ShowBaseline();
        }
    }

    /// <summary>The preview in words: each layer, then what happens to each Create-a-Player player.</summary>
    private static string Describe(RosterBaseline.LayeredResult r, string pack)
    {
        var sb = new StringBuilder();
        var conv = r.Created.Where(c => c.Skip == null).ToList();
        sb.AppendLine($"Step 1 builds {pack} onto the game's own roster, keeping the game's player indexes (a whole-table copy crashes franchise):");
        sb.AppendLine($"1. Ratings and bios of the players both rosters have: {r.RatingsAndBios:N0} rows change.");
        sb.AppendLine($"2. {r.Added} of the roster's players the game doesn't have are added (bio, ratings, gear).");
        sb.AppendLine("3. Its Create-a-Player tables come in.");
        sb.AppendLine($"4. {r.Spots}.");
        sb.AppendLine($"5. {conv.Count} Create-a-Player players become real players: {conv.Count(c => c.MatchId < 0)} new, {conv.Count(c => c.MatchId >= 0)} copies merged into the real player "
            + "(he moves to the copy's team with its ratings, keeping his face, portrait, draft and rights)"
            + string.Join("", r.Created.Where(c => c.Skip != null).GroupBy(c => c.Skip).Select(g => $"; {g.Count()} left as they are ({g.Key})")) + "."
            + (r.Cleaned != null ? " " + r.Cleaned + "." : ""));
        sb.AppendLine(r.Problems.Count == 0 ? "Checked: no player or team reference points at nothing." : "PROBLEMS — it won't be written:\n  " + string.Join("\n  ", r.Problems));
        sb.AppendLine("The game's own teams, strategies, jerseys, arenas, team gear, draft picks and existing players' gear stay as they are. Earlier NHL Sync changes are replaced: run NHL Sync (step 2) afterwards.");
        sb.AppendLine();
        foreach (var g in conv.GroupBy(c => c.League))
            sb.AppendLine($"League {g.Key} ({g.Count()}): " + string.Join(", ", g.Select(c => c.MatchId >= 0 ? $"{c.Name} [{c.MatchTeam} → {c.TeamName}]" : $"{c.Name} ({c.TeamName})")));
        return sb.ToString().TrimEnd();
    }

    private async void OnBaselineApply(object sender, RoutedEventArgs e) => await ApplyBaselineAsync(ask: true);

    /// <summary>Builds the roster onto the game's (RosterBaseline.ImportLayered), as one roster mod entry.</summary>
    public async Task<string> ApplyBaselineAsync(bool ask)
    {
        if (_mods == null || string.IsNullOrEmpty(_settings.BaselineRoster) || _busy || _baselineBusy) return null;
        if (ModService.GameRunning)
        {
            MessageBox.Show(Window.GetWindow(this), "KytyPS5 is running. Close the game first: its files can't be changed while it reads them.", "HMS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        var existing = RosterEntryApplied();
        var plan = existing != null ? null : _baselinePlan ?? await PreviewBaselineAsync();
        if (existing == null && plan == null) return null;
        if (plan != null && plan.Problems.Count > 0)
        {
            MessageBox.Show(Window.GetWindow(this), "This roster would leave references pointing at nothing, which crashes the game, so it can't be used:\n\n" + string.Join("\n", plan.Problems),
                "Base roster", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        var conv = plan?.Created.Where(c => c.Skip == null).ToList();
        if (ask && MessageBox.Show(Window.GetWindow(this),
                $"Use this roster as the game's base roster?\n\n{_settings.BaselineRoster}\n\n"
                + (existing != null
                    ? $"• the game's roster has changes in it ({Short(existing.Summary)}): they're restored first, and this roster is built onto the game's own roster\n"
                    : $"• ratings and bios of {plan.RatingsAndBios:N0} players' rows, and {plan.Added} players added\n"
                      + "• its roster spots and lines\n"
                      + $"• {conv.Count} Create-a-Player players become real players ({conv.Count(c => c.MatchId >= 0)} merged into the real player)\n")
                + "• earlier NHL Sync changes and roster edits are replaced: run NHL Sync again afterwards\n\n"
                + "It goes in as one roster mod entry; the Mods page can restore the game's roster. A roster saved in the game keeps its own copy: load the default roster to see this one.",
                "Base roster", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return null;

        _baselineBusy = true;
        ShowBaseline();
        string dir = _settings.BaselineRoster, summary = null;
        StatusChanged?.Invoke("Writing the base roster into the game…");
        try
        {
            var report = BaselineProgressReporter();
            // step 1 always builds from the game's own roster: an applied roster entry (an earlier base roster, NHL Sync) goes first
            if (existing != null)
            {
                report.Report(("Restoring the game's own roster", 2));
                await _mods.RestoreAsync(existing);
            }
            string meta = await Task.Run(() => Encoding.UTF8.GetString(new Legacy(_mods.Index).Read(MetaFile)).TrimStart((char)0xFEFF));
            await _mods.SaveDbAsync(Roster.Database, db =>
            {
                var result = RosterBaseline.ImportLayered(db, dir, meta, (name, i, n) => report.Report(Step(name, i, n)));
                // nothing may point at a player or team that isn't there: the game crashes on it
                if (result.Problems.Count > 0)
                    throw new InvalidDataException("The base roster wasn't written: these references would point at nothing —\n" + string.Join("\n", result.Problems));
                report.Report(("Writing the changes into the game files…", -1));
                summary = result.Summary(Path.GetFileName(dir));
                return summary;
            }, "Base roster");
            _baselinePlan = null;
            BaselineDetails.Text = summary + ".\n\nNext: Fetch & compare (step 2) to bring it up to date. New players have no portraits yet: the Portraits button on the Roster page adds them a team at a time.";
            StatusChanged?.Invoke(summary + ". The Mods page can restore the game's roster.");
            Applied?.Invoke();
            // anything compared before is against the old roster
            if (_fetched) FetchStatus.Text = "The base roster changed: Fetch again to compare against it.";
            return summary;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("Couldn't write the base roster: " + ex.Message);
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Couldn't write the base roster", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally
        {
            _baselineBusy = false;
            BaselineProgressPanel.Visibility = Visibility.Collapsed;
            ShowBaseline();
        }
    }

    /// <summary>
    /// The card's progress bar and line, updated from any thread: (text, percent), a percent below 0 showing a moving
    /// bar for a step that can't say how far it is (writing into the game files).
    /// </summary>
    private IProgress<(string Text, int Percent)> BaselineProgressReporter()
    {
        BaselineProgressPanel.Visibility = Visibility.Visible;
        BaselineProgress.IsIndeterminate = false;
        BaselineProgress.Value = 0;
        BaselineProgressText.Text = "Starting…";
        BaselinePercent.Text = "";
        return new Progress<(string Text, int Percent)>(p =>
        {
            BaselineProgressText.Text = p.Text;
            BaselineProgress.IsIndeterminate = p.Percent < 0;
            if (p.Percent >= 0) BaselineProgress.Value = p.Percent;
            BaselinePercent.Text = p.Percent >= 0 ? $"{p.Percent}%" : "";
            StatusChanged?.Invoke(p.Percent >= 0 ? $"{p.Text} ({p.Percent}%)" : p.Text);
        });
    }

    /// <summary>The roster database's mod entry when it's in the game (any roster changes: a base roster, NHL Sync, edits).</summary>
    private ModEntry RosterEntryApplied() =>
        _mods?.Find(DbEditor.Kind, Roster.Database) is { } e && _mods.State(e) is ModState.Applied or ModState.Partial ? e : null;

    private static string Short(string text) => text.Length > 140 ? text[..140] + "…" : text;

    /// <summary>What the card's details box shows (scripted runs).</summary>
    public string BaselineText => BaselineDetails.Text;

    private async Task PickThenPreviewAsync()
    {
        if (await PickBaselineAsync()) await PreviewBaselineAsync();
    }

    /// <summary>Points the card at a roster folder (scripted runs), as picking it would.</summary>
    public void UseBaseline(string dir)
    {
        if (!RosterPack.IsPack(dir)) return;
        UsePack(dir);
    }

    /// <summary>
    /// Before the first fetch with no base roster in the game: recommends starting from one. True to go on with the fetch
    /// (No: the game's own roster, not asked again); false when the user went to pick a roster instead (or cancelled).
    /// </summary>
    private bool AskForBaseline()
    {
        if (BaselineApplied || _settings.BaselineDeclined || App.Args.ContainsKey("shot")) return true;
        var answer = MessageBox.Show(Window.GetWindow(this),
            "Starting from a shared roster is highly recommended (step 1): it brings updated ratings, gear and created prospects, and NHL Sync then only has to bring it up to date.\n\n"
            + "Yes: choose a roster now (the sync waits).\nNo: sync the game's own roster, and don't ask again.",
            "Base roster", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.No)
        {
            _settings.BaselineDeclined = true;
            _settings.Save();
            return true;
        }
        if (answer == MessageBoxResult.Yes) _ = PickThenPreviewAsync();
        return false;
    }
}
