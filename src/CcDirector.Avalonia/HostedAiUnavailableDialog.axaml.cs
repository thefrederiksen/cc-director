using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Avalonia.HostedAi;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// The single shared modal every desktop voice/Wingman/text-to-speech surface shows when hosted AI is
/// unavailable (issue #940, epic #937): the ONE message from <see cref="HostedAiMessages"/> for the
/// resolved <see cref="HostedAiState"/> plus its call-to-action button ("Add credits" -> billing, or
/// "Add a key" -> Cockpit settings). Using one dialog everywhere makes the desktop message identical by
/// construction with the phone and web, instead of a per-surface hand-written error string.
/// </summary>
public partial class HostedAiUnavailableDialog : Window
{
    private readonly HostedAiCtaAction _ctaAction;

    /// <summary>Parameterless constructor for the Avalonia designer/loader only.</summary>
    public HostedAiUnavailableDialog() : this(HostedAiState.NeedsCredits) { }

    /// <summary>Build the dialog for a specific unavailable state, filling the shared copy + call-to-action.</summary>
    public HostedAiUnavailableDialog(HostedAiState state)
    {
        InitializeComponent();
        var message = HostedAiMessages.For(state);
        _ctaAction = message.CtaAction;
        MessageText.Text = message.Text;
        CtaButton.Content = string.IsNullOrWhiteSpace(message.CtaLabel) ? "OK" : message.CtaLabel;
        // A state with no call-to-action (should not happen for the unavailable states) hides the button.
        CtaButton.IsVisible = message.CtaAction != HostedAiCtaAction.None;
        FileLog.Write($"[HostedAiUnavailableDialog] shown for state={state}, cta={message.CtaAction}");
    }

    private async void BtnCta_Click(object? sender, RoutedEventArgs e)
    {
        // Entry point (click handler): the try-catch lives here; the opener itself never throws out.
        try
        {
            await DesktopHostedAiCta.InvokeAsync(_ctaAction).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[HostedAiUnavailableDialog] BtnCta_Click FAILED: {ex.Message}");
        }
        Close(true);
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close(false);
}
