using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NHL27Kit.Core;

namespace NHL27Kit.App;

/// <summary>
/// NHL Sync step 3 (optional): portraits for NHL and AHL players who have none (PortraitStage), from the data step 2
/// read. Find fetches and fits the photos and writes the skipped list; Add shows the fitted portraits and writes the
/// ticked ones, each into a free slot in HMS's band (a texture mod entry each), then one roster entry pointing the
/// players' bios at them.
/// </summary>
public partial class RosterSyncView
{
    private List<StagePortrait> _portraits;
    private bool _portraitBusy;

    /// <summary>The skipped-players list (docs/portraits-stage3-plan.md): rewritten by every Find.</summary>
    public static string PortraitSkippedFile => Path.Combine(KitPaths.Export, "portraits-skipped.csv");

    private void SetPortraitButtons()
    {
        bool idle = !_busy && !_planning && !_portraitBusy;
        PortraitFindButton.IsEnabled = idle && _mods != null && _api != null;
        int ready = _portraits?.Count(c => c.Portrait != null) ?? 0;
        PortraitAddButton.IsEnabled = idle && ready > 0;
        PortraitAddLabel.Text = ready > 0 ? $"Add {ready} portrait{(ready == 1 ? "" : "s")}" : "Add portraits";
        PortraitSkippedButton.Visibility = File.Exists(PortraitSkippedFile) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnPortraitFind(object sender, RoutedEventArgs e) => await FindPortraitsAsync();

    /// <summary>Finds the players with no portrait, fetches and fits their photos, writes the skipped list. Nothing in the
    /// game changes. Returns the status text.</summary>
    public async Task<string> FindPortraitsAsync()
    {
        if (_mods == null || _api == null || _portraitBusy) return null;
        bool nhl = PortraitNhl.IsChecked == true, ahlOn = PortraitAhl.IsChecked == true;
        if (!nhl && !ahlOn) return PortraitSay("Tick NHL, AHL or both.");
        _portraitBusy = true;
        _portraits = null;
        SetPortraitButtons();
        try
        {
            PortraitSay("Finding players with no portrait…");
            var db = await _mods.OpenDbAsync(Roster.Database);
            var api = _api;
            var ahl = _ahl;
            var found = await Task.Run(() => PortraitStage.Find(db, _mods.Index, api, ahl, nhl, ahlOn));
            string dir = Path.Combine(_mods.Store.Dir, "portraits");
            Directory.CreateDirectory(dir);
            var todo = found.Where(c => c.Problem == null).ToList();
            int i = 0;
            foreach (var c in todo)
            {
                PortraitSay($"Fetching photos… {++i}/{todo.Count} · {c.Player.Name} ({c.TeamName})");
                try
                {
                    // an AHL player's NHL.com photo is on his CapWages page (cached from step 2)
                    if (c.HeadshotUrl == null)
                    {
                        var shot = await CapWagesApi.HeadshotAsync(c.CapSlug);
                        if (shot is not { Nhl: true } s)
                        {
                            c.Problem = shot == null ? "no photo on CapWages" : "no NHL.com photo (CapWages has only a non-NHL photo)";
                            continue;
                        }
                        c.HeadshotUrl = s.Url;
                    }
                    string raw = Path.Combine(dir, $"{c.Key}.headshot.png");
                    bool cached = File.Exists(raw) && DateTime.Now - File.GetLastWriteTime(raw) < TimeSpan.FromDays(1);
                    if (!cached) await File.WriteAllBytesAsync(raw, await CapWagesApi.BytesAsync(c.HeadshotUrl));
                    var (rgba, w, h) = await Task.Run(() => ImageLoader.Load(raw));
                    c.Portrait = await Task.Run(() => PortraitSync.Fit(rgba, w, h));
                    c.PortraitFile = Path.Combine(dir, $"{c.Key}.portrait.png");
                    SavePortraitPng(c.Portrait, PortraitSync.Width, PortraitSync.Height, c.PortraitFile);
                    if (!cached) await Task.Delay(250);
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or InvalidOperationException or NotSupportedException or FileFormatException)
                {
                    c.Problem = "photo couldn't be read: " + ex.Message;
                    c.Portrait = null;
                }
            }
            _portraits = found;
            Directory.CreateDirectory(KitPaths.Export);
            await File.WriteAllTextAsync(PortraitSkippedFile, PortraitStage.SkippedCsv(found, DateTime.Now), new System.Text.UTF8Encoding(true));
            int ready = found.Count(c => c.Portrait != null), skipped = found.Count(c => c.Problem != null);
            string by(string lg) => $"{lg} {found.Count(c => c.League == lg && c.Portrait != null)} of {found.Count(c => c.League == lg)}";
            return PortraitSay(found.Count == 0
                ? "Every player on the chosen teams already has a portrait."
                : $"{found.Count} player{(found.Count == 1 ? "" : "s")} with no portrait: {ready} have an NHL.com photo ready ({string.Join(", ", new[] { nhl ? by("NHL") : null, ahlOn ? by("AHL") : null }.Where(s => s != null))}), "
                  + $"{skipped} skipped (the Skipped list says why). Nothing has been written yet.");
        }
        catch (Exception ex)
        {
            return PortraitSay("Couldn't look up portraits: " + ex.Message);
        }
        finally
        {
            _portraitBusy = false;
            SetPortraitButtons();
        }
    }

    private async void OnPortraitAdd(object sender, RoutedEventArgs e)
    {
        var ready = _portraits?.Where(c => c.Portrait != null).ToList();
        if (ready == null || ready.Count == 0 || _portraitBusy) return;
        if (ModService.GameRunning)
        {
            MessageBox.Show(Window.GetWindow(this), "KytyPS5 is running. Close the game first: its files can't be changed while it reads them.", "HMS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var keep = PortraitPreview.Show(Window.GetWindow(this), ready);
        if (keep == null || keep.Count == 0) return;
        await WritePortraitsAsync(keep);
    }

    /// <summary>Writes the portraits: each into the next free slot in HMS's band (a texture mod entry each), then one roster
    /// entry pointing the bios at them.</summary>
    private async Task WritePortraitsAsync(List<StagePortrait> keep)
    {
        _portraitBusy = true;
        SetPortraitButtons();
        try
        {
            PortraitSay("Finding free portrait slots…");
            var db = await _mods.OpenDbAsync(Roster.Database);
            var others = new List<T3Db>();
            foreach (var name in PortraitSync.OtherRosters)
                try { others.Add(await _mods.OpenDbAsync(name)); } catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException) { }
            var free = new Queue<int>(await Task.Run(() => PortraitStage.FreeSlots(_mods.Index, db, others)));
            var done = new List<StagePortrait>();
            int i = 0;
            foreach (var c in keep)
            {
                PortraitSay($"Writing portraits… {++i}/{keep.Count} · {c.Player.Name}");
                while (free.Count > 0 && c.NewArtId < 0)
                {
                    int slot = free.Dequeue();
                    string tex = PortraitSync.TextureName(slot);
                    try
                    {
                        var plan = await _mods.PlanTextureAsync(tex, c.PortraitFile, null);
                        var h = plan.Header;
                        var entry = _mods.Queue("texture", tex, c.PortraitFile,
                            $"{c.Player.Name}'s portrait (NHL Sync step 3, NHL.com headshot), {h.Format.Name} {h.Width}×{h.Height}",
                            plan.Chunk.Patches, plan.Chunk.Notes.Where(n => n.StartsWith("left") || n.Contains("shared")).ToList());
                        await _mods.ApplyAsync(entry);
                        c.NewArtId = slot;
                    }
                    // a slot whose image can't be replaced on its own: the next one
                    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { }
                }
                if (c.NewArtId < 0) { c.Problem = $"no free portrait slot left in HMS's range ({PortraitStage.BandFrom}–{PortraitStage.BandTo})"; c.Portrait = null; }
                else done.Add(c);
            }
            string summary = null;
            if (done.Count > 0)
                await _mods.SaveDbAsync(Roster.Database, d => summary = PortraitStage.Apply(d, done), "Portraits (NHL Sync step 3)");
            foreach (var c in done) c.Portrait = null;
            await File.WriteAllTextAsync(PortraitSkippedFile, PortraitStage.SkippedCsv(_portraits, DateTime.Now), new System.Text.UTF8Encoding(true));
            int failed = keep.Count - done.Count;
            string said = (summary ?? "No portraits written") + (failed > 0 ? $"; {failed} couldn't be written (in the Skipped list)" : "")
                + ". They show on the game's default roster and on franchises started from now on (a saved roster or franchise keeps its own copy of the players); the Mods page can restore them.";
            StatusChanged?.Invoke(said);
            return;
        }
        catch (Exception ex)
        {
            PortraitSay("Couldn't add portraits: " + ex.Message);
        }
        finally
        {
            _portraitBusy = false;
            SetPortraitButtons();
        }
    }

    private void OnPortraitSkipped(object sender, RoutedEventArgs e)
    {
        if (File.Exists(PortraitSkippedFile))
            try { Process.Start(new ProcessStartInfo(PortraitSkippedFile) { UseShellExecute = true }); }
            catch (Exception ex) { PortraitSay($"Couldn't open {PortraitSkippedFile}: {ex.Message}"); }
    }

    private string PortraitSay(string text)
    {
        PortraitStatus.Text = text;
        return text;
    }

    private static void SavePortraitPng(byte[] rgba, int w, int h, string file)
    {
        var bmp = TextureService.ToBitmap(rgba, w, h, Channel.Rgba);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}

/// <summary>The fitted portraits before they're written: one card each (photo, name, team), all ticked; untick any that
/// look wrong. Returns the ticked ones, or null when cancelled.</summary>
internal static class PortraitPreview
{
    public static List<StagePortrait> Show(Window owner, List<StagePortrait> ready)
    {
        var ticks = new Dictionary<StagePortrait, CheckBox>();
        var wrap = new WrapPanel { Margin = new Thickness(12) };
        foreach (var c in ready)
        {
            var img = new Image { Width = 192, Height = 128, Stretch = Stretch.Uniform, Source = TextureService.ToBitmap(c.Portrait, PortraitSync.Width, PortraitSync.Height, Channel.Rgba) };
            var box = new CheckBox { IsChecked = true, Content = new TextBlock { Text = c.Player.Name, FontWeight = FontWeights.SemiBold }, Margin = new Thickness(0, 6, 0, 0) };
            ticks[c] = box;
            var card = new Border
            {
                Width = 212, Margin = new Thickness(6), Padding = new Thickness(10), CornerRadius = new CornerRadius(8),
                Background = (Brush)Application.Current.TryFindResource("Surface2") ?? Brushes.DimGray,
                Child = new StackPanel { Children = { new Border { Background = new SolidColorBrush(Color.FromRgb(0x10, 0x1A, 0x2C)), CornerRadius = new CornerRadius(6), Child = img },
                    box, new TextBlock { Text = $"{c.TeamName} · {c.League}", Opacity = 0.7, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis } } },
            };
            wrap.Children.Add(card);
        }
        var ok = new Button { Content = $"Write ticked portraits", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 6, 14, 6), IsCancel = true };
        if (Application.Current.TryFindResource("Primary") is Style primary) ok.Style = primary;
        var bar = new DockPanel { Margin = new Thickness(16, 8, 16, 14), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(new TextBlock { Text = "Each goes into a free portrait slot; untick any photo that's the wrong player or looks off.", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { Content = wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var win = new Window
        {
            Title = $"Add {ready.Count} portrait{(ready.Count == 1 ? "" : "s")}", Owner = owner, Width = 1000, Height = 680,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root,
            Background = (Brush)Application.Current.TryFindResource("Surface") ?? Brushes.Black,
        };
        if (Application.Current.TryFindResource("Text") is Brush text) win.Foreground = text;
        ok.Click += (_, _) => win.DialogResult = true;
        return win.ShowDialog() == true ? ticks.Where(t => t.Value.IsChecked == true).Select(t => t.Key).ToList() : null;
    }
}
