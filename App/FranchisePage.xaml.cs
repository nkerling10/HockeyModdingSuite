using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NHL27Kit.Core;

namespace NHL27Kit.App;

/// <summary>A franchise save on the picker.</summary>
public sealed class FrSlotCard
{
    public FrSlotCard(FranchiseSlot slot) => Slot = slot;
    public FranchiseSlot Slot { get; }
    public string TeamText => string.IsNullOrEmpty(Slot.Team) ? Slot.Name : Slot.Team;
    /// <summary>"MW" for Minnesota Wild: the card's big letters.</summary>
    public string Initials => new(TeamText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0])).Take(3).ToArray());
    public string Saved => Slot.Modified > DateTime.MinValue ? "Saved " + Slot.Modified.ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture) : "";
    public Brush Banner => new LinearGradientBrush((Color)Application.Current.FindResource("NavyColor"), Color.FromRgb(0x1A, 0x32, 0x59), 45);
}

/// <summary>A label and value on the player and team cards.</summary>
public sealed record FrFact(string Label, string Value, string Hint = null);

/// <summary>A rating with its bar.</summary>
public sealed record FrRating(string Label, int Value)
{
    public Brush Brush => RosterVisuals.RatingBrush(Value);
    public GridLength Fill => new(Math.Clamp(Value, 0, 99), GridUnitType.Star);
    public GridLength Rest => new(99 - Math.Clamp(Value, 0, 99), GridUnitType.Star);
}

/// <summary>A team colour on the team card.</summary>
public sealed record FrSwatch(string Label, string Field, int Rgb)
{
    public Brush Brush => new SolidColorBrush(Color.FromRgb((byte)(Rgb >> 16), (byte)(Rgb >> 8), (byte)Rgb));
    public string Hex => $"#{Rgb:X6}";
}

/// <summary>A player in a team's list.</summary>
public sealed record FrPlayerChip(FranchisePlayer Player, string Name, string Position, string Number, int Rating)
{
    public string Overall => Rating > 0 ? Rating.ToString() : "–";
    public Brush OverallBrush => RosterVisuals.RatingBrush(Rating);
}

/// <summary>A league table in the list.</summary>
public sealed record FrTableRow(T3Table Table, int Rows)
{
    public string Name => Table.Name;
    public string Meta => $"{Rows:N0} rows  ·  {Table.Fields.Count} fields  ·  {Table.ShortName}";
    public Brush NameBrush => (Brush)Application.Current.FindResource(Rows > 0 ? "Text" : "Faint");
}

/// <summary>A team choice in a filter box (-2 every team, -1 none).</summary>
public sealed record FrTeamChoice(int Team, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The franchise explorer: picks a franchise save from KytyPS5's save folders, then shows what it holds: every player
/// record (bio, contract, draft, ratings, X-Factors, the record's bytes), the league database's teams, rosters, GMs,
/// draft picks and schedule, every league table as it is, and the save's blocks. Read only for now.
/// </summary>
public partial class FranchisePage : UserControl
{
    private const string Meta = "gamemodes/database/t3db/nhl_game-meta.xml", LocDb = "loc/eng_us.db";
    private const int AllTeams = -2, NoTeam = -1;

    private sealed record TeamInfo(int Index, string Abbr, string Name, string City, int Primary, int Secondary);

    private Settings _settings;
    private string _titleId;
    private ModService _mods;
    private string _meta;
    private List<XFactorAbility> _abilities;
    private string[] _tierNames;
    private FranchiseSave _save;
    private readonly Dictionary<int, TeamInfo> _teams = new();
    private DataTable _players, _teamRows, _gridRows, _tableRows;
    private bool _busy;
    private readonly DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Action _pendingFilter;

    public event Action<string> StatusChanged;

    /// <summary>Where the saves are read from, when a scripted run points elsewhere (a save folder or one slot).</summary>
    public string SavesDirOverride { get; set; }

    public FranchisePage()
    {
        InitializeComponent();
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); _pendingFilter?.Invoke(); };
    }

    public void Init(Settings settings, string titleId)
    {
        _settings = settings;
        _titleId = titleId;
    }

    public void SetMods(ModService mods) => _mods = mods;

    private string TitleId => string.IsNullOrEmpty(_titleId) ? "PPSA34063" : _titleId;

    // ================================================================ finding the saves

    /// <summary>
    /// The save folders to look in: the run folder KytyPS5 is set to, the save folder of the emulator the kit uses or
    /// finds next to it (one folder deeper too: NHL Games\NHL 27\KytyPS5-…), and the folders picked here before.
    /// </summary>
    private List<string> SaveFolders()
    {
        if (SavesDirOverride != null) return new() { SavesDirOverride };
        var list = new List<string>();
        try { list.Add(BeAProSave.SavesDir(KytySetup.Load(TitleId, _settings?.Emulator).RunDir, TitleId)); } catch { }
        try
        {
            string emu = _settings?.Emulator is { } e && File.Exists(e) ? e : KytySetup.FindEmulator();
            if (emu != null) list.Add(Path.Combine(Path.GetDirectoryName(emu), "_SaveData", TitleId));
        }
        catch { }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            try
            {
                foreach (var sub in dir.GetDirectories())
                    foreach (var kyty in sub.GetDirectories("KytyPS5-*"))
                        list.Add(Path.Combine(kyty.FullName, "_SaveData", TitleId));
            }
            catch { /* folders we can't list */ }
        }
        if (_settings?.FranchiseFolders != null) list.AddRange(_settings.FranchiseFolders);
        return list.Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The page was opened: the picker until a franchise is open.</summary>
    public async Task Shown()
    {
        if (_busy || _save != null) return;
        await ShowPickerAsync();
    }

    /// <summary>Opens a franchise and shows a tab (scripted runs): --frslot NAME, --frtab, --frfind, --frselect.</summary>
    public async Task Show(string slot, string tab, string find, string select)
    {
        await ShowPickerAsync();
        var cards = SlotCards.ItemsSource as List<FrSlotCard> ?? new();
        var card = cards.FirstOrDefault(c => c.Slot.Name.Equals(slot ?? "", StringComparison.OrdinalIgnoreCase)) ?? (slot == null ? cards.FirstOrDefault() : null);
        if (card == null && slot != null && Directory.Exists(slot)) await OpenAsync(slot);
        else if (card != null) await OpenAsync(card.Slot.Dir);
        if (_save == null) return;
        var tabButton = (tab ?? "players").ToLowerInvariant() switch
        {
            "teams" => TabTeams, "rosters" => TabRosters, "gms" => TabGms, "picks" => TabPicks, "schedule" => TabSchedule,
            "tables" => TabTables, "blocks" => TabBlocks, _ => TabPlayers,
        };
        tabButton.IsChecked = true;
        ShowTab();
        if (find != null)
        {
            if (TabPlayers.IsChecked == true) { PlayerSearch.Text = find; ApplyPlayerFilter(); }
            else if (TabTeams.IsChecked == true) { TeamSearch.Text = find; ApplyTeamFilter(); }
            else if (TabTables.IsChecked == true) TableList.SelectedItem = (TableList.ItemsSource as List<FrTableRow>)?.FirstOrDefault(t => t.Name.Contains(find, StringComparison.OrdinalIgnoreCase));
            else { GridSearch.Text = find; ApplyGridFilter(); }
        }
        if (int.TryParse(select, out int n))
        {
            var grid = TabPlayers.IsChecked == true ? PlayerGrid : TabTeams.IsChecked == true ? TeamGrid : TabTables.IsChecked == true ? TableGrid : RowGrid;
            if (n >= 0 && n < grid.Items.Count) { grid.SelectedIndex = n; grid.ScrollIntoView(grid.SelectedItem); }
        }
        await Task.Delay(300);
    }

    /// <summary>Scrolls the player card (scripted runs: --frscroll Y).</summary>
    public void ScrollCard(double y)
    {
        PlayerScroll.UpdateLayout();
        PlayerScroll.ScrollToVerticalOffset(y);
    }

    private async Task ShowPickerAsync()
    {
        _busy = true;
        try
        {
            var folders = SaveFolders();
            var slots = await Task.Run(() => folders.SelectMany(FranchiseSave.Slots).OrderBy(s => s.IsBackup).ThenByDescending(s => s.Modified).ToList());
            // a folder pointed at one slot
            if (SavesDirOverride != null && FranchiseSave.ReadSlot(SavesDirOverride) is { } one) slots.Insert(0, one);
            SlotCards.ItemsSource = slots.Select(s => new FrSlotCard(s)).ToList();
            SlotsEmpty.Text = slots.Count > 0 ? $"Looked in {string.Join(", ", folders)}"
                : folders.Count == 0 ? "No KytyPS5 save folder found. Play NHL 27 in KytyPS5 once, or point the kit at the save folder."
                : $"No franchise saves in {string.Join(", ", folders)}. Start a franchise in the game, or point the kit at another save folder.";
            Summary.Text = $"{slots.Count} franchise save{(slots.Count == 1 ? "" : "s")} found. Pick one to explore it.";
            PageTitle.Text = "Franchise";
            PickerView.Visibility = Visibility.Visible;
            ExplorerView.Visibility = Visibility.Collapsed;
            ChangeButton.Visibility = Visibility.Collapsed;
        }
        finally { _busy = false; }
    }

    private async void OnSlotCard(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FrSlotCard card) await OpenAsync(card.Slot.Dir);
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose a franchise save (BeAGM1…) or the save folder that holds them (…\\_SaveData\\PPSA34063)" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        string dir = dlg.FolderName;
        bool slot = File.Exists(Path.Combine(dir, "DATA"));
        string folder = slot ? Path.GetDirectoryName(dir) : dir;
        if (_settings != null && !_settings.FranchiseFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            _settings.FranchiseFolders.Add(folder);
            _settings.Save();
        }
        if (slot) await OpenAsync(dir);
        else await ShowPickerAsync();
    }

    private async void OnChange(object sender, RoutedEventArgs e)
    {
        _save = null;
        await ShowPickerAsync();
    }

    private async void OnReload(object sender, RoutedEventArgs e)
    {
        if (_save != null) await OpenAsync(_save.Dir);
        else await ShowPickerAsync();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        string dir = _save?.Dir ?? SaveFolders().FirstOrDefault();
        if (dir != null && Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    // ================================================================ opening

    private async Task OpenAsync(string dir)
    {
        _busy = true;
        Busy.Text = $"Reading {Path.GetFileName(dir)}…";
        Busy.Visibility = Visibility.Visible;
        PickerView.Visibility = Visibility.Collapsed;
        StatusChanged?.Invoke(Busy.Text);
        try
        {
            if (_meta == null && _mods != null)
            {
                try { _meta = await _mods.LegacyTextAsync(Meta); }
                catch { /* the league tables keep their short names */ }
            }
            if (_abilities == null && _mods != null)
            {
                try
                {
                    var loc = await _mods.OpenDbAsync(LocDb);
                    (_abilities, _tierNames) = await Task.Run(() => (XFactors.Abilities(loc), XFactors.TierNames(loc)));
                }
                catch { /* ids are shown instead of names */ }
            }
            string meta = _meta;
            _save = await Task.Run(() => FranchiseSave.Open(dir, meta));
            await FillAsync();
            var s = _save.Slot;
            PageTitle.Text = string.IsNullOrEmpty(s.Team) ? $"Franchise · {s.Name}" : $"Franchise · {s.Team}";
            var (ok, total) = _save.League?.CheckCrcs() ?? (0, 0);
            Summary.Text = $"{s.Name}  ·  {s.Detail}  ·  {_save.Players.Count:N0} players, {_save.League?.Tables.Count ?? 0} league tables (CRCs {ok}/{total})"
                           + (_meta == null ? "  ·  table names need the game's index (open after it loads)" : "");
            ExplorerView.Visibility = Visibility.Visible;
            ChangeButton.Visibility = Visibility.Visible;
            ShowTab();
            StatusChanged?.Invoke($"Read the franchise {s.Name}: {_save.Players.Count:N0} players");
        }
        catch (Exception ex)
        {
            _save = null;
            await ShowPickerAsync();
            Summary.Text = $"Couldn't read {Path.GetFileName(dir)}: {ex.Message}";
            StatusChanged?.Invoke(Summary.Text);
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            _busy = false;
        }
    }

    private async Task FillAsync()
    {
        _teams.Clear();
        var db = _save.League;
        var teams = db?.Table("leagueteams") ?? db?.Table("RGbs");
        if (teams != null)
            foreach (int r in Rows(teams))
            {
                int idx = Int(teams, r, "index", r);
                _teams[idx] = new TeamInfo(idx, Str(teams, r, "abbrname", "xsnn"), Str(teams, r, "teamname", "cXHn"), Str(teams, r, "cityname", "bhQD"),
                                           Int(teams, r, "presprimarycolor"), Int(teams, r, "pressecondarycolor"));
            }

        var choices = new List<FrTeamChoice> { new(AllTeams, "All teams"), new(NoTeam, "No team (free agents, prospects…)") };
        choices.AddRange(_teams.Values.OrderBy(t => t.Index).Select(t => new FrTeamChoice(t.Index, $"{t.Abbr}  ·  {t.Name}")));
        PlayerTeam.ItemsSource = choices;
        PlayerTeam.SelectedIndex = 0;
        GridTeam.ItemsSource = choices.Where(c => c.Team != NoTeam).ToList();
        GridTeam.SelectedIndex = 0;

        _players = await Task.Run(BuildPlayers);
        BuildColumns(PlayerGrid, _players);
        PlayerGrid.ItemsSource = _players.DefaultView;
        ApplyPlayerFilter();
        ShowPlayer(null);

        _teamRows = BuildTeams();
        BuildColumns(TeamGrid, _teamRows);
        TeamGrid.ItemsSource = _teamRows.DefaultView;
        TeamSearch.Text = "";
        ShowTeam(-1);

        TableList.ItemsSource = db == null ? new List<FrTableRow>() : db.Tables.OrderBy(t => t.Name).Select(t => new FrTableRow(t, Rows(t).Count())).ToList();
        TableCount.Text = db == null ? "" : $"{db.Tables.Count}";
        TableGrid.ItemsSource = null;
        TableTitle.Text = "Pick a table";
        TableMeta.Text = "";
    }

    // ================================================================ tabs

    private void OnTab(object sender, RoutedEventArgs e)
    {
        if (_save != null && IsLoaded) ShowTab();
    }

    private void ShowTab()
    {
        PlayersView.Visibility = TabPlayers.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TeamsView.Visibility = TabTeams.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TablesView.Visibility = TabTables.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        bool grid = TabRosters.IsChecked == true || TabGms.IsChecked == true || TabPicks.IsChecked == true || TabSchedule.IsChecked == true || TabBlocks.IsChecked == true;
        RowsView.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        if (!grid) return;
        GridSearch.Text = "";
        if (TabRosters.IsChecked == true) ShowGrid("Rosters", "leaguerostertable: who plays for whom, jersey, captaincy and every line slot (EV, PP, PK, 3-on-3, shootout).", BuildRosters());
        else if (TabGms.IsChecked == true) ShowGrid("GMs", "leaguegmdata: each NHL team's GM, payroll and cap figures.", BuildGms());
        else if (TabPicks.IsChecked == true) ShowGrid("Draft picks", "leaguedraftpick: who owns each pick (and who it was used on).", BuildPicks());
        else if (TabSchedule.IsChecked == true) ShowGrid("Schedule", "leaguescheduletable: every game of the season.", BuildSchedule());
        else ShowGrid("Save blocks", $"The franchise file's blocks ({_save.Dir}).", BuildBlocks());
    }

    private void ShowGrid(string title, string meta, DataTable table)
    {
        _gridRows = table;
        GridTitle.Text = title;
        GridMeta.Text = $"{table.Rows.Count:N0} rows  ·  {meta}";
        GridTeam.Visibility = table.Columns.Contains("_team") ? Visibility.Visible : Visibility.Collapsed;
        GridTeam.SelectionChanged -= OnGridFilter;
        GridTeam.SelectedIndex = 0;
        GridTeam.SelectionChanged += OnGridFilter;
        BuildColumns(RowGrid, table);
        RowGrid.ItemsSource = table.DefaultView;
        ApplyGridFilter();
    }

    // ================================================================ players

    /// <summary>The players as rows: the columns a list wants first, then every field of the record.</summary>
    private DataTable BuildPlayers()
    {
        var t = new DataTable();
        void Col(string name, string header, Type type) => t.Columns.Add(new DataColumn(name, type) { Caption = header });
        Col("idx", "#", typeof(int));
        Col("name", "Name", typeof(string));
        Col("team", "Team", typeof(string));
        Col("pos", "Pos", typeof(string));
        Col("num", "No.", typeof(int));
        Col("ovr", "OVR", typeof(int));
        Col("age", "Age", typeof(int));
        Col("ht", "Height", typeof(string));
        Col("wt", "Weight", typeof(int));
        Col("salary", "Salary ($)", typeof(long));
        Col("yrs", "Years", typeof(int));
        Col("shoots", "Shoots", typeof(string));
        Col("birthplace", "Birthplace", typeof(string));
        Col("rights", "NHL rights", typeof(string));
        var keys = FranchiseSave.Fields.Select(f => f.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var k in keys) Col("f_" + k, FieldLabel(k), typeof(int));
        Col("_team", "", typeof(int));
        Col("_pos", "", typeof(int));
        Col("_search", "", typeof(string));
        Col("_player", "", typeof(object));

        DateTime season = SeasonDate();
        t.BeginLoadData();
        foreach (var p in _save.Players)
        {
            var row = t.NewRow();
            int team = p.CurrentTeam, pos = p.Value("position"), ht = p.Value("height");
            row["idx"] = p.Index;
            row["name"] = p.Name;
            row["team"] = TeamAbbr(team);
            row["pos"] = Roster.PositionDisplay(p.Value("positionprimary"), p.Value("positionsecondary") is int s and >= 0 and <= 5 ? s : -1);
            row["num"] = p.Value("jersey");
            row["ovr"] = p.Value("skill_overall");
            row["age"] = Age(p, season);
            row["ht"] = $"{ht / 12}'{ht % 12}\"";
            row["wt"] = p.Value("weight");
            row["salary"] = (long)p.Value("contractdollars");
            row["yrs"] = p.Value("contractlength");
            row["shoots"] = p.Handedness == 1 ? "R" : "L";
            row["birthplace"] = p.Birthplace;
            row["rights"] = TeamAbbr(p.Value("proteam") is int r and < 255 ? r : -1);
            foreach (var k in keys)
                row["f_" + k] = FranchiseSave.Field(k, p.IsGoalie) is { } f ? (int)p.Value(f) : DBNull.Value;
            row["_team"] = team;
            row["_pos"] = pos;
            row["_search"] = $"{p.Name} {p.Birthplace} {p.Value("game_id")} {TeamAbbr(team)}".ToLowerInvariant();
            row["_player"] = p;
            t.Rows.Add(row);
        }
        t.EndLoadData();
        return t;
    }

    private void OnPlayerFilter(object sender, RoutedEventArgs e) => Debounce(ApplyPlayerFilter, sender is TextBox);

    private void ApplyPlayerFilter()
    {
        if (_players == null) return;
        var parts = new List<string>();
        if (PlayerSearch.Text.Trim() is { Length: > 0 } q) parts.Add($"_search LIKE '%{Like(q.ToLowerInvariant())}%'");
        if (PlayerTeam.SelectedItem is FrTeamChoice { Team: not AllTeams } tc) parts.Add($"_team = {tc.Team}");
        if (PosF.IsChecked == true) parts.Add("_pos IN (0, 1, 2)");
        else if (PosD.IsChecked == true) parts.Add("_pos = 3");
        else if (PosG.IsChecked == true) parts.Add("_pos = 4");
        _players.DefaultView.RowFilter = string.Join(" AND ", parts);
        PlayerCount.Text = $"{_players.DefaultView.Count:N0} of {_players.Rows.Count:N0} players  ·  scroll right for every field of the record";
    }

    private void OnPlayerPicked(object sender, SelectionChangedEventArgs e) =>
        ShowPlayer((PlayerGrid.SelectedItem as DataRowView)?["_player"] as FranchisePlayer);

    private void ShowPlayer(FranchisePlayer p)
    {
        PlayerCard.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
        PlayerEmpty.Visibility = p == null ? Visibility.Visible : Visibility.Collapsed;
        if (p == null) return;
        int team = p.CurrentTeam, ht = p.Value("height"), primary = p.Value("positionprimary"), secondary = p.Value("positionsecondary");
        var info = _teams.GetValueOrDefault(team);
        PlayerBanner.Background = Banner(info?.Primary ?? 0);
        PlayerFirst.Text = p.FirstName;
        PlayerLast.Text = p.LastName.ToUpperInvariant();
        int overall = p.Value("skill_overall");
        PlayerOverall.Text = overall.ToString();
        string pos = Roster.PositionDisplay(primary, secondary is >= 0 and <= 5 ? secondary : -1);
        PlayerLine.Text = string.Join("  ·  ", new[] { pos, $"#{p.Value("jersey")}", info?.Name ?? "No team", $"game id {p.Value("game_id")}", $"player {p.Index}" });

        DateTime season = SeasonDate();
        int day = p.Value("day"), month = p.Value("month"), year = p.Value("year");
        string born = month is >= 1 and <= 12 && day is >= 1 and <= 31 ? $"{new DateTime(year, month, Math.Min(day, DateTime.DaysInMonth(year, month))):MMM d, yyyy}  (age {Age(p, season)})" : $"{year}";
        BioFacts.ItemsSource = new List<FrFact>
        {
            new("Born", born), new("Birthplace", p.Birthplace),
            new("Height", $"{ht / 12}'{ht % 12}\"  ({ht} in)"), new("Weight", $"{p.Value("weight")} lb"),
            new(p.IsGoalie ? "Catches" : "Shoots", p.Handedness == 1 ? "Right" : "Left"),
            new("Position", Roster.PositionNameFull(primary) + (secondary is >= 0 and <= 5 ? $"  (also {Roster.PositionNameFull(secondary)})" : "")),
            new("Team", info?.Name ?? "None"), new("NHL rights", p.Value("proteam") is int r and < 255 ? _teams.GetValueOrDefault(r)?.Name ?? $"{r}" : "None"),
            new("Country", p.Value("intlcountry").ToString(), "intlcountry"), new("Personality", p.Value("personalitytype").ToString(), "personalitytype"),
            new("Nickname", string.IsNullOrEmpty(p.Nickname) ? "–" : p.Nickname), new("Injury", InjuryText(p)),
        };

        int extYears = p.Value("extensionlength");
        ContractFacts.ItemsSource = new List<FrFact>
        {
            new("Salary", $"${p.Value("contractdollars"):N0}"), new("Years remaining", p.Value("contractlength").ToString()),
            new("Extension", extYears > 0 ? $"{extYears} yr at ${p.Value("extensiondollars"):N0}" : "None"),
            new("First signed", p.Value("yearfirstsigned").ToString()), new("Waiver eligible", p.Value("yearwaivereligible").ToString()),
            new("UFA year", p.Value("yearufa").ToString()), new("Expansion draft", p.Value("expansiondraftstatus").ToString(), "expansiondraftstatus"),
            new("Two-way", p.Value("contractis2way") == 1 ? "Yes" : "No"), new("Extension interest", ExtensionText(p), "extensioninterest: 1 No, 3 and 6 Yes, 0 no talks"),
        };
        TradeChips.Children.Clear();
        for (int i = 0; i < 9; i++)
        {
            int clause = p.Value($"contractclause{i}"), teams = p.Value($"contractclausemntcnumteams{i}");
            if (clause <= 0) continue;
            string name = ClauseEdit.Choices.FirstOrDefault(c => c.Value == clause)?.Name ?? $"clause {clause}";
            TradeChips.Children.Add(Chip($"Year {i + 1}: {name}" + (teams > 0 ? $"  ·  {teams} teams" : "")));
        }
        if (TradeChips.Children.Count == 0) TradeChips.Children.Add(Chip("No trade protection"));

        int draftTeam = p.Value("draftteam"), round = p.Value("draftround");
        DraftFacts.ItemsSource = new List<FrFact>
        {
            new("Draft", round > 0 ? $"{p.Value("draftyear")}  ·  round {round}, pick {p.Value("draftposition")}  ·  {TeamName(draftTeam)}" : "Undrafted"),
            new("Pro debut", p.Value("yearprodebut").ToString()),
            new("NHL games", $"{p.Value("nhlgamesplayedcareer"):N0}  ({p.Value("nhlcareergamesplayedregularseason"):N0} regular season)"),
            new("NHL goals (reg.)", $"{p.Value("nhlcareergoalsregularseason"):N0}"), new("Pro games", $"{p.Value("progamesplayedcareer"):N0}"),
            new("NHL GP, last 3", $"{p.Value("nhlgamesplayedlastseason")} / {p.Value("nhlgamesplayedtwoseasonsago")} / {p.Value("nhlgamesplayedthreeseasonsago")}"),
            new("Cups won", p.Value("cupswon") > 0 ? $"{p.Value("cupswon")} (last {p.Value("cupsyearlastwon") + 1900})" : "0"),
        };

        var fields = FranchiseSave.Fields.Where(f => f.AppliesTo(p.IsGoalie)).ToList();
        RatingsTitle.Text = p.IsGoalie ? "GOALIE RATINGS" : "SKATER RATINGS";
        Ratings.ItemsSource = fields.Where(f => f.Group == "Ratings").Select(f => new FrRating(FieldLabel(f.Key), (int)p.Value(f)))
            .OrderBy(r => r.Label == "Overall" ? 0 : 1).ThenBy(r => r.Label).ToList();
        CategoryRatings.ItemsSource = fields.Where(f => f.Group == "Category ratings").Select(f => new FrRating(FieldLabel(f.Key).Replace("Skill ", ""), (int)p.Value(f))).ToList();

        var xfs = new List<FrFact>();
        for (int i = 1; i <= 3; i++)
        {
            var af = FranchiseSave.Field($"xfactor{i}ability", p.IsGoalie);
            if (af == null) continue;
            int id = (int)p.Value(af), tier = p.Value($"xfactor{i}tier");
            if (id < 0) continue;
            string name = _abilities?.FirstOrDefault(a => a.Id == id)?.Name ?? $"Ability {id}";
            string tierName = tier >= 1 && _tierNames != null && tier <= _tierNames.Length ? _tierNames[tier - 1] : tier switch { 1 => "Specialist", 2 => "All-Star", 3 => "Elite", _ => $"tier {tier}" };
            xfs.Add(new FrFact(name, tierName, af.Confirmed ? $"X-Factor {i}: ability {id}" : $"X-Factor {i}: ability {id} (slot not confirmed for goalies)"));
        }
        if (xfs.Count == 0) xfs.Add(new FrFact("No X-Factors", "–"));
        XFactorList.ItemsSource = xfs;

        StyleFacts.ItemsSource = fields.Where(f => f.Group is "Growth" or "Style" or "Traits" or "Skating style")
            .Select(f => new FrFact(FieldLabel(f.Key), p.Value(f).ToString(), $"{f.Key} @ 0x{f.Offset:X3}")).ToList();
        LookFacts.ItemsSource = fields.Where(f => f.Group is "Appearance" or "Identity")
            .Select(f => new FrFact(FieldLabel(f.Key), p.Value(f).ToString(), $"{f.Key} @ 0x{f.Offset:X3}")).ToList();

        var all = new DataTable();
        foreach (var (c, type) in new[] { ("Group", typeof(string)), ("Field", typeof(string)), ("Key", typeof(string)), ("Offset", typeof(string)), ("Stored", typeof(int)), ("Shown", typeof(long)) })
            all.Columns.Add(new DataColumn(c, type) { Caption = c });
        foreach (var f in fields.OrderBy(f => f.Offset))
            all.Rows.Add(f.Group, FieldLabel(f.Key) + (f.Confirmed ? "" : " (?)"), f.Key, $"0x{f.Offset:X3}", p.Raw(f), p.Value(f));
        BuildColumns(FieldGrid, all);
        FieldGrid.ItemsSource = all.DefaultView;
        PlayerHex.Text = Hex(p.Bytes());
        PlayerScroll.ScrollToTop();
    }

    // ================================================================ teams

    private DataTable BuildTeams()
    {
        var t = new DataTable();
        void Col(string name, string header, Type type) => t.Columns.Add(new DataColumn(name, type) { Caption = header });
        Col("idx", "#", typeof(int));
        Col("abbr", "Abbr", typeof(string));
        Col("name", "Team", typeof(string));
        Col("city", "City", typeof(string));
        Col("arena", "Arena", typeof(string));
        Col("gm", "GM", typeof(string));
        Col("payroll", "Payroll ($)", typeof(long));
        Col("players", "Players", typeof(int));
        Col("primary", "Primary colour", typeof(string));
        Col("_search", "", typeof(string));
        var db = _save.League;
        var arena = db?.Table("leaguearena");
        var gm = db?.Table("leaguegmdata");
        var gms = new Dictionary<int, int>();
        if (gm != null) foreach (int r in Rows(gm)) gms[Int(gm, r, "index", r)] = r;
        var counts = _save.Players.GroupBy(p => p.CurrentTeam).ToDictionary(g => g.Key, g => g.Count());
        foreach (var team in _teams.Values.OrderBy(x => x.Index))
        {
            string arenaName = arena != null && team.Index < arena.WrittenRecords ? Str(arena, team.Index, "arenaname") : "";
            string gmName = gms.TryGetValue(team.Index, out int g) ? $"{Str(gm, g, "firstname")} {Str(gm, g, "lastname")}".Trim() : "";
            object payroll = gms.TryGetValue(team.Index, out g) ? (long)Int(gm, g, "salary") : DBNull.Value;
            t.Rows.Add(team.Index, team.Abbr, team.Name, team.City, arenaName, gmName, payroll, counts.GetValueOrDefault(team.Index), $"#{team.Primary:X6}",
                       $"{team.Abbr} {team.Name} {team.City} {arenaName} {gmName}".ToLowerInvariant());
        }
        return t;
    }

    private void OnTeamFilter(object sender, TextChangedEventArgs e) => Debounce(ApplyTeamFilter, true);

    private void ApplyTeamFilter()
    {
        if (_teamRows == null) return;
        _teamRows.DefaultView.RowFilter = TeamSearch.Text.Trim() is { Length: > 0 } q ? $"_search LIKE '%{Like(q.ToLowerInvariant())}%'" : "";
    }

    private void OnTeamPicked(object sender, SelectionChangedEventArgs e) => ShowTeam(TeamGrid.SelectedItem is DataRowView v ? (int)v["idx"] : -1);

    private void ShowTeam(int index)
    {
        var team = _teams.GetValueOrDefault(index);
        TeamCard.Visibility = team == null ? Visibility.Collapsed : Visibility.Visible;
        TeamEmpty.Visibility = team == null ? Visibility.Visible : Visibility.Collapsed;
        if (team == null) return;
        var db = _save.League;
        var teams = db.Table("leagueteams") ?? db.Table("RGbs");
        int row = Rows(teams).FirstOrDefault(r => Int(teams, r, "index", r) == index);
        TeamBanner.Background = Banner(team.Primary);
        TeamCardAbbr.Text = team.Abbr;
        TeamCardName.Text = team.Name;
        TeamLine.Text = string.Join("  ·  ", new[] { team.City, $"team {team.Index}" }.Where(s => !string.IsNullOrEmpty(s)));

        // the colours the team is shown in, without the arena-effect ones (stanchions, spotlights, mascots…)
        TeamColors.ItemsSource = teams.Fields.Where(f => f.Name.EndsWith("color", StringComparison.OrdinalIgnoreCase) && (f.Name.StartsWith("pres") || f.Name.StartsWith("fe") || f.Name.StartsWith("overlay")))
            .Select(f => (f, v: (int)db.Get(teams, row, f))).Where(x => x.v > 0)
            .Select(x => new FrSwatch(ColorLabel(x.f.Name), x.f.Name, x.v)).ToList();

        var facts = new List<FrFact> { new("Full name", Str(teams, row, "nickname")), new("Art abbreviation", Str(teams, row, "artabbr")) };
        var arena = db.Table("leaguearena");
        if (arena != null && index < arena.WrittenRecords)
        {
            facts.Add(new("Arena", Str(arena, index, "arenaname")));
            facts.Add(new("Arena city", Str(arena, index, "cityname")));
        }
        var gm = db.Table("leaguegmdata");
        if (gm != null && Rows(gm).Where(r => Int(gm, r, "index", r) == index).Select(r => (int?)r).FirstOrDefault() is int g)
        {
            facts.Add(new("GM", $"{Str(gm, g, "firstname")} {Str(gm, g, "lastname")}".Trim()));
            facts.Add(new("Payroll", $"${Int(gm, g, "salary"):N0}"));
            facts.Add(new("Payroll next year", $"${Int(gm, g, "salarynextyear"):N0}"));
            facts.Add(new("Stanley Cups", Int(gm, g, "stanleycups").ToString()));
        }
        facts.Add(new("Team type", Int(teams, row, "teamtype").ToString(), "teamtype"));
        facts.Add(new("League", Int(teams, row, "leagueindex").ToString(), "leagueindex"));
        TeamFacts.ItemsSource = facts;

        var players = _save.Players.Where(p => p.CurrentTeam == index)
            .OrderBy(p => p.Value("position") switch { 4 => 2, 3 => 1, _ => 0 }).ThenByDescending(p => p.Value("skill_overall"))
            .Select(p => new FrPlayerChip(p, p.Name, Roster.PositionName(p.Value("position")), $"#{p.Value("jersey")}", p.Value("skill_overall"))).ToList();
        TeamPlayers.ItemsSource = players;
        TeamRosterTitle.Text = $"PLAYERS ({players.Count})";
    }

    private static string ColorLabel(string field)
    {
        string kind = field.StartsWith("pres") ? "Presentation" : field.StartsWith("fe") ? "Menus" : "Overlay";
        string which = field.Contains("primary") ? "1" : field.Contains("secondary") ? "2" : "3";
        return $"{kind} {which}";
    }

    private void OnTeamPlayerOpen(object sender, MouseButtonEventArgs e)
    {
        if (TeamPlayers.SelectedItem is not FrPlayerChip chip) return;
        PlayerSearch.Text = "";
        PlayerTeam.SelectedIndex = 0;
        PosAll.IsChecked = true;
        ApplyPlayerFilter();
        TabPlayers.IsChecked = true;
        ShowTab();
        var view = _players.DefaultView.Cast<DataRowView>().FirstOrDefault(v => v["_player"] == chip.Player);
        if (view == null) return;
        PlayerGrid.SelectedItem = view;
        PlayerGrid.ScrollIntoView(view);
    }

    // ================================================================ rosters, GMs, picks, schedule, blocks

    private static readonly HashSet<string> RosterKeys = new(StringComparer.OrdinalIgnoreCase) { "key", "team", "playerindex", "jersey", "captain", "rosterstatus", "playerstyle" };

    private DataTable BuildRosters()
    {
        var t = NewGrid(("team", "Team", typeof(string)), ("player", "Player", typeof(string)), ("pos", "Pos", typeof(string)), ("num", "No.", typeof(int)),
                        ("ovr", "OVR", typeof(int)), ("captain", "Captain", typeof(string)), ("status", "Status", typeof(int)), ("style", "Style", typeof(int)),
                        ("slots", "Line slots", typeof(string)), ("pidx", "Player #", typeof(int)), ("row", "Row", typeof(int)), ("_team", "", typeof(int)));
        var db = _save.League;
        var roster = db?.Table("leaguerostertable");
        if (roster == null) return t;
        var slotFields = roster.Fields.Where(f => !RosterKeys.Contains(f.Name)).ToList();
        foreach (int r in Rows(roster))
        {
            int team = Int(roster, r, "team"), pi = Int(roster, r, "playerindex");
            var p = pi >= 0 && pi < _save.Players.Count ? _save.Players[pi] : null;
            string slots = string.Join("  ·  ", slotFields.Where(f => (int)db.Get(roster, r, f) == 1).Select(f => SlotName(f.Name)).OrderBy(s => s));
            int captain = Int(roster, r, "captain");
            AddRow(t, TeamAbbr(team), p?.Name ?? $"player {pi}", p == null ? "" : Roster.PositionName(p.Value("position")), Int(roster, r, "jersey"),
                   p?.Value("skill_overall") ?? 0, captain switch { 0 => "", 1 => "C", 2 => "A", _ => captain.ToString() },
                   Int(roster, r, "rosterstatus"), Int(roster, r, "playerstyle"), slots, pi, r, team);
        }
        return t;
    }

    /// <summary>
    /// A line slot of leaguerostertable, readable: "l1lw" → "L1 LW", "pp_2c" → "PP2 C", "pp4_1rd" → "PP4 1 RD" (the
    /// four-man unit), "pk3_1c" → "PK3 1 C", "ev4_2ld" → "EV4 2 LD", "three_w" → "3v3 W", "s3" → "SO3", "g1" → "G1".
    /// </summary>
    private static string SlotName(string f)
    {
        string s = f.ToUpperInvariant();
        if (s.StartsWith("THREE_")) return "3v3 " + s[6..];
        var m = Regex.Match(s, @"^(PP4|PK3|PK4|EV3|EV4|PP|L)_?(\d)([A-Z]+)$");
        if (m.Success)
        {
            string kind = m.Groups[1].Value, unit = m.Groups[2].Value, pos = m.Groups[3].Value;
            return kind is "L" or "PP" ? $"{kind}{unit} {pos}" : $"{kind} {unit} {pos}";
        }
        if (s.Length == 2 && s[0] == 'S') return "SO" + s[1];
        return s;
    }

    private DataTable BuildGms()
    {
        var db = _save.League;
        var gm = db?.Table("leaguegmdata");
        var t = NewGrid(("team", "Team", typeof(string)), ("first", "First name", typeof(string)), ("last", "Last name", typeof(string)),
                        ("salary", "Payroll ($)", typeof(long)), ("next", "Next year ($)", typeof(long)), ("todate", "Paid to date ($)", typeof(long)),
                        ("misc", "Misc cap ($)", typeof(long)), ("cups", "Cups", typeof(int)), ("_team", "", typeof(int)));
        if (gm == null) return t;
        foreach (int r in Rows(gm))
        {
            int team = Int(gm, r, "index", r);
            AddRow(t, TeamAbbr(team), Str(gm, r, "firstname"), Str(gm, r, "lastname"), (long)Int(gm, r, "salary"), (long)Int(gm, r, "salarynextyear"),
                   (long)Int(gm, r, "salarytodate"), (long)Int(gm, r, "misccapexpenses"), Int(gm, r, "stanleycups"), team);
        }
        return t;
    }

    private DataTable BuildPicks()
    {
        var db = _save.League;
        var picks = db?.Table("leaguedraftpick");
        var t = NewGrid(("year", "Year", typeof(int)), ("round", "Round", typeof(int)), ("pick", "Overall", typeof(int)), ("owner", "Owner", typeof(string)),
                        ("orig", "Original team", typeof(string)), ("player", "Used on", typeof(string)), ("key", "Key", typeof(int)), ("_team", "", typeof(int)));
        if (picks == null) return t;
        foreach (int r in Rows(picks))
        {
            int owner = Int(picks, r, "owner"), pi = Int(picks, r, "playerindex"), overall = Int(picks, r, "pickoverall");
            AddRow(t, Int(picks, r, "year") + 1900, Int(picks, r, "round"), overall > 0 ? overall : DBNull.Value, TeamAbbr(owner), TeamAbbr(Int(picks, r, "origteamid")),
                   pi >= 0 && pi < _save.Players.Count ? _save.Players[pi].Name : "", Int(picks, r, "key"), owner);
        }
        return t;
    }

    private DataTable BuildSchedule()
    {
        var db = _save.League;
        var games = db?.Table("leaguescheduletable");
        var t = NewGrid(("game", "Game", typeof(int)), ("date", "Date", typeof(string)), ("away", "Away", typeof(string)), ("home", "Home", typeof(string)),
                        ("score", "Score", typeof(string)), ("league", "League", typeof(int)), ("round", "Round", typeof(int)), ("status", "Status", typeof(int)),
                        ("_sort", "", typeof(int)), ("_team", "", typeof(int)), ("_team2", "", typeof(int)));
        if (games == null) return t;
        // a played game has a box score (leaguegamestats, by the schedule's index); the schedule's status stays 0
        var box = db.Table("leaguegamestats");
        var scores = new Dictionary<int, string>();
        if (box != null)
            foreach (int r in Rows(box))
            {
                string extra = Int(box, r, "shootout") == 1 ? " SO" : Int(box, r, "otgame") == 1 ? " OT" : "";
                scores[Int(box, r, "index", r)] = $"{Int(box, r, "scoreaway")}–{Int(box, r, "scorehome")}{extra}";
            }
        foreach (int r in Rows(games))
        {
            // month and day are both 0-based (9, 0 is October 1)
            int index = Int(games, r, "index", r), month = Int(games, r, "month"), day = Int(games, r, "day") + 1, home = Int(games, r, "home"), away = Int(games, r, "away");
            string date = month is >= 0 and < 12 ? $"{CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedMonthNames[month]} {day}" : $"{month + 1}/{day}";
            AddRow(t, index, date, TeamAbbr(away), TeamAbbr(home), scores.GetValueOrDefault(index, ""), Int(games, r, "league"), Int(games, r, "round"),
                   Int(games, r, "status"), month * 100 + day, home, away);
        }
        return t;
    }

    private DataTable BuildBlocks()
    {
        var t = NewGrid(("i", "#", typeof(int)), ("id", "Id", typeof(string)), ("name", "What", typeof(string)), ("kind", "Stored as", typeof(string)),
                        ("offset", "Offset", typeof(int)), ("stored", "Stored bytes", typeof(int)), ("size", "Bytes", typeof(int)), ("notes", "Notes", typeof(string)));
        foreach (var b in _save.Blocks)
            AddRow(t, b.Index, $"0x{b.Id:X8}", b.Name, b.Kind, b.Offset, b.StoredSize, b.Size, b.Notes);
        return t;
    }

    private void OnGridFilter(object sender, RoutedEventArgs e) => Debounce(ApplyGridFilter, sender is TextBox);

    private void ApplyGridFilter()
    {
        if (_gridRows == null) return;
        var parts = new List<string>();
        if (GridSearch.Text.Trim() is { Length: > 0 } q) parts.Add($"_search LIKE '%{Like(q.ToLowerInvariant())}%'");
        if (GridTeam.Visibility == Visibility.Visible && GridTeam.SelectedItem is FrTeamChoice { Team: not AllTeams } tc)
            parts.Add(_gridRows.Columns.Contains("_team2") ? $"(_team = {tc.Team} OR _team2 = {tc.Team})" : $"_team = {tc.Team}");
        _gridRows.DefaultView.RowFilter = string.Join(" AND ", parts);
        GridMeta.Text = Regex.Replace(GridMeta.Text, @"^[\d,]+( of [\d,]+)? rows", $"{_gridRows.DefaultView.Count:N0}{(parts.Count > 0 ? $" of {_gridRows.Rows.Count:N0}" : "")} rows");
    }

    // ================================================================ every league table

    private void OnTablePicked(object sender, SelectionChangedEventArgs e)
    {
        if (TableList.SelectedItem is not FrTableRow row) return;
        var db = _save.League;
        var t = row.Table;
        var table = new DataTable();
        table.Columns.Add(new DataColumn("row", typeof(int)) { Caption = "#" });
        foreach (var f in t.Fields)
            table.Columns.Add(new DataColumn("c_" + f.ShortName, f.Type == T3FieldType.Integer ? typeof(int) : typeof(string)) { Caption = f.Name });
        table.Columns.Add(new DataColumn("_search", typeof(string)));
        table.BeginLoadData();
        foreach (int r in Rows(t))
        {
            var values = new List<object> { r };
            var text = new StringBuilder();
            foreach (var f in t.Fields)
            {
                object v;
                try { v = db.Get(t, r, f); }
                catch { v = DBNull.Value; }
                values.Add(v);
                text.Append(v).Append(' ');
            }
            values.Add(text.ToString().ToLowerInvariant());
            table.Rows.Add(values.ToArray());
        }
        table.EndLoadData();
        _tableRows = table;
        TableTitle.Text = t.Name;
        TableMeta.Text = $"{row.Rows:N0} rows (room for {t.Records:N0})  ·  {t.Fields.Count} fields  ·  {t.RecordSize} bytes a row  ·  short name {t.ShortName}";
        BuildColumns(TableGrid, table);
        TableGrid.ItemsSource = table.DefaultView;
        TableSearch.Text = "";
    }

    private void OnTableFilter(object sender, TextChangedEventArgs e) => Debounce(() =>
    {
        if (_tableRows != null) _tableRows.DefaultView.RowFilter = TableSearch.Text.Trim() is { Length: > 0 } q ? $"_search LIKE '%{Like(q.ToLowerInvariant())}%'" : "";
    }, true);

    // ================================================================ helpers

    /// <summary>A grid's columns from a DataTable's (captions as headers); columns starting with _ are kept out of sight.</summary>
    private void BuildColumns(DataGrid grid, DataTable table)
    {
        grid.Columns.Clear();
        foreach (DataColumn c in table.Columns)
        {
            if (c.ColumnName.StartsWith("_")) continue;
            var binding = new Binding($"[{c.ColumnName}]");
            if (c.DataType == typeof(long) && c.Caption.Contains('$')) binding.StringFormat = "N0";
            var col = new DataGridTextColumn
            {
                Header = string.IsNullOrEmpty(c.Caption) ? c.ColumnName : c.Caption,
                Binding = binding,
                SortMemberPath = c.ColumnName,
            };
            if (c.DataType != typeof(string))
                col.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right) } };
            if (c.ColumnName is "name" or "player")
                col.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold) } };
            grid.Columns.Add(col);
        }
    }

    /// <summary>A grid table with these columns and a _search column filled in by <see cref="AddRow"/>.</summary>
    private static DataTable NewGrid(params (string Name, string Header, Type Type)[] cols)
    {
        var t = new DataTable();
        foreach (var (name, header, type) in cols) t.Columns.Add(new DataColumn(name, type) { Caption = header });
        t.Columns.Add(new DataColumn("_search", typeof(string)));
        return t;
    }

    private static void AddRow(DataTable t, params object[] values)
    {
        var all = values.Append(string.Join(" ", values.Take(t.Columns.Count - 1).Where(v => v is string)).ToLowerInvariant()).ToArray();
        t.Rows.Add(all);
    }

    private void Debounce(Action action, bool typing)
    {
        _pendingFilter = action;
        _filterTimer.Stop();
        if (typing) _filterTimer.Start();
        else action();
    }

    /// <summary>Text for a DataView LIKE filter: quotes doubled, wildcards bracketed.</summary>
    private static string Like(string s) => s.Replace("[", "[[]").Replace("]", "[]]").Replace("*", "[*]").Replace("%", "[%]").Replace("'", "''");

    private IEnumerable<int> Rows(T3Table t)
    {
        for (int r = 0; r < t.WrittenRecords; r++)
            if (!_save.League.IsInvalid(t, r)) yield return r;
    }

    private int Int(T3Table t, int r, string name, int fallback = 0, string shortName = null) =>
        (Roster.Field(t, name) ?? (shortName != null ? Roster.Field(t, shortName) : null)) is { } f && f.Type == T3FieldType.Integer ? (int)_save.League.Get(t, r, f) : fallback;

    private string Str(T3Table t, int r, string name, string shortName = null) =>
        (Roster.Field(t, name) ?? (shortName != null ? Roster.Field(t, shortName) : null)) is { } f && f.Type != T3FieldType.Integer ? _save.League.Get(t, r, f) as string ?? "" : "";

    /// <summary>"Upper body, Sep 30 – Oct 5 (3 days left)", "Groin, pending evaluation", or "None".</summary>
    private static string InjuryText(FranchisePlayer p)
    {
        if (FranchiseSave.Date(p.Value("injuryreturndate")) is not { } back) return "None";
        int type = p.Value("injurytype");
        string what = FranchiseSave.InjuryName(type);
        if (p.Value("injuryevaluation") > 0) return $"{what}, pending evaluation (back {back:MMM d})";
        return $"{what}, {FranchiseSave.Date(p.Value("injurydate")):MMM d} – {back:MMM d} ({p.Value("injurydaysleft")} days left)";
    }

    /// <summary>The contracts screen's EXT column.</summary>
    private static string ExtensionText(FranchisePlayer p)
    {
        int v = p.Value("extensioninterest");
        string ask = $"{p.Value("extensionaskyears")} yr at ${p.Value("extensionask"):N0}";
        return v switch { 0 => "–", 1 => $"No (asks {ask})", 3 or 6 => $"Yes (asks {ask})", _ => $"{v}? (asks {ask})" };
    }

    /// <summary>The Roster page's label for a field; c_fighting is how often he fights, not how well.</summary>
    private static string FieldLabel(string key) => key.Equals("c_fighting", StringComparison.OrdinalIgnoreCase) ? "Fighting frequency" : RosterVisuals.Label(key);

    private string TeamAbbr(int team) => team < 0 ? "" : _teams.TryGetValue(team, out var t) ? t.Abbr : team.ToString();
    private string TeamName(int team) => team < 0 ? "" : _teams.TryGetValue(team, out var t) ? t.Name : team.ToString();

    private DateTime SeasonDate()
    {
        int s = _save?.Slot.Season ?? 0;
        try { return s > 19000000 ? new DateTime(s / 10000, s / 100 % 100, s % 100) : DateTime.Today; }
        catch { return DateTime.Today; }
    }

    private static int Age(FranchisePlayer p, DateTime on)
    {
        int year = p.Value("year"), month = p.Value("month"), day = p.Value("day");
        int age = on.Year - year;
        if (on.Month < month || (on.Month == month && on.Day < day)) age--;
        return age;
    }

    private static Brush Banner(int rgb)
    {
        var c = RosterVisuals.TeamColor(rgb);
        var dark = Color.FromRgb((byte)(c.R * 0.45), (byte)(c.G * 0.45), (byte)(c.B * 0.45));
        return new LinearGradientBrush(c, dark, 20);
    }

    private static Border Chip(string text) => new()
    {
        Style = (Style)Application.Current.FindResource("Pill"),
        Margin = new Thickness(0, 0, 6, 6),
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = (Brush)Application.Current.FindResource("Accent") },
    };

    private static string Hex(byte[] b)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < b.Length; i += 16)
        {
            sb.Append($"{i:X3}  ");
            for (int j = i; j < i + 16; j++) sb.Append(j < b.Length ? $"{b[j]:X2} " : "   ");
            sb.Append(' ');
            for (int j = i; j < Math.Min(i + 16, b.Length); j++) sb.Append(b[j] is >= 32 and < 127 ? (char)b[j] : '·');
            if (i + 16 < b.Length) sb.AppendLine();
        }
        return sb.ToString();
    }
}
