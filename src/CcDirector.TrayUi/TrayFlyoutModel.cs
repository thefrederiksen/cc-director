using Avalonia.Media;

namespace CcDirector.TrayUi;

/// <summary>How the status pill at the top of the flyout reads: green / amber / red.</summary>
public enum StatusLevel { Ok, Warn, Error }

/// <summary>One label/value line in the flyout's status block (e.g. "Version" / "0.9.8").</summary>
public sealed record StatusRow(string Label, string Value);

/// <summary>A full-width action button in the flyout. <see cref="Primary"/> actions are accented.</summary>
public sealed class FlyoutAction
{
    public required string Text { get; init; }
    public required Action OnClick { get; init; }
    public bool Primary { get; init; }
}

/// <summary>
/// A titled block of label/value rows rendered between the actions and the details section
/// (e.g. the Gateway's "Fleet" block with one row per Director).
/// </summary>
public sealed class FlyoutSection
{
    public required string Title { get; init; }
    public required IReadOnlyList<StatusRow> Rows { get; init; }
}

/// <summary>The "Start with Windows / on login" switch row.</summary>
public sealed class ToggleSpec
{
    public required string Label { get; init; }
    public required bool IsOn { get; init; }
    public required Action<bool> OnChanged { get; init; }
}

/// <summary>
/// Everything the shared <see cref="TrayFlyout"/> needs to render one app's panel. Each tray app
/// builds a fresh model each time the flyout opens, so the panel always shows current state.
/// The layout/chrome is identical across apps; only this content differs.
/// </summary>
public sealed class TrayFlyoutModel
{
    /// <summary>App name shown in the header (e.g. "CC Launcher", "DevThrottle Gateway").</summary>
    public required string AppName { get; init; }

    /// <summary>The tray icon, already loaded by the app (the library never resolves app resources).</summary>
    public IImage? Icon { get; init; }

    /// <summary>Primary status line, e.g. "Running on :7900".</summary>
    public required string StatusTitle { get; init; }

    /// <summary>Optional secondary status line under the title.</summary>
    public string? StatusDetail { get; init; }

    /// <summary>Drives the status pill colour.</summary>
    public StatusLevel Status { get; init; } = StatusLevel.Ok;

    /// <summary>
    /// One word for the header status pill (e.g. "Running", "Starting", "Failed"). When null the
    /// header shows a bare status dot instead of the pill.
    /// </summary>
    public string? StatusPillText { get; init; }

    /// <summary>Accent colour for the primary action button + header rule (per-app brand tint).</summary>
    public Color Accent { get; init; } = Color.Parse("#F2600C");

    /// <summary>Label/value status rows.</summary>
    public IReadOnlyList<StatusRow> Rows { get; init; } = Array.Empty<StatusRow>();

    /// <summary>
    /// Optional titled row blocks rendered between the actions and the details section, in order
    /// (e.g. a "Fleet" block listing every Director). Empty hides them.
    /// </summary>
    public IReadOnlyList<FlyoutSection> Sections { get; init; } = Array.Empty<FlyoutSection>();

    /// <summary>
    /// Optional secondary label/value rows rendered as a quieter "Details" section under the
    /// actions (build info, install paths, component versions). Empty hides the section.
    /// </summary>
    public IReadOnlyList<StatusRow> DetailRows { get; init; } = Array.Empty<StatusRow>();

    /// <summary>Caption of the <see cref="DetailRows"/> section.</summary>
    public string DetailsTitle { get; init; } = "Details";

    /// <summary>
    /// Action buttons. <see cref="FlyoutAction.Primary"/> actions render as full-width accented
    /// buttons at the top; the rest flow into a two-per-row grid of secondary buttons.
    /// </summary>
    public IReadOnlyList<FlyoutAction> Actions { get; init; } = Array.Empty<FlyoutAction>();

    /// <summary>
    /// Quiet text links in the footer (e.g. Settings / Logs / Config), rendered left of Quit.
    /// </summary>
    public IReadOnlyList<FlyoutAction> FooterLinks { get; init; } = Array.Empty<FlyoutAction>();

    /// <summary>Optional toggle switch row (autostart).</summary>
    public ToggleSpec? Toggle { get; init; }

    /// <summary>Quit handler; when set, a quiet "Quit" appears in the footer.</summary>
    public Action? OnQuit { get; init; }
}
