using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The one way this app puts text on the clipboard from a button: copy, say "Copied" on the button
/// itself for a moment, then go back to the original label. The button is disabled while it reads
/// "Copied" so a second click cannot race the restore.
/// </summary>
internal static class CopyToClipboard
{
    /// <summary>How long the button reads "Copied" before returning to its label.</summary>
    internal static TimeSpan Confirmation { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Say something on the button itself for a moment WITHOUT copying - used when the thing the
    /// button offered has stopped being true, so there is nothing honest to put on the clipboard.
    /// </summary>
    public static async Task FlashAsync(Button button, string message, string label)
    {
        button.Content = message;
        button.IsEnabled = false;
        try
        {
            await Task.Delay(Confirmation);
        }
        finally
        {
            button.Content = label;
            button.IsEnabled = true;
        }
    }

    /// <summary>
    /// Put text on the clipboard and confirm on the button. Throws if there is no clipboard or the
    /// write fails - the CALLING event handler catches and reports, per the repository's rule that
    /// try-catch lives in event handlers rather than helpers.
    /// </summary>
    /// <param name="confirmation">
    /// What the button reads on success. Defaults to "Copied"; callers pass something more specific
    /// when the copy carried a caveat the user should notice ("Copied - partial").
    /// </param>
    public static async Task RunAsync(Button button, string text, string label, string what, string confirmation = "Copied")
    {
        var clipboard = TopLevel.GetTopLevel(button)?.Clipboard
            ?? throw new InvalidOperationException($"No clipboard is available, so {what} could not be copied.");

        await clipboard.SetTextAsync(text);
        FileLog.Write($"[CopyToClipboard] copied {what} ({text.Length} chars)");
        await FlashAsync(button, confirmation, label);
    }
}
