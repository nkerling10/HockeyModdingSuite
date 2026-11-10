using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NHL27Kit.Core;

namespace NHL27Kit.App;

/// <summary>Where one step of a source stands: Off gray (not run, or the source is unticked), Waiting a hollow gold dot (it
/// runs once an earlier step is done), Running gold, Done green, Failed red.</summary>
public enum StageState { Off, Waiting, Running, Done, Failed }

/// <summary>One step of a source's card: what it does ("Rosters", "Contracts") and how far it is ("12/32 · MIN", "23 to change").</summary>
public sealed class SyncStage : Observable
{
    private StageState _state;
    private string _detail = "";

    public SyncStage(string label, string source = "")
    {
        Label = label;
        Source = source;
    }

    public string Label { get; }
    /// <summary>Where the step reads from ("NHL.com", "CapWages player pages"), or "" for a check made in HMS.</summary>
    public string Source { get; }
    public StageState State { get => _state; private set => Set(ref _state, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    /// <summary>How far a running step is (0–100), read from its "12/32" detail; -1 when it has no count.</summary>
    public double Percent { get => _percent; private set { if (Set(ref _percent, value)) Raise(nameof(HasPercent)); } }
    private double _percent = -1;
    public bool HasPercent => _percent >= 0;

    public void Update(StageState state, string detail = "")
    {
        State = state;
        Detail = detail;
        var m = System.Text.RegularExpressions.Regex.Match(detail ?? "", @"^(\d+)/(\d+)");
        Percent = state == StageState.Running && m.Success && int.Parse(m.Groups[2].Value) > 0
            ? 100.0 * int.Parse(m.Groups[1].Value) / int.Parse(m.Groups[2].Value) : -1;
    }
}

/// <summary>
/// One website the sync reads (NHL.com, TheAHL, CapWages, DailyFaceoff): its card says where the data comes from, what it
/// gives the game, and each step's progress. Optional sources have a tick box; an unticked source is not read.
/// </summary>
public sealed class SyncSource : Observable
{
    private bool _on = true;
    private bool _canToggle = true;

    public SyncSource(string name, string host, string provides, bool optional, string tip, params SyncStage[] steps)
    {
        Name = name;
        Host = host;
        Provides = provides;
        Optional = optional;
        Tip = tip;
        Steps = steps;
        foreach (var s in steps) s.PropertyChanged += (_, _) => { Raise(nameof(State)); Raise(nameof(StateText)); };
    }

    public string Name { get; }
    public string Host { get; }
    public string Provides { get; }
    public bool Optional { get; }
    public string Tip { get; }
    public SyncStage[] Steps { get; }

    /// <summary>Raised when the user ticks or unticks the source.</summary>
    public event Action Toggled;

    public bool IsOn
    {
        get => _on;
        set { if (Set(ref _on, value)) { Raise(nameof(State)); Raise(nameof(StateText)); Toggled?.Invoke(); } }
    }

    /// <summary>False while a fetch or apply is running: the box can't change under it.</summary>
    public bool CanToggle { get => _canToggle; set => Set(ref _canToggle, value); }

    /// <summary>The card as a whole: red when a step failed, gold while any step is still to come, green when all are done.</summary>
    public StageState State =>
        !_on ? StageState.Off :
        Steps.Any(s => s.State == StageState.Failed) ? StageState.Failed :
        Steps.Any(s => s.State is StageState.Running or StageState.Waiting) ? StageState.Running :
        Steps.All(s => s.State == StageState.Done) ? StageState.Done : StageState.Off;

    public string StateText => State switch
    {
        StageState.Running => "WORKING",
        StageState.Done => "DONE",
        StageState.Failed => Steps.All(s => s.State == StageState.Failed) ? "FAILED" : "PROBLEM",
        _ => _on ? "" : "OFF",
    };
}

/// <summary>One difference in the sync list, with its tick box.</summary>
public sealed class SyncRow : Observable
{
    private readonly Action _changed;
    private bool _selected;

    public SyncRow(SyncChange c, Action changed)
    {
        Change = c;
        _selected = c.Selected && (c.CanApply || c.CanCreate);
        _changed = changed;
    }

    /// <summary>A draft pick owner change from CapWages: same row, ticked and applied like a player move.</summary>
    public SyncRow(PickSyncChange p, Action changed)
    {
        Pick = p;
        _selected = p.Selected && p.CanApply;
        _changed = changed;
    }

    /// <summary>A contract change (or a team's retained-salary totals) from CapWages.</summary>
    public SyncRow(ContractChange c, Action changed)
    {
        Contract = c;
        _selected = c.Selected && c.CanApply;
        _changed = changed;
    }

    /// <summary>A team's lines from DailyFaceoff.</summary>
    public SyncRow(LineChange l, Action changed)
    {
        Line = l;
        _selected = l.Selected && l.Ready;
        _changed = changed;
    }

    /// <summary>A player's positions from CapWages.</summary>
    public SyncRow(PositionChange p, Action changed)
    {
        Pos = p;
        _selected = p.Selected && p.CanApply;
        _changed = changed;
    }

    /// <summary>A player with no play-by-play name call whose last name the game has a recording for.</summary>
    public SyncRow(NameCallChange n, Action changed)
    {
        Call = n;
        _selected = n.Selected && n.CanApply;
        _changed = changed;
    }

    public SyncChange Change { get; }
    public PickSyncChange Pick { get; }
    public ContractChange Contract { get; }
    public LineChange Line { get; }
    public PositionChange Pos { get; }
    public NameCallChange Call { get; }
    public bool IsPick => Pick != null;
    public bool IsPosition => Pos != null;
    public bool IsNameCall => Call != null;
    public bool IsContract => Contract != null;
    public bool IsLines => Line != null;
    /// <summary>A roster row (trade, call-up, info, left, not in game), as opposed to a pick, contract, position, name call or lines row.</summary>
    public bool IsPlayer => Change != null;

    public string Name => IsPosition ? Pos.Name : IsNameCall ? Call.Name : IsLines ? $"{Line.TeamName} lines" : IsPick ? $"{Pick.Year + 1900} {PickSync.Ordinal(Pick.Round)} round pick ({Pick.OriginalAbbr})" : IsContract ? Contract.Name : Change.Name;
    public string Meta => IsPosition ? $"{Roster.PositionDisplay(Pos.OldPrimary, Pos.OldSecondary)}  ·  {Pos.TeamAbbr}  ·  CapWages" :
        IsNameCall ? $"{Call.Position}  ·  {Call.TeamAbbr}  ·  game id {Call.GameId}" : IsLines ? $"{Line.TeamAbbr}  ·  lines  ·  DailyFaceoff" : IsPick ? "Draft pick  ·  CapWages" :
        IsContract ? (Contract.Kind switch
        {
            ContractChangeKind.TeamRetention => $"{Contract.TeamAbbr}  ·  team retained salary  ·  CapWages",
            ContractChangeKind.TeamBuyouts => $"{Contract.TeamAbbr}  ·  team buyouts  ·  CapWages",
            _ => $"{Contract.Position}  ·  {Contract.TeamAbbr}  ·  CapWages",
        }) :
        $"{Change.Position}" + (Change.Ahl is { } h ? $"  ·  AHL id {h.Id}" + (h.Born is { } b ? $"  ·  born {b:yyyy-MM-dd}" : "") : Change.Api is { } a ? $"  ·  NHL id {a.Id}" + (a.Born is { } b2 ? $"  ·  born {b2:yyyy-MM-dd}" : "") : $"  ·  game id {Change.GameId}");
    public string Description => IsPosition ? Pos.Description : IsNameCall ? Call.Description : IsLines ? Line.Description : IsPick ? Pick.Description : IsContract ? Contract.Description : Change.Description;
    public bool CanApply => IsPosition ? Pos.CanApply : IsNameCall ? Call.CanApply : IsLines ? Line.Ready : IsPick ? Pick.CanApply : IsContract ? Contract.CanApply : Change.Hold == null && (Change.CanApply || Change.CanCreate);
    public string Note => IsPosition ? (Pos.NameOnly ? "Matched by name only: the birth dates differ. Check it's the same player.  ·  " : "") + Pos.Source :
        IsNameCall ? "The game has a commentary recording of this name; he has none set." : IsLines ? Line.Source : IsPick ? "" :
        IsContract ? (Contract.NameOnly ? "Matched by name only: the birth dates differ. Check it's the same player." : Contract.Source) :
        Change.Hold != null ? Change.Hold :
        Change.Lineup != null ? Change.Lineup :
        Change.NameOnly ? "Matched by name only: the birth dates differ. Check it's the same player." :
        Change.Kind == SyncKind.Join && Change.NeedsNewRow ? "In the game as a free agent with no roster row — Sync will create one." :
        Change.Kind == SyncKind.Left && Change.Where != null ? Change.Where :
        Change.Kind == SyncKind.Left ? "Unticked: he may be injured, sent down or gone. Tick it to send him to the AHL affiliate." :
        Change.Kind == SyncKind.Missing ? MissingNote() : "";

    /// <summary>A Missing row's note: his bio and that Sync will create him, or why he can't be created.</summary>
    private string MissingNote()
    {
        if (Change.Bio == null) return "Not in the game's database. His bio is needed to create him.";
        string problem = PlayerCreator.Problem(Change);
        return Change.Bio.Text + (problem == null
            ? "  ·  Sync will create him, with ratings and gear copied from a similar player and a generic face."
            : $"  ·  Can't be created: {problem}.");
    }
    public string KindText => IsPosition ? "POSITION" : IsNameCall ? "NAME CALL" : IsLines ? "LINES" : IsPick ? "PICK" : IsContract ? Contract.Kind switch { ContractChangeKind.TeamRetention => "RETAINED", ContractChangeKind.TeamBuyouts => "BUYOUTS", _ => "CONTRACT" } : Change.Kind switch
    {
        SyncKind.Move => "TRADE", SyncKind.Join => "CALL-UP", SyncKind.Update => "INFO", SyncKind.Left => "LEFT NHL",
        _ => Change.CanCreate ? "CREATE" : "NOT IN GAME",
    };
    public Brush KindBrush => new SolidColorBrush(IsPosition ? Color.FromRgb(0xF0, 0xD0, 0x8A) : IsNameCall ? Color.FromRgb(0x9F, 0xB8, 0xE8) : IsLines ? (Line.Ready ? Color.FromRgb(0x9A, 0xD0, 0x5E) : Color.FromRgb(0xE0, 0x6C, 0x6C)) : IsPick ? Color.FromRgb(0xB7, 0x8C, 0xFF) :
        IsContract ? Contract.Kind switch
        {
            ContractChangeKind.TeamRetention => Color.FromRgb(0xE8, 0x9E, 0xC9),
            ContractChangeKind.TeamBuyouts => Color.FromRgb(0xD9, 0x8B, 0x6A),
            _ => Color.FromRgb(0x4F, 0xC9, 0xC4),
        } : Change.Kind switch
    {
        SyncKind.Move => Color.FromRgb(0xF2, 0xA6, 0x3B), SyncKind.Join => Color.FromRgb(0x5B, 0xC2, 0x8C), SyncKind.Update => Color.FromRgb(0x7C, 0xA7, 0xF5),
        SyncKind.Left => Color.FromRgb(0xB8, 0xBE, 0xC9),
        _ => Change.CanCreate ? Color.FromRgb(0xE6, 0xC1, 0x5A) : Color.FromRgb(0xE0, 0x6C, 0x6C),
    });
    /// <summary>Everything the search box matches on for a pick, contract or lines row.</summary>
    public string SearchText => IsPosition ? $"{Name} {Pos.TeamAbbr} {Description}" : IsNameCall ? $"{Name} {Call.TeamAbbr} {Description}" : IsContract ? $"{Name} {Contract.TeamAbbr} {Description}" : IsLines ? $"{Name} {Line.TeamAbbr} {Description}" : $"{Name} {Description}";

    /// <summary>Called when the Missing player's bio arrives: the row shows it, and a player who can be created is ticked.</summary>
    public void BioLoaded()
    {
        foreach (var p in new[] { nameof(Note), nameof(KindText), nameof(KindBrush), nameof(CanApply) }) Raise(p);
        if (Change.CanCreate && !_selected) { _selected = true; Raise(nameof(Selected)); _changed?.Invoke(); }
    }

    public bool Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value && CanApply)) _changed?.Invoke(); }
    }

    /// <summary>Puts back the tick a rebuilt row had, without telling the page: it isn't the user ticking it, so it must
    /// not start another check (which would rebuild the rows and put the tick back again, endlessly).</summary>
    /// <summary>
    /// A held roster change: another source has the player where he is, so nothing is done and the row isn't listed.
    /// </summary>
    public bool IsHeld => IsPlayer && Change.Hold != null;

    /// <summary>
    /// A change that needs the user: it's unticked until confirmed. Everything else (trades, call-ups, numbers, sizes,
    /// contracts and clauses, same-group positions, name calls, lines, picks, new players) is applied automatically.
    /// </summary>
    public bool NeedsReview =>
        IsPlayer ? !IsHeld && (Change.NameOnly || Change.Kind == SyncKind.Left && !Change.Settled ||Change.Lineup != null && !Selected || Change.Kind == SyncKind.Missing && !Change.CanCreate)
        : IsPosition ? Pos.NameOnly || Pos.CrossesGroup
        : IsContract ? Contract.NameOnly || !Contract.IsTeam && Contract.Old.Length > 0 && Contract.New.Length == 0
        : IsLines ? !Line.Ready
        : false;

    /// <summary>A Left row was placed from CapWages after it was built: its destination, note and box change.</summary>
    public void Replaced()
    {
        foreach (var p in new[] { nameof(Description), nameof(Note), nameof(CanApply), nameof(SearchText) }) Raise(p);
    }

    /// <summary>The row's note changed (a hold was set after it was built).</summary>
    public void NoteChanged()
    {
        Raise(nameof(Note));
        // a held change can't be ticked (CanApply false): its box goes grey and empty
        Raise(nameof(CanApply));
        if (_selected && !CanApply) { _selected = false; Raise(nameof(Selected)); }
    }

    public void Restore(bool selected)
    {
        if (_selected != (selected && CanApply)) { _selected = selected && CanApply; Raise(nameof(Selected)); }
    }
}

/// <summary>
/// The Roster page's NHL sync. Four sources are read at the same time: NHL.com (every NHL roster, and the bios of
/// players not in the game), TheAHL (every AHL roster and bios), CapWages (contracts, retained salary, buyouts and draft pick
/// owners) and DailyFaceoff (line combinations). The rosters are compared with the game first (RosterSync); then the
/// contracts, picks and lines are compared against a copy of the game's database with the ticked roster changes made
/// and the missing players created (SyncPlan), so they show what Apply will really do. Ticking a roster row checks them
/// again. Applying reads the database from the game again and matches everything afresh, so nothing stale is written.
/// </summary>
public partial class RosterSyncView : UserControl
{
    private ModService _mods;
    private List<NhlPlayer> _api;
    private string _nhlError;
    private DateTime _fetchedAt;
    /// <summary>Every AHL team's roster as fetched (team + players), or null when the AHL source is unticked or the fetch failed.</summary>
    private List<(AhlTeam Team, List<AhlPlayer> Players)> _ahl;
    /// <summary>Feed teams with no matching team in the game, from the last AHL compare (skipped, not failed).</summary>
    private List<string> _ahlSkipped = new();
    /// <summary>The last AHL error (fetch or compare), shown in the status line; null when the AHL part is fine.</summary>
    private string _ahlError;
    private int _bioFailed;
    private List<SyncRow> _rows = new();
    /// <summary>Every NHL team's CapWages page, parsed (contracts and picks), or null when the source is unticked or the fetch failed.</summary>
    private List<CapTeam> _cap;
    /// <summary>Why the CapWages fetch failed; null when it's fine. Picks and contracts are then not compared at all.</summary>
    private string _capError;
    private List<OwnedPick> _picks;
    private PickCompareResult _pickResult;
    private List<SyncRow> _pickRows = new();
    private List<SyncRow> _contractRows = new();
    private List<SyncRow> _positionRows = new();
    /// <summary>Players with no name call whose last name the game has a recording of (from the game's own name list).</summary>
    private List<SyncRow> _nameRows = new();
    /// <summary>Every NHL team's DailyFaceoff lines as fetched (a team that failed has its error), or null when the source is unticked.</summary>
    private List<(string Abbr, LineTeam Team, string Error)> _lines;
    private List<SyncRow> _lineRows = new();
    /// <summary>Call-ups from DailyFaceoff lineups that the NHL's roster lists missed (LineupCallUps), rebuilt by each check.</summary>
    private List<SyncRow> _lineupRows = new();
    /// <summary>" Draft picks from CapWages: 1120 picks (2027–2031); game first draft year 2026, offset 0." — "" when no pick compare has run.</summary>
    private string _pickSource = "";
    private bool _busy;
    /// <summary>A fetch has finished, so ticking a source reads or drops just that source.</summary>
    private bool _fetched;
    /// <summary>The contracts, picks and lines are being checked against the planned rosters; Apply waits for it.</summary>
    private bool _planning;
    /// <summary>Counts the checks: a check that a newer one has replaced throws its result away.</summary>
    private int _planRun;
    /// <summary>Checks the contracts and lines again shortly after the roster rows' ticks change.</summary>
    private readonly DispatcherTimer _replan = new() { Interval = TimeSpan.FromMilliseconds(700) };

    public event Action<string> StatusChanged;
    /// <summary>Raised after changes were written (the roster list reloads).</summary>
    public event Action Applied;

    /// <summary>Missing players' bios from this fetch, by source and id ("NHL:8485414", "AHL:11419"). Every compare
    /// builds new rows, so they take their bios from here instead of losing them; a new Fetch starts it empty.</summary>
    private readonly Dictionary<string, PlayerBio> _bios = new();

    private static string BioKey(SyncChange c) => SyncPlan.BioKey(c);

    // what is read, in the order it's read (the first four at the same time)
    private readonly SyncStage _nhlRosters = new("NHL rosters", "NHL.com");
    private readonly SyncStage _ahlRosters = new("AHL rosters", "TheAHL.com");
    private readonly SyncStage _capPages = new("NHL team contracts", "CapWages team pages");
    private readonly SyncStage _linePages = new("Lines", "DailyFaceoff");
    private readonly SyncStage _ahlSizes = new("AHL players' heights and weights", "CapWages player pages");
    private readonly SyncStage _nhlBios = new("New NHL players' bios", "NHL.com");
    private readonly SyncStage _ahlBios = new("New AHL players' bios", "CapWages player pages");
    private readonly SyncStage _leftPages = new("Players no roster lists", "CapWages player pages");
    // what is checked once it's read
    private readonly SyncStage _compareStage = new("Rosters compared");
    private readonly SyncStage _placeStage = new("Players no roster lists, placed");
    private readonly SyncStage _contractsStage = new("Contracts");
    private readonly SyncStage _positionsStage = new("Positions");
    private readonly SyncStage _picksStage = new("Draft picks");
    private readonly SyncStage _linesStage = new("Lines");

    // A sync reads everything: NHL and AHL rosters, CapWages and DailyFaceoff, heights and weights included.
    private const bool AhlOn = true, CapOn = true, LinesOn = true;

    /// <summary>Cancels the running fetch (Cancel button). Writing to the game is never cancelled half way.</summary>
    private CancellationTokenSource _cts;
    private CancellationToken Ct => _cts?.Token ?? CancellationToken.None;

    public RosterSyncView()
    {
        InitializeComponent();
        ReadList.ItemsSource = new[] { _nhlRosters, _ahlRosters, _capPages, _linePages, _ahlSizes, _nhlBios, _ahlBios, _leftPages };
        CheckList.ItemsSource = new[] { _compareStage, _placeStage, _contractsStage, _positionsStage, _picksStage, _linesStage };
        // the page's extent is only known after layout: re-fit when it or the window changes
        PageScroll.ScrollChanged += (_, e) => { if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) FitChanges(); };
        TopPanel.SizeChanged += (_, _) => FitChanges();
        _replan.Tick += async (_, _) =>
        {
            _replan.Stop();
            var scope = _pending;
            _pending = null;
            if (_busy || !_fetched || scope == null) return;
            await PlanAsync(scope);
            ShowSummary();
        };
    }

    public void SetMods(ModService mods)
    {
        _mods = mods;
        ShowBaseline();
    }

    /// <summary>Starts or ends a fetch/apply: the buttons are locked while it runs.</summary>
    private void SetBusy(bool busy)
    {
        _busy = busy;
        FetchButton.IsEnabled = !busy;
        ShowBaseline();
        SetButtons();
    }

    /// <summary>
    /// The changes list gets the height the window has left under steps 1 and 2, but never less than MinListHeight: on a short
    /// window (a remote desktop) the page scrolls instead of squeezing the list to nothing.
    /// </summary>
    private const double MinListHeight = 300;

    private void FitChanges()
    {
        // everything on the page but the list (cards, margins) is the extent less the list's own height
        if (ChangesCard.ActualHeight <= 0 || PageScroll.ViewportHeight <= 0) return;
        double others = Math.Max(TopPanel.ActualHeight, PageScroll.ExtentHeight - ChangesCard.ActualHeight);
        double height = Math.Max(MinListHeight, Math.Floor(PageScroll.ViewportHeight - others));
        if (Math.Abs(ChangesCard.Height - height) >= 1) ChangesCard.Height = height;
    }

    private void OnShowProgress(object sender, RoutedEventArgs e) => ShowProgress(ProgressPanel.Visibility != Visibility.Visible);

    /// <summary>Opens or folds the sources and progress list (open while a fetch runs, folded once its list is ready).</summary>
    private void ShowProgress(bool show)
    {
        ProgressPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ProgressToggleText.Text = show ? "Hide progress" : "Show sources & progress";
        ProgressToggleIcon.Text = show ? "" : "";
    }

    /// <summary>Opens the progress list and/or step 1's details (scripted screenshots).</summary>
    public void OpenPanels(bool progress, bool details)
    {
        if (progress) ShowProgress(true);
        if (details) ShowBaselineMore(true);
    }

    private void OnBaselineMore(object sender, RoutedEventArgs e) => ShowBaselineMore(BaselineMore.Visibility != Visibility.Visible);

    /// <summary>Step 1's details (the pack's folder, Preview, and what the last preview or import did): folded by default.</summary>
    private void ShowBaselineMore(bool show)
    {
        BaselineMore.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BaselineMoreText.Text = show ? "Less" : "Details";
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_cts == null || _cts.IsCancellationRequested) return;
        _cts.Cancel();
        CancelButton.IsEnabled = false;
        FetchStatus.Text = "Cancelling…";
    }

    // ---------------------------------------------------------------- the sources' cards

    /// <summary>The AHL card after a fetch or compare: off when unticked, red when the fetch or compare failed, else the counts.</summary>
    private void ShowAhlStage()
    {
        if (!AhlOn) { _ahlRosters.Update(StageState.Off, "off"); _ahlBios.Update(StageState.Off, "off"); }
        else if (_ahlError != null) _ahlRosters.Update(StageState.Failed, _ahlError);
        else if (_ahl != null) _ahlRosters.Update(StageState.Done, $"{_ahl.Sum(t => t.Players.Count)} players");
    }

    /// <summary>The CapWages card's contract and pick steps after a check: off when unticked, red when the pages couldn't be read, else how many differ.</summary>
    private void ShowCapStages()
    {
        if (!CapOn)
        {
            foreach (var s in new[] { _capPages, _contractsStage, _positionsStage, _picksStage }) s.Update(StageState.Off, "off");
            return;
        }
        if (_capError != null)
        {
            foreach (var s in new[] { _contractsStage, _positionsStage, _picksStage }) s.Update(StageState.Failed, "not checked");
            return;
        }
        if (_cap == null) return;
        _contractsStage.Update(StageState.Done, $"{_contractRows.Count} to change");
        _positionsStage.Update(StageState.Done, $"{_positionRows.Count} to change");
        _picksStage.Update(StageState.Done, $"{_pickRows.Count} to change");
    }

    /// <summary>The DailyFaceoff card's lines step after a check: off when unticked, red when no page could be read, else how many teams change and how many are left alone.</summary>
    private void ShowLinesStage()
    {
        if (!LinesOn) { _linePages.Update(StageState.Off, "off"); _linesStage.Update(StageState.Off, "off"); return; }
        if (_lines == null) return;
        if (_lines.All(t => t.Error != null)) { _linesStage.Update(StageState.Failed, "no team pages read"); return; }
        int ready = _lineRows.Count(r => r.Line.Ready), left = _lineRows.Count(r => !r.Line.Ready);
        _linesStage.Update(StageState.Done, $"{ready} teams to change" + (left > 0 ? $", {left} left alone" : ""));
    }

    /// <summary>The steps that run after the rosters, shown as waiting for them.</summary>
    private void ShowPlanWaiting()
    {
        if (CapOn) foreach (var s in new[] { _contractsStage, _positionsStage, _picksStage }) s.Update(StageState.Waiting, "after new players");
        if (LinesOn) _linesStage.Update(StageState.Waiting, "after new players");
    }

    // ---------------------------------------------------------------- fetching

    private async void OnFetch(object sender, RoutedEventArgs e) => await FetchAsync();

    /// <summary>
    /// Reads every source, then checks in order (the order the progress list shows): NHL.com, TheAHL.com, CapWages' team
    /// pages and DailyFaceoff at the same time; the AHL players' CapWages details; the NHL and AHL rosters compared
    /// together (one shared matched set); the missing players' bios; the players no roster lists placed from CapWages;
    /// and last the contracts, positions, picks and lines, against the rosters as Apply will leave them (PlanAsync).
    /// Cancel stops it at the next step or download and drops what it had found, so nothing half-read can be applied.
    /// </summary>
    public async Task FetchAsync()
    {
        if (_mods == null || _busy) return;
        // step 1 first: recommend a base roster once, until one is in the game
        if (!AskForBaseline()) return;
        // A check still running from before throws its result away.
        _replan.Stop();
        _planRun++;
        _planning = false;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);
        CancelButton.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
        ShowProgress(true);
        try
        {
            FetchStatus.Text = "Reading the sources…";
            foreach (var s in new[] { _nhlRosters, _ahlRosters, _capPages, _linePages }) s.Update(StageState.Running, "starting…");
            _ahlSizes.Update(StageState.Waiting, "after AHL rosters and CapWages");
            _nhlBios.Update(StageState.Waiting, "after the compare");
            _ahlBios.Update(StageState.Waiting, "after the compare");
            _leftPages.Update(StageState.Waiting, "after the compare");
            _compareStage.Update(StageState.Waiting, "after the rosters");
            _placeStage.Update(StageState.Waiting, "after the compare");
            ShowPlanWaiting();

            // Earlier rows go away: everything below is compared from this fetch.
            _rows = new List<SyncRow>();
            _nameRows = new List<SyncRow>();
            ClearCap();
            _lines = null;
            _lineRows = new List<SyncRow>();
            _lineupRows = new List<SyncRow>();
            ApplyFilter();

            int done = 0;
            var progress = new Progress<string>(t => _nhlRosters.Update(StageState.Running, $"{++done}/{NhlApi.Teams.Length} · {t}"));
            var sink = (IProgress<string>)progress;
            Task<List<NhlPlayer>> nhlTask = Task.Run(() => NhlApi.AllRostersAsync(t => sink.Report(t), ct));

            int ahlDone = 0;
            var ahlProgress = new Progress<string>(t => _ahlRosters.Update(StageState.Running, $"{++ahlDone}/32 · {t}"));
            var ahlSink = (IProgress<string>)ahlProgress;
            var ahlTask = Task.Run(() => AhlApi.AllRostersAsync(t => ahlSink.Report(t), ct));

            // CapWages and DailyFaceoff: read now (they never throw), used once the rosters are in
            Task capTask = FetchCapPagesAsync();
            Task linesTask = FetchLinePagesAsync();

            // A failed NHL fetch means no player list, but the other sources still finish on their own.
            _nhlError = null;
            try { _api = await nhlTask; _nhlRosters.Update(StageState.Done, $"{_api.Count} players"); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _api = null; _nhlError = ex.Message; _nhlRosters.Update(StageState.Failed, ex.Message); }
            ct.ThrowIfCancellationRequested();
            _fetchedAt = DateTime.Now;

            _ahl = null;
            _ahlError = null;
            _ahlSkipped = new List<string>();
            try { _ahl = await ahlTask; _ahlRosters.Update(StageState.Done, $"{_ahl.Sum(t => t.Players.Count)} players on {_ahl.Count} teams"); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _ahl = null; _ahlError = ex.Message; _ahlRosters.Update(StageState.Failed, ex.Message); }
            ct.ThrowIfCancellationRequested();
            // TheAHL.com says who is on each AHL roster; who the player is (birth date, size, bio) comes from CapWages,
            // so it's read before the compare
            _ahlFeed = _ahl;
            _ahlSized = false;
            await capTask;
            ct.ThrowIfCancellationRequested();
            if (_ahl != null) await UseCapWagesForAhlAsync();
            else _ahlSizes.Update(StageState.Off, "no AHL rosters");
            ct.ThrowIfCancellationRequested();

            // The combined player compare, then the bios of the players it finds missing.
            _bios.Clear();
            if (_api != null) await CompareAsync();
            ct.ThrowIfCancellationRequested();
            _bioFailed = 0;
            if (_api != null) _bioFailed = await LookUpBiosAsync();
            else
            {
                _compareStage.Update(StageState.Failed, "no NHL rosters");
                _nhlBios.Update(StageState.Off, "skipped");
                _ahlBios.Update(StageState.Off, "skipped");
            }
            ct.ThrowIfCancellationRequested();

            await linesTask;
            ct.ThrowIfCancellationRequested();
            // the roster lists against CapWages and DailyFaceoff: don't send down a player they have on his NHL team
            HoldNhlPlayers();
            // and the rest of those no roster lists placed from CapWages: minors, junior club, released
            _leftStats.Clear();
            await PlaceLeftAsync();
            ct.ThrowIfCancellationRequested();

            // Last: contracts, picks and lines against the rosters as Apply will leave them.
            await PlanAsync();
            ct.ThrowIfCancellationRequested();
            _fetched = true;
            // the list again, now that the fetch is over (its empty-list text said it was reading)
            ApplyFilter();
            ShowSummary();
            FilterBar.IsEnabled = true;
            // the list is what matters now: the progress folds away (its button opens it again)
            ShowProgress(false);
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            // nothing half-read stays: the list is emptied, and the next Fetch starts over
            _planRun++;
            _planning = false;
            _fetched = false;
            _api = null;
            _rows = new List<SyncRow>();
            _nameRows = new List<SyncRow>();
            ClearCap();
            _lines = null;
            _lineRows = new List<SyncRow>();
            _lineupRows = new List<SyncRow>();
            ApplyFilter();
            foreach (var s in AllStages) if (s.State is StageState.Running or StageState.Waiting) s.Update(StageState.Off, "cancelled");
            FetchStatus.Text = "Cancelled. Nothing was changed in the game.";
            _ = ex;
        }
        catch (Exception ex)
        {
            FetchStatus.Text = "Couldn't fetch: " + ex.Message;
            foreach (var s in AllStages)
                if (s.State is StageState.Running or StageState.Waiting) s.Update(StageState.Failed, ex.Message);
        }
        finally
        {
            CancelButton.Visibility = Visibility.Collapsed;
            SetBusy(false);
            FetchLabel.Text = _api == null ? "Fetch & compare" : "Fetch again";
        }
    }

    private SyncStage[] AllStages => new[] { _nhlRosters, _ahlRosters, _capPages, _linePages, _ahlSizes, _nhlBios, _ahlBios, _leftPages,
                                             _compareStage, _placeStage, _contractsStage, _positionsStage, _picksStage, _linesStage };

    /// <summary>Reads every NHL team's CapWages page. All or nothing (half a league is never compared); never throws.</summary>
    private async Task FetchCapPagesAsync()
    {
        _cap = null;
        _capError = null;
        _capPages.Update(StageState.Running, "starting…");
        try
        {
            int done = 0;
            var progress = new Progress<string>(t => _capPages.Update(StageState.Running, $"{++done}/{CapWagesApi.Teams.Length} · {t}"));
            var sink = (IProgress<string>)progress;
            var ct = Ct;
            _cap = await Task.Run(() => CapWagesApi.AllTeamsAsync(t => sink.Report(t), ct));
            _capPages.Update(StageState.Done, $"{_cap.Count} teams read");
            await SaveRetainedNotesAsync();
        }
        catch (Exception ex)
        {
            _cap = null;
            _capError = ex.Message;
            _capPages.Update(StageState.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Saves who retains whom from CapWages (RetainedSalary notes, in the game's mod folder) for the player profile's
    /// retained salary section: the game only says how many teams retain part of a player's salary. The profile checks
    /// the notes against the game's own retained totals before showing them. A failure only costs the profile its detail.
    /// </summary>
    private async Task SaveRetainedNotesAsync(T3Db db = null)
    {
        try
        {
            var cap = _cap;
            if (cap == null) return;
            db ??= await _mods.OpenDbAsync(Roster.Database);
            var notes = await Task.Run(() => RetainedSalary.FromCapWages(db, cap));
            RetainedSalary.SaveNotes(_mods.Store.Dir, notes);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    /// <summary>Reads every NHL team's DailyFaceoff page. A team whose page fails keeps its error and the others go on; never throws.</summary>
    private async Task FetchLinePagesAsync()
    {
        _lines = null;
        _linePages.Update(StageState.Running, "starting…");
        try
        {
            int done = 0;
            var progress = new Progress<string>(t => _linePages.Update(StageState.Running, $"{++done}/32 · {t}"));
            var sink = (IProgress<string>)progress;
            var ct = Ct;
            _lines = await Task.Run(() => DailyFaceoffApi.AllTeamsAsync(t => sink.Report(t), ct));
            int failed = _lines.Count(t => t.Error != null);
            if (failed == _lines.Count) _linePages.Update(StageState.Failed, _lines.FirstOrDefault().Error ?? "no teams");
            else _linePages.Update(StageState.Done, $"{_lines.Count - failed} teams read" + (failed > 0 ? $", {failed} failed" : ""));
        }
        catch (Exception ex)
        {
            _lines = null;
            _linePages.Update(StageState.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Looks up the full bio of each player who is not in the game: NHL roster players from NHL.com's player page, AHL
    /// roster players from the AHL feed's player view. The two run side by side, one player at a time each with a short
    /// pause, each on its own card. A failed lookup leaves that row without a bio; players whose bio is already known
    /// (from _bios) are not looked up again. Returns the number of failures.
    /// </summary>
    private async Task<int> LookUpBiosAsync()
    {
        var missing = _rows.Where(r => r.Change.Kind == SyncKind.Missing).ToList();
        int failed = 0;

        async Task Run(List<SyncRow> all, SyncStage stage)
        {
            var rows = all.Where(r => r.Change.Bio == null).ToList();
            int done = 0, bad = 0;
            if (rows.Count > 0) stage.Update(StageState.Running, $"0/{rows.Count}");
            foreach (var r in rows)
            {
                Ct.ThrowIfCancellationRequested();
                try
                {
                    r.Change.Bio = await MissingPlayers.BioAsync(r.Change, Ct);
                    if (r.Change.Bio != null && BioKey(r.Change) is { } key) _bios[key] = r.Change.Bio;
                    r.BioLoaded();
                }
                catch (Exception) when (!Ct.IsCancellationRequested) { bad++; }
                stage.Update(StageState.Running, $"{++done}/{rows.Count}");
                await Task.Delay(150, Ct);
            }
            failed += bad;
            int with = all.Count(r => r.Change.Bio != null);
            if (bad > 0) stage.Update(StageState.Failed, $"{bad} of {all.Count} failed");
            else stage.Update(StageState.Done, all.Count == 0 ? "none needed" : $"{with} players");
        }

        var tasks = new List<Task> { Run(missing.Where(r => r.Change.Api != null).ToList(), _nhlBios) };
        if (AhlOn) tasks.Add(Run(missing.Where(r => r.Change.Ahl is { Id: > 0 }).ToList(), _ahlBios));
        await Task.WhenAll(tasks);
        return failed;
    }

    // ---------------------------------------------------------------- comparing

    /// <summary>Compares the fetched rosters with a database read from the game now.</summary>
    private async Task CompareAsync()
    {
        var db = await _mods.OpenDbAsync(Roster.Database);
        const bool sizes = true; // a sync always includes heights and weights
        // a re-compare (after Apply) whose AHL players' CapWages sizes weren't read: read them now
        if (_fetched && _ahlFeed != null && _cap != null && !_ahlSized)
            await UseCapWagesForAhlAsync();
        _compareStage.Update(StageState.Running, "comparing…");
        var api = _api;
        var ahl = _ahl;
        // players left on AHL teams no roster list has: only with CapWages, which is what places them
        bool stragglers = CapOn && _cap != null;
        // One combined compare (AHL first, NHL second, one shared matched set — see CompareCombined). An AHL compare
        // failure must not wipe the NHL rows: fall back to the NHL compare alone and remember the error.
        List<SyncChange> changes;
        if (ahl != null)
        {
            try { var (combined, skipped) = await Task.Run(() => RosterSync.CompareCombined(db, api, ahl, sizes, stragglers)); changes = combined; _ahlSkipped = skipped; _ahlError = null; }
            catch (Exception ex) { _ahlError = ex.Message; changes = await Task.Run(() => RosterSync.Compare(db, api, sizes)); }
        }
        else
        {
            changes = await Task.Run(() => RosterSync.Compare(db, api, sizes));
        }
        foreach (var c in changes)
            if (c.Kind == SyncKind.Missing && BioKey(c) is { } key && _bios.TryGetValue(key, out var bio)) { c.Bio = bio; c.Selected = c.CanCreate; }
        _rows = changes.Select(PlayerRow).ToList();
        int Count(SyncKind k) => changes.Count(c => c.Kind == k);
        _compareStage.Update(_ahlError != null ? StageState.Failed : StageState.Done,
            (_ahlError != null ? "AHL compare failed: " + _ahlError + " · " : "") +
            $"{Count(SyncKind.Move)} trades, {Count(SyncKind.Join)} call-ups, {Count(SyncKind.Update)} info, {Count(SyncKind.Left)} off a roster, {Count(SyncKind.Missing)} not in game");
        // a re-compare after the fetch (after Apply): CapWages and DailyFaceoff are already read
        _abbrOf = RosterSync.TeamIndex(db).GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
        if (_fetched) { HoldNhlPlayers(); await PlaceLeftAsync(db); }
        ApplyFilter();
    }

    /// <summary>The AHL rosters as TheAHL.com gave them; <see cref="_ahl"/> is these with CapWages' details when CapWages is read.</summary>
    private List<(AhlTeam Team, List<AhlPlayer> Players)> _ahlFeed;
    /// <summary>Whether <see cref="_ahl"/> has CapWages' heights and weights (read only when sizes are compared).</summary>
    private bool _ahlSized;

    /// <summary>
    /// The AHL rosters with CapWages' birth dates (and, when sizes are compared, heights and weights from the players'
    /// CapWages pages, kept a day on this machine). TheAHL.com's feed is left with only who is where and the numbers.
    /// </summary>
    private async Task UseCapWagesForAhlAsync()
    {
        if (_ahlFeed == null) return;
        var cap = CapOn ? _cap : null;
        if (cap == null) { _ahl = _ahlFeed; _ahlSized = false; _ahlSizes.Update(StageState.Failed, "CapWages not read: TheAHL.com's details used"); return; }
        _ahl = AhlCapWages.Enrich(_ahlFeed, cap);
        int onCap = _ahl.Sum(r => r.Players.Count(p => p.CapSlug.Length > 0)), all = _ahl.Sum(r => r.Players.Count);
        var progress = new Progress<(int Done, int Total)>(x => _ahlSizes.Update(StageState.Running, $"{x.Done}/{x.Total} pages"));
        var sink = (IProgress<(int, int)>)progress;
        var enriched = _ahl;
        var ct = Ct;
        _ahlSizes.Update(StageState.Running, $"0/{onCap} pages");
        try
        {
            _ahl = await Task.Run(() => AhlCapWages.SizesAsync(enriched, (d, t) => sink.Report((d, t)), ct));
            _ahlSized = true;
            _ahlSizes.Update(StageState.Done, $"{onCap} of {all} on CapWages");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { _ahlSizes.Update(StageState.Failed, ex.Message); }
    }

    /// <summary>CapWages player pages read for "left the NHL" players (slug → stats), kept until the next fetch.</summary>
    private readonly Dictionary<string, CapPage> _leftStats = new();
    /// <summary>Where each "left the NHL" player goes (LeftNhl.Plan), by game id; empty without CapWages.</summary>
    private Dictionary<int, LeftPlan> _leftPlans = new();

    /// <summary>
    /// Places the "left the NHL" rows that aren't held from CapWages (LeftNhl): a player in the minors goes to his team's
    /// affiliate, a junior or European player to his latest club (read from his player page, once per fetch), a player no
    /// one has under contract is released. A placed row is ticked and applied without review; the rest stay for review.
    /// </summary>
    private async Task PlaceLeftAsync(T3Db db = null)
    {
        _leftPlans = new Dictionary<int, LeftPlan>();
        var cap = CapOn ? _cap : null;
        var left = _rows.Where(r => r.Change.Kind == SyncKind.Left && r.Change.Hold == null).ToList();
        if (cap == null) { _leftPages.Update(StageState.Failed, "CapWages not read"); _placeStage.Update(StageState.Failed, "CapWages not read"); return; }
        if (left.Count == 0 || _mods == null) { _leftPages.Update(StageState.Done, "none needed"); _placeStage.Update(StageState.Done, "none"); return; }
        var ct = Ct;
        try
        {
            db ??= await _mods.OpenDbAsync(Roster.Database);
            var changes = left.Select(r => r.Change).ToList();
            // every row: an AHL player the game lacks may be the straggler under another spelling (LeftNhl's namesake check)
            var everything = _rows.Select(r => r.Change).ToList();
            var needed = LeftNhl.PagesNeeded(db, changes, cap).Where(s => !_leftStats.ContainsKey(s)).ToList();
            int done = 0;
            _leftPages.Update(StageState.Running, $"0/{needed.Count} pages");
            foreach (var slug in needed)
            {
                ct.ThrowIfCancellationRequested();
                bool cached = CapWagesApi.IsCached(slug);
                // null: CapWages has no such page (kept, so it isn't asked again); an error leaves it unread
                try { _leftStats[slug] = await CapWagesApi.PageAsync(slug, ct); }
                catch (Exception) when (!ct.IsCancellationRequested) { }
                _leftPages.Update(StageState.Running, $"{++done}/{needed.Count} pages");
                if (!cached) await Task.Delay(300, ct);
            }
            _leftPages.Update(StageState.Done, $"{_leftStats.Count(kv => kv.Value != null)} pages read for {left.Count} players");
            _placeStage.Update(StageState.Running, "placing…");
            _leftPlans = await Task.Run(() => LeftNhl.Plan(db, everything, cap, _leftStats));
            LeftNhl.Use(changes, _leftPlans);
            foreach (var r in left) { r.Restore(r.Change.Selected); r.Replaced(); r.NoteChanged(); }
            int held = left.Count(r => r.Change.Hold != null), released = left.Count(r => r.Change.Release),
                moved = left.Count(r => r.Change.Hold == null && !r.Change.Release && r.Change.Settled), review = left.Count - held - released - moved;
            _placeStage.Update(StageState.Done, $"{moved} moved, {released} released, {held} kept, {review} to review");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _leftPlans = new Dictionary<int, LeftPlan>();
            _placeStage.Update(StageState.Failed, ex.Message);
        }
        SetButtons();
    }

    /// <summary>
    /// Holds (unticks, with the reason in the note) every "left the NHL" row and every AHL move out of an NHL team whose
    /// player CapWages has on that team's NHL roster or injured list, or who is in its DailyFaceoff lineup. NHL.com's team
    /// rosters leave out some injured and recently recalled players, and TheAHL.com keeps listing some recalled ones, so
    /// following them would send a player down for the next sync to call him back up (Trey Taylor, Philip Tomasino).
    /// </summary>
    private void HoldNhlPlayers()
    {
        var cap = CapOn ? _cap : null;
        var lines = LinesOn ? _lines : null;
        if ((cap == null && lines == null) || _abbrOf.Count == 0) return;
        string Key(string name) => RosterSync.Norm(name);
        var onCap = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in cap ?? new List<CapTeam>())
            onCap[t.Abbr] = t.Players.Where(p => p.Section is "roster" or "inactive")
                .GroupBy(p => Key(p.First + p.Last)).ToDictionary(g => g.Key, g => g.First().Section);
        var inLineup = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (abbr, team, error) in lines ?? new List<(string, LineTeam, string)>())
            if (team != null && error == null)
                inLineup[abbr] = team.Spots.Where(s => s.Category is "ev" or "pp" or "pk").Select(s => Key(s.Name)).ToHashSet();
        foreach (var r in _rows)
        {
            var c = r.Change;
            bool outOfNhl = c.Kind == SyncKind.Left || c.IsAhl && c.Kind == SyncKind.Move;
            if (!outOfNhl || !_abbrOf.TryGetValue(c.FromTeam, out var abbr)) continue;
            string k = Key(c.Name);
            var why = new List<string>();
            if (onCap.TryGetValue(abbr, out var capNames) && capNames.TryGetValue(k, out var section))
                why.Add(section == "inactive" ? $"CapWages has him on {abbr}'s injured or inactive list" : $"CapWages has him on {abbr}'s NHL roster");
            if (inLineup.TryGetValue(abbr, out var lineup) && lineup.Contains(k)
                // CapWages has him in that team's minors: the lineup is out of date (LineupCallUps.SentDown)
                && !LineupCallUps.SentDown(cap, abbr, c.Name)) why.Add($"he's in {abbr}'s DailyFaceoff lineup");
            if (why.Count == 0) continue;
            c.Hold = $"Held: {string.Join(", and ", why)}. {(c.IsAhl ? "TheAHL.com" : "NHL.com")}'s roster list is behind, so he stays on {abbr}.";
            r.Restore(false);
            r.NoteChanged();
        }
        SetButtons();
    }

    /// <summary>The ticked roster rows (trades, call-ups, info, left), keyed as Apply matches them on its fresh compare.</summary>
    private HashSet<(int GameId, SyncKind Kind, int ToTeam, bool IsAhl)> PickedMoves() =>
        _rows.Where(r => r.Selected && r.Change.Kind != SyncKind.Missing && r.Change.Hold == null).Select(r => (r.Change.GameId, r.Change.Kind, r.Change.ToTeam, r.Change.IsAhl)).ToHashSet();

    /// <summary>The ticked players to create, by source id, with their bios (a fresh compare's rows have none).</summary>
    private Dictionary<string, PlayerBio> PickedCreates() =>
        _rows.Where(r => r.Selected && r.Change.CanCreate && BioKey(r.Change) != null).GroupBy(r => BioKey(r.Change)).ToDictionary(g => g.Key, g => g.First().Change.Bio);

    /// <summary>
    /// Compares CapWages' contracts, positions and pick owners, the game's name calls and DailyFaceoff's lines with a copy
    /// of the game's database in which the ticked roster changes are made and the ticked missing players created, exactly
    /// as Apply does first; the ticked positions go in before the lines are compared, since the lines are built by
    /// position. The rows then show what Apply will write: a traded player's retention on his new team, a new player in
    /// his team's lineup, no "not on the team yet". New players' own contracts and positions aren't listed: creating a
    /// player writes his CapWages deal and positions with him. Rows that were there before keep their ticks.
    /// <para>With a <paramref name="scope"/> (a tick, not a fetch or apply) nothing is downloaded and only what the tick
    /// can change is checked again: the lines and lineup call-ups of the NHL teams it touches, and the contracts and
    /// positions only when it creates a player or fixes a birth date (which changes how players match). Draft picks and
    /// name calls never depend on a tick. Every other row stays as it was.</para>
    /// </summary>
    private async Task PlanAsync(PlanScope scope = null)
    {
        if (_mods == null) return;
        bool full = scope == null;
        if (full) _pending = null; // a full check covers every tick so far
        // A tick while an earlier tick's check is still running: that check is thrown away, so this one covers its teams too.
        if (!full && _planning && _runningScope != null) scope.Merge(_runningScope);
        var teams = full ? null : ScopeTeams(scope);
        // a tick that touches no NHL team (an AHL-only move) changes nothing below
        if (!full && teams.Count == 0 && !scope.Players) { if (!_planning) { ShowCapStages(); ShowLinesStage(); } return; }
        int run = ++_planRun;
        _runningScope = scope;
        bool capOn = CapOn && _cap != null, linesOn = LinesOn && _lines != null, namesOn = _api != null;
        bool redoPlayers = full || scope.Players;
        if (!capOn) ClearCap(keepPages: true);
        if (!linesOn) _lineRows = new List<SyncRow>();
        // call-ups come from DailyFaceoff lineups and from CapWages
        if (!linesOn && !capOn) _lineupRows = new List<SyncRow>();
        if (!namesOn) _nameRows = new List<SyncRow>();
        if (!capOn && !linesOn && !namesOn) { ShowCapStages(); ShowLinesStage(); ApplyFilter(); return; }

        if (full)
        {
            if (capOn) foreach (var s in new[] { _contractsStage, _positionsStage, _picksStage }) s.Update(StageState.Running, "checking…");
            if (linesOn) _linesStage.Update(StageState.Running, "checking…");
        }
        else ShowScopeChecking(scope, teams, StageState.Running);
        _planning = true;
        SetButtons();
        var api = _api;
        var ahl = _ahl;
        var cap = _cap;
        var lines = _lines;
        const bool sizes = true; // a sync always includes heights and weights
        var picked = PickedMoves();
        var toCreate = PickedCreates();
        var posTicks = _positionRows.ToDictionary(r => r.Pos.GameId, r => r.Selected);
        var lineupTicks = _lineupRows.ToDictionary(r => r.Change.GameId, r => r.Selected);
        // the list's roster changes, compared against this same game database: no need to compare the rosters again
        var compared = _rows.Select(r => r.Change).ToList();
        var leftPlans = _leftPlans;
        // a tick's check looks for lineup call-ups on its own teams, and still makes the ticked ones elsewhere
        var lineupLines = full || lines == null ? lines : lines.Where(l => teams.Contains(l.Abbr)
            || _lineupRows.Any(r => r.Selected && _abbrOf.TryGetValue(r.Change.ToTeam, out var a) && a == l.Abbr)).ToList();
        try
        {
            var db = await _mods.OpenDbAsync(Roster.Database);
            var result = await Task.Run(() =>
            {
                var players = SyncPlan.ApplyPlayers(db, api, ahl, sizes, picked, toCreate, capOn ? cap : null, compared, leftPlans);
                // lineup players the NHL's roster lists missed, called up as ticked before anything else is compared
                var lineup = linesOn || capOn ? SyncPlan.ApplyLineupCallUps(db, lineupLines, api, ahl, capOn ? cap : null, lineupTicks, othersByDefault: true).CallUps : null;
                var contracts = capOn ? ContractSync.Compare(db, cap).Where(c => !players.Created.Contains(c.GameId)).ToList() : null;
                List<PositionChange> positions = null;
                if (capOn)
                {
                    var all = PositionSync.Compare(db, cap);
                    positions = all.Where(c => !players.Created.Contains(c.GameId)).ToList();
                    // the positions Apply will write (ticked ones, and every new player's), so the lines below are built on them
                    PositionSync.Apply(db, all.Where(c => players.Created.Contains(c.GameId) || (posTicks.TryGetValue(c.GameId, out var t) ? t : c.Selected)));
                }
                var owners = capOn && full ? PickOwners.FromCapWages(cap, db) : null;
                var pickResult = capOn && full ? PickSync.Compare(db, owners.Picks) : null;
                var names = namesOn && full ? NameCalls.Compare(db) : null;
                var lineChanges = linesOn ? LineSync.Compare(db, full ? lines : lines.Where(l => teams.Contains(l.Abbr)).ToList(), capOn ? cap : null) : null;
                return (contracts, positions, owners, pickResult, names, lineChanges, lineup);
            });
            if (run != _planRun) return;

            if (capOn && redoPlayers)
            {
                var oldContracts = _contractRows.ToDictionary(r => r.Contract.Key, r => r.Selected);
                _contractRows = result.contracts.Select(c => Keep(new SyncRow(c, SetButtons), oldContracts.TryGetValue(c.Key, out var s) ? s : null)).ToList();
                _positionRows = result.positions.Select(c => Keep(PositionRow(c), posTicks.TryGetValue(c.GameId, out var s) ? s : null)).ToList();
            }
            if (capOn && full)
            {
                var oldPicks = _pickRows.ToDictionary(r => (r.Pick.Year, r.Pick.Round, r.Pick.OriginalTeam), r => r.Selected);
                _picks = result.owners.Picks;
                _pickResult = result.pickResult;
                _pickRows = _pickResult.Changes.Select(c => Keep(new SyncRow(c, SetButtons), oldPicks.TryGetValue((c.Year, c.Round, c.OriginalTeam), out var s) ? s : null)).ToList();
                var owners = result.owners;
                string range = owners.Picks.Count == 0 ? "" : $" ({owners.Picks.Min(p => p.DraftYear) - owners.Offset}–{owners.Picks.Max(p => p.DraftYear) - owners.Offset})";
                _pickSource = $" Draft picks from CapWages: {owners.Picks.Count} picks{range}; game first draft year {(owners.GameFirstYear < 0 ? "unknown" : owners.GameFirstYear.ToString())}, offset {owners.Offset}"
                    + (owners.Conflicts > 0 ? $"; {owners.Conflicts} picks listed by two teams (first kept)" : "") + ".";
            }
            if (namesOn && full)
            {
                var oldNames = _nameRows.ToDictionary(r => r.Call.GameId, r => r.Selected);
                _nameRows = result.names.Select(c => Keep(new SyncRow(c, SetButtons), oldNames.TryGetValue(c.GameId, out var s) ? s : null)).ToList();
            }
            if (linesOn)
            {
                // a team that was left alone (unticked because it couldn't go in) gets its own default tick once it can
                var oldLines = _lineRows.Where(r => r.Line.Ready).ToDictionary(r => r.Line.TeamAbbr, r => r.Selected);
                var newLines = result.lineChanges.Select(c => Keep(new SyncRow(c, SetButtons), oldLines.TryGetValue(c.TeamAbbr, out var s) ? s : null));
                var newLineup = result.lineup.Select(c => Keep(PlayerRow(c), lineupTicks.TryGetValue(c.GameId, out var s) ? s : null));
                if (full)
                {
                    _lineRows = newLines.ToList();
                    _lineupRows = newLineup.ToList();
                }
                else
                {
                    // only the touched teams' rows are replaced; the rest stay as they were
                    bool Touched(int team) => _abbrOf.TryGetValue(team, out var a) && teams.Contains(a);
                    _lineRows = _lineRows.Where(r => !teams.Contains(r.Line.TeamAbbr)).Concat(newLines).OrderBy(r => r.Line.TeamName).ToList();
                    _lineupRows = _lineupRows.Where(r => !Touched(r.Change.ToTeam)).Concat(newLineup.Where(r => Touched(r.Change.ToTeam)))
                        .OrderBy(r => r.Change.ToTeamName).ThenBy(r => r.Change.Name).ToList();
                }
            }
            else if (capOn && result.lineup != null)
                _lineupRows = result.lineup.Select(c => Keep(PlayerRow(c), lineupTicks.TryGetValue(c.GameId, out var s) ? s : null)).ToList();
            ApplyFilter();
            ShowCapStages();
            ShowLinesStage();
        }
        catch (Exception ex)
        {
            if (run != _planRun) return;
            if (capOn) foreach (var s in new[] { _contractsStage, _positionsStage, _picksStage }) s.Update(StageState.Failed, ex.Message);
            if (linesOn) _linesStage.Update(StageState.Failed, ex.Message);
        }
        finally
        {
            if (run == _planRun) { _planning = false; _runningScope = null; SetButtons(); }
        }
    }

    /// <summary>What a tick can change, gathered over the ticks before its check runs: the game teams and NHL abbreviations
    /// it touches, and whether players' matching or creation changes (then contracts and positions are checked too).</summary>
    private sealed class PlanScope
    {
        public HashSet<int> Teams { get; } = new();
        public HashSet<string> Abbrs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Players { get; set; }
        public void Merge(PlanScope o) { Teams.UnionWith(o.Teams); Abbrs.UnionWith(o.Abbrs); Players |= o.Players; }
    }

    /// <summary>Game team index → NHL abbreviation, for the NHL teams (from the last compare).</summary>
    private Dictionary<int, string> _abbrOf = new();
    private PlanScope _pending, _runningScope;

    /// <summary>The NHL teams (abbreviations) a scope touches.</summary>
    private HashSet<string> ScopeTeams(PlanScope scope)
    {
        var set = new HashSet<string>(scope.Abbrs, StringComparer.OrdinalIgnoreCase);
        foreach (int t in scope.Teams) if (_abbrOf.TryGetValue(t, out var a)) set.Add(a);
        return set;
    }

    /// <summary>The steps a tick's check covers, shown waiting or running: only those, so it's clear nothing is downloaded again.</summary>
    private void ShowScopeChecking(PlanScope scope, HashSet<string> teams, StageState state)
    {
        string what = state == StageState.Waiting ? "re-checking soon" : "re-checking";
        if (LinesOn && _lines != null && teams.Count > 0)
            _linesStage.Update(state, $"{what} {string.Join(", ", teams.OrderBy(t => t).Take(4))}{(teams.Count > 4 ? $" +{teams.Count - 4}" : "")}");
        if (CapOn && _cap != null && scope.Players)
            foreach (var s in new[] { _contractsStage, _positionsStage }) s.Update(state, what);
    }

    /// <summary>A roster (or lineup call-up) row whose tick re-checks what it touches.</summary>
    private SyncRow PlayerRow(SyncChange c)
    {
        SyncRow row = null;
        row = new SyncRow(c, () => OnRowTick(row));
        return row;
    }

    /// <summary>A position row whose tick re-checks its team's lines.</summary>
    private SyncRow PositionRow(PositionChange c)
    {
        SyncRow row = null;
        row = new SyncRow(c, () => OnRowTick(row));
        return row;
    }

    /// <summary>A rebuilt row keeps the tick it had before (when it had one).</summary>
    private static SyncRow Keep(SyncRow row, bool? was)
    {
        if (was is { } s) row.Restore(s);
        return row;
    }

    /// <summary>
    /// A roster, call-up or position row was ticked or unticked: the buttons, and shortly after a check of only what it
    /// touches: the lines of the NHL team(s) he leaves and joins (a position: his team's), plus the contracts and
    /// positions when it creates a player or fixes a birth date.
    /// </summary>
    private void OnRowTick(SyncRow row)
    {
        SetButtons();
        if (_busy || !_fetched || row == null) return;
        _pending ??= new PlanScope();
        if (row.Change is { } c)
        {
            if (c.FromTeam >= 0) _pending.Teams.Add(c.FromTeam);
            if (c.ToTeam >= 0) _pending.Teams.Add(c.ToTeam);
            if (c.Kind == SyncKind.Missing || c.BornChanges || c.NameOnly) _pending.Players = true;
        }
        if (row.Pos is { } p && !string.IsNullOrEmpty(p.TeamAbbr)) _pending.Abbrs.Add(p.TeamAbbr);
        ShowScopeChecking(_pending, ScopeTeams(_pending), StageState.Waiting);
        _replan.Stop();
        _replan.Start();
    }

    /// <summary>Drops the CapWages results (and, unless <paramref name="keepPages"/>, the pages read).</summary>
    private void ClearCap(bool keepPages = false)
    {
        if (!keepPages) { _cap = null; _capError = null; }
        _picks = null;
        _pickResult = null;
        _pickRows = new List<SyncRow>();
        _contractRows = new List<SyncRow>();
        _positionRows = new List<SyncRow>();
        _pickSource = "";
    }

    // ---------------------------------------------------------------- the status line

    private void ShowSummary(string lead = "")
    {
        FetchStatus.Text = _api == null
            ? (_nhlError != null ? $"Couldn't fetch the NHL rosters: {_nhlError}." : "") + CapCounts() + LinesCounts() + _pickSource
            : $"{lead}{_api.Count} players on NHL rosters{AhlSummary()}, fetched {_fetchedAt:HH:mm}. " + Counts()
              + (_ahlError == null ? "" : $" AHL failed: {_ahlError}.")
              + (_bioFailed == 0 ? "" : $" Bios couldn't be fetched for {_bioFailed} missing player{(_bioFailed == 1 ? "" : "s")}.")
              + _pickSource;
    }

    private string Counts()
    {
        var all = _rows.Concat(_lineupRows).Concat(_nameRows).Concat(_contractRows).Concat(_positionRows).Concat(_pickRows).Concat(_lineRows).ToList();
        int held = all.Count(r => r.IsHeld), review = all.Count(r => r.NeedsReview), auto = all.Count(r => !r.IsHeld && !r.NeedsReview && r.Selected);
        string lead = $"{auto} changes go in automatically, {review} need your review (the Review list)" + (held > 0 ? $", {held} held (other sources have the player where he is)" : "") + ". ";
        int N(SyncKind k) => _rows.Count(r => !r.IsHeld && !r.Change.IsAhl && !r.Change.FromAhl && r.Change.Kind == k);
        int A(SyncKind k) => _rows.Count(r => !r.IsHeld && (r.Change.IsAhl || r.Change.FromAhl) && r.Change.Kind == k);
        int released = _rows.Count(r => !r.IsHeld && r.Change.Release);
        string s = lead + $"{N(SyncKind.Move)} trades, {N(SyncKind.Join)} call-ups/signings, {N(SyncKind.Update)} info changes, " +
                   $"{N(SyncKind.Left)} no longer on an NHL roster, {N(SyncKind.Missing)} not in the game.";
        if (_rows.Any(r => r.Change.IsAhl || r.Change.FromAhl))
            s += $" AHL: {A(SyncKind.Join)} call-ups, {A(SyncKind.Move)} moves, {A(SyncKind.Update)} info changes, {A(SyncKind.Left)} no longer on an AHL roster, {A(SyncKind.Missing)} not in the game.";
        if (released > 0) s += $" {released} released to free agency (no contract or club on CapWages).";
        if (_nameRows.Count > 0) s += $" {_nameRows.Count} player{(_nameRows.Count == 1 ? "" : "s")} can get a play-by-play name call.";
        return s + CapCounts() + LinesCounts();
    }

    /// <summary>The DailyFaceoff part of the status line; "" when the source is unticked or nothing was read.</summary>
    private string LinesCounts()
    {
        if (_lines == null || !LinesOn) return "";
        int failed = _lines.Count(t => t.Error != null), left = _lineRows.Count(r => r.Line.CanApply && !r.Line.Ready);
        return $" {_lineRows.Count(r => r.Line.Ready)} teams' lines differ from DailyFaceoff" + (left > 0 ? $" ({left} left alone: a player in the lineup isn't on the team)" : "")
            + (failed > 0 ? $"; {failed} team page{(failed == 1 ? "" : "s")} couldn't be read" : "") + "."
            + (_lineupRows.Count > 0 ? $" {_lineupRows.Count} player{(_lineupRows.Count == 1 ? " is" : "s are")} in a DailyFaceoff lineup but missing from NHL.com's roster lists (call-up rows)." : "");
    }

    /// <summary>The CapWages part of the status line: contracts and pick owners that differ, or why CapWages couldn't be compared; "" when the source is unticked.</summary>
    private string CapCounts()
    {
        if (!CapOn) return "";
        if (_capError != null) return $" CapWages couldn't be read, so contracts and picks were not compared: {_capError}";
        string s = "";
        if (_cap != null)
        {
            int players = _contractRows.Count(r => r.Contract.Kind == ContractChangeKind.Contract);
            int teams = _contractRows.Count(r => r.Contract.Kind == ContractChangeKind.TeamRetention);
            int buyouts = _contractRows.Count(r => r.Contract.Kind == ContractChangeKind.TeamBuyouts);
            s += $" {players} contract{(players == 1 ? "" : "s")}, {teams} team{(teams == 1 ? "" : "s")}' retained salary, {buyouts} team{(buyouts == 1 ? "" : "s")}' buyouts and {_positionRows.Count} player{(_positionRows.Count == 1 ? "'s" : "s'")} positions differ from CapWages.";
        }
        if (_pickResult != null)
        {
            s += $" {_pickRows.Count} draft pick owner{(_pickRows.Count == 1 ? "" : "s")} differ.";
            var notes = new List<string>();
            if (_pickResult.NoGameRow > 0) notes.Add($"{_pickResult.NoGameRow} CapWages picks with no game row");
            if (_pickResult.NoSourcePick > 0) notes.Add($"{_pickResult.NoSourcePick} game picks CapWages doesn't list");
            if (_pickResult.SkippedUnmapped > 0) notes.Add($"{_pickResult.SkippedUnmapped} unmapped");
            if (notes.Count > 0) s += " (" + string.Join(", ", notes) + ")";
        }
        return s;
    }

    /// <summary>", N players on M AHL teams" for the status line, plus any skipped teams; "" when the AHL source is unticked.</summary>
    private string AhlSummary() => _ahl == null ? "" : $", {_ahl.Sum(t => t.Players.Count)} on {_ahl.Count} AHL teams" +
        (_ahlSkipped.Count == 0 ? "" : $" (teams not in the game: {string.Join(", ", _ahlSkipped)})");

    // ---------------------------------------------------------------- the list

    private SyncKind? Kind => KindMove.IsChecked == true ? SyncKind.Move : KindJoin.IsChecked == true ? SyncKind.Join :
        KindUpdate.IsChecked == true ? SyncKind.Update : KindLeft.IsChecked == true ? SyncKind.Left : KindMissing.IsChecked == true ? SyncKind.Missing : null;

    private IEnumerable<SyncRow> Shown => ChangeList.ItemsSource as IEnumerable<SyncRow> ?? Enumerable.Empty<SyncRow>();

    private void ApplyFilter()
    {
        if (ChangeList == null) return;
        var kind = Kind;
        bool picksOnly = KindPicks.IsChecked == true, contractsOnly = KindContracts.IsChecked == true, linesOnly = KindLines.IsChecked == true,
             positionsOnly = KindPositions.IsChecked == true;
        string q = SyncSearch.Text.Trim();
        bool reviewOnly = KindReview.IsChecked == true;
        // held rows have nothing to do: never listed (counted in the status line)
        var rows = _rows.Concat(_lineupRows).Concat(_nameRows).Concat(_contractRows).Concat(_positionRows).Concat(_pickRows).Concat(_lineRows).Where(r => !r.IsHeld).Where(r =>
            (reviewOnly ? r.NeedsReview : picksOnly ? r.IsPick : contractsOnly ? r.IsContract : linesOnly ? r.IsLines : positionsOnly ? r.IsPosition :
             kind == null || r.IsPlayer && r.Change.Kind == kind || r.IsNameCall && kind == SyncKind.Update) &&
            (q.Length == 0 || (!r.IsPlayer ? r.SearchText.Contains(q, StringComparison.OrdinalIgnoreCase)
                : r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                  || (r.Change.Api?.Team ?? r.Change.Ahl?.TeamName ?? "").Equals(q, StringComparison.OrdinalIgnoreCase)))).ToList();
        ChangeList.ItemsSource = rows;
        EmptyText.Text = _busy && !_fetched ? "Reading the sources…" :
            _api == null && _cap == null && _lines == null ? "Press Fetch & compare to compare the game with today's NHL and AHL." :
            reviewOnly ? "Nothing needs your review: Apply writes every change listed under All." : "Nothing here: the game already matches.";
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetButtons();
    }

    private void OnFilter(object sender, RoutedEventArgs e) => ApplyFilter();

    private void OnTickAll(object sender, RoutedEventArgs e) { foreach (var r in Shown) r.Selected = true; }
    private void OnTickNone(object sender, RoutedEventArgs e) { foreach (var r in Shown) r.Selected = false; }

    private void SetButtons()
    {
        var all = _rows.Concat(_lineupRows).Concat(_nameRows).Concat(_contractRows).Concat(_positionRows).Concat(_pickRows).Concat(_lineRows).ToList();
        int n = all.Count(r => r.Selected), total = all.Count;
        ApplyButton.IsEnabled = !_busy && !_planning && n > 0;
        TickAll.IsEnabled = TickNone.IsEnabled = !_busy && Shown.Any();
        ApplyLabel.Text = _planning ? "Checking…" : n > 0 ? $"Apply {n} to game" : "Apply to game";
        ApplyStatus.Text = _api == null && _cap == null && _lines == null ? "" : $"{n} of {total} ticked. Players who change team get their line spots cleared; the game puts them in its own lines."
            + (_rows.Any(r => r.Change.CanCreate) ? " Create rows add the player with ratings and gear copied from a similar player, a generic face, his play-by-play name when the game has it and (with CapWages ticked) his CapWages deal and positions." : "")
            + (_positionRows.Count > 0 ? " Position rows set the primary and secondary position from where he has played." : "")
            + (_nameRows.Count > 0 ? " Name call rows give the commentators his name." : "")
            + (_contractRows.Count > 0 ? " Contract rows write the deal, extension, clauses and retention; an unchanged deal keeps the game's clauses." : "")
            + (_pickRows.Count > 0 ? " Pick rows only change the pick's owner." : "")
            + (_lineRows.Count > 0 ? " Lines rows set the team's lines, special teams and goalies from DailyFaceoff, after the roster changes and new players." : "");
    }

    // ---------------------------------------------------------------- applying

    private async void OnApply(object sender, RoutedEventArgs e) => await ApplyAsync();

    /// <summary>
    /// Writes the ticked changes into the game, on a fresh read of the database, in order: roster changes, new players
    /// (and their contracts and positions), the ticked contracts, positions, name calls, lines, draft picks — each matched afresh against the database as
    /// the step before left it.
    /// </summary>
    public async Task ApplyAsync()
    {
        _replan.Stop();
        var picked = PickedMoves();
        var toCreate = PickedCreates();
        bool contractsOn = CapOn;
        var pickedPicks = _pickRows.Where(r => r.Selected).Select(r => (r.Pick.Year, r.Pick.Round, r.Pick.OriginalTeam)).ToHashSet();
        var pickedContracts = _contractRows.Where(r => r.Selected).Select(r => r.Contract.Key).ToHashSet();
        var pickedPositions = _positionRows.Where(r => r.Selected).Select(r => r.Pos.GameId).ToHashSet();
        var pickedNames = _nameRows.Where(r => r.Selected).Select(r => r.Call.GameId).ToHashSet();
        var pickedLines = _lineRows.Where(r => r.Selected).Select(r => r.Line.TeamAbbr).ToHashSet();
        var lineupTicks = _lineupRows.ToDictionary(r => r.Change.GameId, r => r.Selected);
        int pickedLineup = lineupTicks.Count(t => t.Value);
        if (picked.Count == 0 && toCreate.Count == 0 && pickedPicks.Count == 0 && pickedContracts.Count == 0 && pickedLines.Count == 0
            && pickedPositions.Count == 0 && pickedNames.Count == 0 && pickedLineup == 0 || _busy || _planning) return;
        if (ModService.GameRunning)
        {
            MessageBox.Show(Window.GetWindow(this), "KytyPS5 is running. Close the game first: its files can't be changed while it reads them.", "HMS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SetBusy(true);
        var api = _api;
        var ahl = _ahl;
        var picks = _picks;
        var cap = _cap;
        var lines = _lines;
        var leftPlans = _leftPlans;
        const bool sizes = true; // a sync always includes heights and weights
        int totalPicked = picked.Count + toCreate.Count + pickedPicks.Count + pickedContracts.Count + pickedLines.Count + pickedPositions.Count + pickedNames.Count + pickedLineup;
        StatusChanged?.Invoke($"Writing {totalPicked} roster change(s) into the game…");
        string summary = null;
        try
        {
            await _mods.SaveDbAsync(Roster.Database, db =>
            {
                var players = SyncPlan.ApplyPlayers(db, api, ahl, sizes, picked, toCreate, contractsOn ? cap : null, left: leftPlans);
                var summaries = players.Summaries;
                var created = players.Created;
                // lineup call-ups right after the roster changes, matched afresh; only the ticked rows go in
                if ((lines != null || cap != null) && pickedLineup > 0 && SyncPlan.ApplyLineupCallUps(db, lines, api, ahl, cap, lineupTicks, othersByDefault: false).Summary is { } lineupSummary)
                    summaries.Add("Lineup call-ups: " + lineupSummary);
                // Contracts after the roster moves (a traded player's retention depends on his new team), re-compared
                // fresh against the database being saved: the ticked ones, and every new player's (he starts unsigned).
                if (cap != null && (pickedContracts.Count > 0 || contractsOn && created.Count > 0))
                {
                    var freshContracts = ContractSync.Compare(db, cap).Where(c => pickedContracts.Contains(c.Key) ||
                        contractsOn && c.Kind == ContractChangeKind.Contract && created.Contains(c.GameId) && !c.NameOnly).ToList();
                    if (freshContracts.Count > 0) summaries.Add(ContractSync.Apply(db, freshContracts));
                }
                // Positions before the lines (the lines are built by position): the ticked ones, and every new player's.
                if (cap != null && (pickedPositions.Count > 0 || contractsOn && created.Count > 0))
                {
                    var freshPositions = PositionSync.Compare(db, cap).Where(c => pickedPositions.Contains(c.GameId) ||
                        contractsOn && created.Contains(c.GameId) && !c.NameOnly).ToList();
                    if (freshPositions.Count > 0) summaries.Add(PositionSync.Apply(db, freshPositions));
                }
                if (pickedNames.Count > 0)
                {
                    var freshNames = NameCalls.Compare(db).Where(c => pickedNames.Contains(c.GameId)).ToList();
                    if (freshNames.Count > 0) summaries.Add(NameCalls.Apply(db, freshNames));
                }
                // Lines last among the roster parts: the moves and new players are in, so the lineups can find them.
                if (lines != null && pickedLines.Count > 0) summaries.Add(LineSync.Apply(db, lines, pickedLines));
                if (picks != null)
                {
                    var freshPicks = PickSync.Compare(db, picks).Changes.Where(c => pickedPicks.Contains((c.Year, c.Round, c.OriginalTeam))).ToList();
                    if (freshPicks.Count > 0) summaries.Add(PickSync.Apply(db, freshPicks));
                }
                if (summaries.Count == 0) return null;
                summary = string.Join("; ", summaries);
                return summary + $" ({DateTime.Now:yyyy-MM-dd})";
            }, "Roster sync");
            if (summary != null)
            {
                // the retained players as the game now has them (a new player or a traded one can change them)
                await SaveRetainedNotesAsync();
                StatusChanged?.Invoke($"{summary}. The Mods page can restore the original.");
                Applied?.Invoke();
            }
            else
            {
                // SaveDbAsync wrote nothing (the fresh re-compare matched none of the ticked rows): say so.
                StatusChanged?.Invoke("Nothing was written: the ticked rows no longer matched on a fresh compare. Fetch again and check the list.");
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("Can't apply: " + ex.Message);
            if (!App.Args.ContainsKey("shot"))
                MessageBox.Show(Window.GetWindow(this), ex.Message, "Can't write the roster changes", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        try
        {
            // What's left after applying: the rosters, then the rest against them.
            ShowPlanWaiting();
            if (api != null) await CompareAsync();
            await PlanAsync();
            ShowSummary("After applying: ");
        }
        finally { SetBusy(false); }
    }

    /// <summary>Every row as text, one per line (scripted runs): kind, tick, review/held, name, description, note.</summary>
    public string DumpRows() => string.Join(Environment.NewLine,
        _rows.Concat(_lineupRows).Concat(_nameRows).Concat(_contractRows).Concat(_positionRows).Concat(_pickRows).Concat(_lineRows)
            .Select(r => $"{r.KindText}	{(r.Selected ? "[x]" : "[ ]")}	{(r.IsHeld ? "HELD" : r.NeedsReview ? "REVIEW" : "auto")}	{r.Name}	{r.Description}	{r.Note}"));

    /// <summary>Flips the tick of the first row shown whose name contains <paramref name="name"/> (scripted runs); false when none.</summary>
    public bool TickByName(string name)
    {
        var row = Shown.FirstOrDefault(r => r.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (row == null) return false;
        row.Selected = !row.Selected;
        return true;
    }

    /// <summary>Shows one kind of change (scripted runs): move, join, update, left, missing, contracts, picks, lines.</summary>
    public void ShowKind(string kind)
    {
        (kind switch { "review" => KindReview, "move" => KindMove, "join" => KindJoin, "update" => KindUpdate, "left" => KindLeft, "missing" => KindMissing, "contracts" => KindContracts, "picks" => KindPicks, "positions" => KindPositions, "lines" => KindLines, _ => KindAll }).IsChecked = true;
    }
}
