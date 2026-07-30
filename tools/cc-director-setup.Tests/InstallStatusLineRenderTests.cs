using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CcDirectorSetup.Steps;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// The markup guard next door checks the ATTRIBUTES. This one renders the real Windows install step
/// and measures the actual failure message, because the defect in issue #1152 was something a person
/// SAW: the sentence stopped at "Check C:" and the log path - the only actionable part - was gone.
///
/// The message used here is the one from the incident, so the case is the real one rather than a
/// convenient long string. The test first proves the message genuinely does not fit on one line at
/// the wizard's width (otherwise it would pass while proving nothing), then proves the control wrapped
/// it instead of running off the edge.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class InstallStatusLineRenderTests
{
    private readonly WpfStaFixture _wpf;

    public InstallStatusLineRenderTests(WpfStaFixture wpf) => _wpf = wpf;

    /// <summary>The install step's content width inside the 900-pixel wizard window.</summary>
    private const double ContentWidth = 828;

    /// <summary>The failure text from the incident, with the log path that used to be cut off.</summary>
    private const string TheFailure =
        "ERROR: Launcher tray app failed to start. The Launcher tray app is still running as process "
        + "11216 but did not answer on port 7900 within 5 minutes. "
        + @"Check C:\Users\qa\AppData\Local\cc-director\logs.";

    [Fact]
    public void TheLauncherFailureMessage_WrapsInsteadOfBeingClippedAtTheWindowEdge()
    {
        _wpf.Run(() =>
        {
            var step = BuildRenderedStep(TheFailure, out var status);

            // The case has to be real: this message cannot fit on one line at the wizard's width.
            var oneLineWidth = UnwrappedWidth(status, TheFailure);
            Assert.True(oneLineWidth > ContentWidth,
                $"The message measures {oneLineWidth:F0}px on one line but the step is {ContentWidth}px wide, "
                + "so this test would pass without wrapping anything. Pick a message that really overflows.");

            // Wrapped, not clipped: it took more than one line and it stayed inside the step.
            var lineHeight = UnwrappedHeight(status);
            Assert.True(status.ActualHeight > lineHeight * 1.5,
                $"The status line rendered {status.ActualHeight:F0}px high - one line of {lineHeight:F0}px. "
                + "The message is being cut off at the window edge instead of wrapping (#1152).");
            Assert.True(status.ActualWidth <= ContentWidth + 1,
                $"The status line is {status.ActualWidth:F0}px wide inside a {ContentWidth}px step - it overflows.");

            GC.KeepAlive(step);
        });
    }

    /// <summary>
    /// The Repair button still sits beside the status line rather than under it, so making the text
    /// wrap did not quietly rearrange the screen for every other message.
    /// </summary>
    [Fact]
    public void AShortStatus_StillLeavesTheRepairButtonBesideIt()
    {
        _wpf.Run(() =>
        {
            var step = BuildRenderedStep("Preparing...", out var status);
            var repair = (Button)step.FindName("RepairButton");
            repair.Visibility = Visibility.Visible;
            step.UpdateLayout();

            var statusTop = status.TranslatePoint(new Point(0, 0), step).Y;
            var repairTop = repair.TranslatePoint(new Point(0, 0), step).Y;
            var repairLeft = repair.TranslatePoint(new Point(0, 0), step).X;

            Assert.True(Math.Abs(statusTop - repairTop) < repair.ActualHeight,
                "The Repair button dropped onto its own row instead of staying beside the status text.");
            Assert.True(repairLeft > status.ActualWidth,
                "The Repair button is no longer to the right of the status text.");
        });
    }

    private static InstallStep BuildRenderedStep(string statusText, out TextBlock status)
    {
        var step = new InstallStep();
        step.SetStatus(statusText);

        var host = new Border { Width = ContentWidth, Child = step };
        host.Measure(new Size(ContentWidth, 600));
        host.Arrange(new Rect(0, 0, ContentWidth, 600));
        host.UpdateLayout();

        status = (TextBlock)step.FindName("StatusText");
        return step;
    }

    /// <summary>How wide this text would be if nothing wrapped it.</summary>
    private static double UnwrappedWidth(TextBlock like, string text) => Format(like, text).Width;

    /// <summary>The height of a single line in this control's typeface and size.</summary>
    private static double UnwrappedHeight(TextBlock like) => Format(like, "Ag").Height;

    private static FormattedText Format(TextBlock like, string text) =>
        new(text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(like.FontFamily, like.FontStyle, like.FontWeight, like.FontStretch),
            like.FontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(like).PixelsPerDip);
}
