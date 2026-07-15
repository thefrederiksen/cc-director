using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Avalonia.Fleet;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia.Controls;

/// <summary>One lane in the desktop fleet map, bound by the XAML.</summary>
public sealed class FleetLaneItem : INotifyPropertyChanged
{
    public required string Title { get; init; }
    public ObservableCollection<FleetCardItem> Nodes { get; } = new();
    public string CountDisplay => Nodes.Count == 1 ? "1 session" : $"{Nodes.Count} sessions";

    public event PropertyChangedEventHandler? PropertyChanged;
    public void RaiseCountChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountDisplay)));
}

/// <summary>
/// One card. Everything it shows about state is READ from the Gateway's stamped answers
/// (<see cref="SessionDto.EffectiveColor"/>, <see cref="SessionDto.StateLabel"/>,
/// <see cref="SessionDto.SessionRole"/>) - never recomputed here. See <see cref="FleetMapTree"/> for why.
/// </summary>
public sealed class FleetCardItem : INotifyPropertyChanged
{
    public required SessionDto Session { get; set; }
    public required int Depth { get; set; }

    /// <summary>True when this Director owns the session, so clicking it can select it in the rail.</summary>
    public required bool IsLocal { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Refresh this card from a newer roster WITHOUT replacing the object.
    ///
    /// This exists because the poll used to rebuild every card from scratch, which destroys and recreates
    /// every control several times a minute. That is not merely wasteful: it drops keyboard focus (so a card
    /// cannot be tabbed to and activated), resets hover, and lets a click that lands mid-rebuild hit nothing
    /// at all - which is exactly the case this view is FOR (watch the map, a session goes red, click it).
    /// Updating in place keeps the controls alive, so the card under the pointer stays the card that gets
    /// clicked.
    /// </summary>
    public void UpdateFrom(SessionDto session, int depth, bool isLocal)
    {
        Session = session;
        Depth = depth;
        IsLocal = isLocal;
        // Everything the XAML binds is computed from those three, so tell it all of them may have moved.
        foreach (var p in new[]
                 {
                     nameof(Session), nameof(Depth), nameof(IsLocal), nameof(IndentMargin), nameof(DotBrush),
                     nameof(BorderBrushForCard), nameof(HasNumber), nameof(NumberDisplay), nameof(NameDisplay),
                     nameof(AgentDisplay), nameof(HasRole), nameof(RoleDisplay), nameof(RoleBrush),
                     nameof(LocationDisplay), nameof(StateDisplay), nameof(OwnershipDisplay), nameof(AutomationName),
                 })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    // The indent is capped even though the depth is not: nesting is real, but a deep chain must lean right
    // a little and then stop rather than squeezing the cards into a sliver.
    public Thickness IndentMargin => new(Math.Min(Depth, 4) * 14, 0, 0, 6);

    public IBrush DotBrush => StatusPalette.BrushFor(Session.EffectiveColor);

    public IBrush BorderBrushForCard =>
        string.Equals(Session.EffectiveColor, "red", StringComparison.OrdinalIgnoreCase)
            ? StatusPalette.BrushFor("red")
            : BorderDefault;

    private static readonly ISolidColorBrush BorderDefault = new SolidColorBrush(Color.Parse("#3C3C3C"));
    private static readonly ISolidColorBrush RoleArchitect = new SolidColorBrush(Color.Parse("#C084FC"));
    private static readonly ISolidColorBrush RoleManager = new SolidColorBrush(Color.Parse("#60A5FA"));
    private static readonly ISolidColorBrush RoleWorker = new SolidColorBrush(Color.Parse("#94A3B8"));

    public bool HasNumber => Session.Number.HasValue;
    public string NumberDisplay => Session.Number?.ToString() ?? "";

    public string NameDisplay => string.IsNullOrWhiteSpace(Session.Name) ? "(unnamed)" : Session.Name!;

    public string AgentDisplay => string.IsNullOrWhiteSpace(Session.Agent) ? "?" : Session.Agent;

    // Standalone is the default and a badge on every card would say nothing.
    public bool HasRole => !string.IsNullOrWhiteSpace(Session.SessionRole)
                           && !string.Equals(Session.SessionRole, "Standalone", StringComparison.OrdinalIgnoreCase);

    public string RoleDisplay => (Session.SessionRole ?? "").ToUpperInvariant();

    public IBrush RoleBrush => (Session.SessionRole ?? "").ToLowerInvariant() switch
    {
        "architect" => RoleArchitect,
        "manager" => RoleManager,
        "worker" => RoleWorker,
        _ => BorderDefault,
    };

    public string LocationDisplay
    {
        get
        {
            var machine = (Session.MachineName ?? "").Trim();
            var repo = FleetMapLanes.RepoBasename(Session.RepoPath);
            if (machine.Length > 0 && repo.Length > 0) return $"{machine}  {repo}";
            return machine.Length > 0 ? machine : repo;
        }
    }

    public string StateDisplay => string.IsNullOrWhiteSpace(Session.StateLabel)
        ? (Session.ActivityState ?? "")
        : Session.StateLabel!;

    // The card says plainly whether clicking it will select it here or hand it to the Cockpit. A click
    // that behaves differently with nothing on screen to say so is the dead click this view must not have.
    public string OwnershipDisplay => IsLocal ? "click to open" : "on another Director - opens Cockpit";

    /// <summary>
    /// The card's name in the accessibility tree. Carries the number and the name (what the owner reads)
    /// plus where the click goes, so the card is identifiable without sighted access to the layout.
    /// </summary>
    public string AutomationName
    {
        get
        {
            var num = Session.Number.HasValue ? $"{Session.Number} " : "";
            var where = IsLocal ? "on this Director" : $"on {Session.MachineName}, opens in the Cockpit";
            return $"{num}{NameDisplay} - {StateDisplay} - {where}";
        }
    }
}

/// <summary>
/// Issue #1627: the fleet map, inside the desktop. Every session on every machine, sliced by repository or
/// by agent, ordered as the spawn tree - and, unlike the Cockpit's map, clicking a session this Director
/// owns SELECTS it in the rail. That is the whole reason it is worth having here rather than in a browser.
///
/// It is a re-implementation, not a port: the Cockpit's map is React over HTTP and its click opens a web
/// deep link. Only the DATA is shared, and it is shared completely - the roster arrives with the Gateway's
/// roles, colours, and state labels already stamped, and this view reads them.
///
/// The roster is fetched over the Director's existing OUTBOUND Gateway client, not the tunnel (which is
/// push-only). See ControlApiHost.ListFleetSessionsAsync.
/// </summary>
public partial class FleetMapView : UserControl
{
    private readonly ObservableCollection<FleetLaneItem> _lanes = new();
    private DispatcherTimer? _timer;
    private FleetPivot _pivot = FleetPivot.Repository;

    /// <summary>Resolves the fleet roster. Set by the host so this control needs no Gateway knowledge.</summary>
    public Func<CancellationToken, Task<List<SessionDto>>?>? FleetSource { get; set; }

    /// <summary>The session ids this Director owns - the ones a click can select in the rail.</summary>
    public Func<HashSet<string>>? LocalSessionIds { get; set; }

    /// <summary>Raised when a card is clicked. The host decides what to do; see MainWindow.</summary>
    public event Action<SessionDto, bool>? SessionActivated;

    public FleetMapView()
    {
        InitializeComponent();
        LanesList.ItemsSource = _lanes;
        UpdatePivotButtons();
    }

    /// <summary>Begin polling the fleet. Called when the overlay opens.</summary>
    public void StartPolling()
    {
        FileLog.Write("[FleetMapView] StartPolling");
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
        _ = RefreshAsync();
    }

    /// <summary>Stop polling. Called when the overlay closes, so a hidden map costs nothing.</summary>
    public void StopPolling()
    {
        FileLog.Write("[FleetMapView] StopPolling");
        _timer?.Stop();
    }

    private void OnTick(object? sender, EventArgs e) => _ = RefreshAsync();

    private void BtnPivotRepo_Click(object? sender, RoutedEventArgs e)
    {
        _pivot = FleetPivot.Repository;
        UpdatePivotButtons();
        _ = RefreshAsync();
    }

    private void BtnPivotAgent_Click(object? sender, RoutedEventArgs e)
    {
        _pivot = FleetPivot.Agent;
        UpdatePivotButtons();
        _ = RefreshAsync();
    }

    private void UpdatePivotButtons()
    {
        var on = new SolidColorBrush(Color.Parse("#007ACC"));
        var off = new SolidColorBrush(Color.Parse("#3C3C3C"));
        BtnPivotRepo.Background = _pivot == FleetPivot.Repository ? on : off;
        BtnPivotRepo.Foreground = Brushes.White;
        BtnPivotAgent.Background = _pivot == FleetPivot.Agent ? on : off;
        BtnPivotAgent.Foreground = Brushes.White;
    }

    private void Card_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not FleetCardItem item) return;
        FileLog.Write($"[FleetMapView] Card clicked: session={item.Session.SessionId}, local={item.IsLocal}");
        SessionActivated?.Invoke(item.Session, item.IsLocal);
    }

    /// <summary>
    /// Fetch the fleet and rebuild the lanes. Failures are SHOWN, never swallowed into an empty map: an
    /// unreachable Gateway and a genuinely empty fleet must not look the same.
    /// </summary>
    private async Task RefreshAsync()
    {
        try
        {
            var task = FleetSource?.Invoke(CancellationToken.None);
            if (task is null)
            {
                Show("No Gateway is configured, so this Director cannot see the fleet. Connect it in Settings.");
                return;
            }

            var sessions = await task;
            var local = LocalSessionIds?.Invoke() ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lanes = FleetMapLanes.Build(sessions, _pivot, FleetMapLanes.DefaultSort);

            // Reconcile in place - do NOT rebuild. See FleetCardItem.UpdateFrom for why: a from-scratch
            // rebuild on a 3-second timer destroys and recreates every control, which drops keyboard focus
            // and lets a click that lands mid-rebuild hit nothing. The steady state here (same sessions,
            // changed colours/labels) touches no controls at all. Structure only changes when a session
            // actually appears or disappears, which is rare and is when a reflow is honest.
            var offset = MapScroll.Offset;
            Reconcile(lanes, local);

            var machines = sessions.Select(s => (s.MachineName ?? "").Trim())
                                   .Where(m => m.Length > 0)
                                   .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var needsYou = sessions.Count(s => string.Equals(s.EffectiveColor, "red", StringComparison.OrdinalIgnoreCase));
            StatsText.Text = $"{sessions.Count} sessions  |  {machines} machine{(machines == 1 ? "" : "s")}  |  " +
                             $"{lanes.Count} {(_pivot == FleetPivot.Repository ? "repos" : "agents")}  |  {needsYou} needs you";
            MessageText.IsVisible = false;

            if (offset.Y > 0)
                Dispatcher.UIThread.Post(() => MapScroll.Offset = offset, DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetMapView] RefreshAsync FAILED: {ex.Message}");
            Show($"Could not read the fleet from the Gateway: {ex.Message}");
        }
    }

    /// <summary>
    /// Fold a freshly-built lane set into the live collections, keeping every existing control alive when
    /// the SHAPE has not changed (same lanes, same sessions in the same order) - which is the overwhelmingly
    /// common case between two polls three seconds apart.
    ///
    /// The comparison is deliberately by SHAPE (lane titles, then session ids in order), not by content: the
    /// content changes constantly (idle clocks, colours, labels) and none of it needs a control rebuilt.
    /// </summary>
    private void Reconcile(List<FleetLane> lanes, HashSet<string> local)
    {
        var titlesMatch = _lanes.Count == lanes.Count;
        if (titlesMatch)
            for (var i = 0; i < lanes.Count; i++)
                if (!string.Equals(_lanes[i].Title, lanes[i].Title, StringComparison.Ordinal))
                {
                    titlesMatch = false;
                    break;
                }

        if (!titlesMatch)
        {
            _lanes.Clear();
            foreach (var lane in lanes)
            {
                var item = new FleetLaneItem { Title = lane.Title };
                foreach (var n in lane.Nodes) item.Nodes.Add(Card(n, local));
                _lanes.Add(item);
            }
            return;
        }

        for (var i = 0; i < lanes.Count; i++)
        {
            var want = lanes[i].Nodes;
            var have = _lanes[i].Nodes;

            var idsMatch = have.Count == want.Count;
            if (idsMatch)
                for (var j = 0; j < want.Count; j++)
                    if (!string.Equals(have[j].Session.SessionId, want[j].Session.SessionId, StringComparison.Ordinal))
                    {
                        idsMatch = false;
                        break;
                    }

            if (idsMatch)
            {
                // The steady state: same cards, newer facts. Update the objects; the controls never move.
                for (var j = 0; j < want.Count; j++)
                    have[j].UpdateFrom(want[j].Session, want[j].Depth, local.Contains(want[j].Session.SessionId ?? ""));
                continue;
            }

            have.Clear();
            foreach (var n in want) have.Add(Card(n, local));
            _lanes[i].RaiseCountChanged();
        }
    }

    private static FleetCardItem Card(FleetTreeNode n, HashSet<string> local) => new()
    {
        Session = n.Session,
        Depth = n.Depth,
        IsLocal = local.Contains(n.Session.SessionId ?? ""),
    };

    private void Show(string message)
    {
        MessageText.Text = message;
        MessageText.IsVisible = true;
        StatsText.Text = "Fleet unavailable";
        _lanes.Clear();
    }
}
