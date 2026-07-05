using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace CcDirectorClient.Voice;

/// <summary>
/// A hosted-AI call returned HTTP 402 - out of credits, monthly cap, or (in bring-your-own mode) no key
/// (issue #943, epic #937). It carries the ONE shared message + call-to-action the server already put in
/// the shared 402 body (<c>{ error, state, text, ctaLabel, ctaAction, ctaUrl }</c>), so the phone shows
/// the identical "add credits" message + Billing link as the desktop, web, and mobile surfaces - instead
/// of a raw "tts failed: 402 {json}". The phone parses the server's body rather than recomputing the
/// copy, so there is nothing to keep in step.
/// </summary>
public sealed class HostedAiUnavailableException : Exception
{
    public string State { get; }
    public string CtaLabel { get; }
    public string? CtaUrl { get; }

    public HostedAiUnavailableException(string state, string text, string ctaLabel, string? ctaUrl)
        : base(text)
    {
        State = state;
        CtaLabel = ctaLabel;
        CtaUrl = ctaUrl;
    }

    /// <summary>
    /// When <paramref name="status"/> is 402, parse the shared body into a typed exception; otherwise
    /// null (the caller keeps its existing error). Defaults keep the message sensible if a field is
    /// missing or the body is not the shared shape.
    /// </summary>
    public static HostedAiUnavailableException? TryFrom(int status, string body)
    {
        if (status != 402) return null;

        var state = "NeedsCredits";
        var text = "Voice needs credit. Add credits to turn it on.";
        var ctaLabel = "Add credits";
        string? ctaUrl = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String)
                state = s.GetString() ?? state;
            if (root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                text = t.GetString() ?? text;
            else if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                text = e.GetString() ?? text;
            if (root.TryGetProperty("ctaLabel", out var cl) && cl.ValueKind == JsonValueKind.String)
                ctaLabel = cl.GetString() ?? ctaLabel;
            if (root.TryGetProperty("ctaUrl", out var cu) && cu.ValueKind == JsonValueKind.String)
                ctaUrl = cu.GetString();
        }
        catch (JsonException)
        {
            // A non-JSON 402 body still means out of credits; keep the sensible defaults.
        }
        return new HostedAiUnavailableException(state, text, ctaLabel, ctaUrl);
    }
}

/// <summary>
/// Shows the shared out-of-credits message on a page with an "Add credits" action that opens the Billing
/// page (issue #943). Reused by every phone voice surface so the out-of-credits prompt is identical.
/// </summary>
public static class HostedAiPrompt
{
    public static async Task ShowAsync(Page page, HostedAiUnavailableException ex)
    {
        if (page is null) return;

        // No call-to-action URL: just show the message. With a URL: offer to open Billing.
        if (string.IsNullOrWhiteSpace(ex.CtaUrl))
        {
            await page.DisplayAlert("Voice unavailable", ex.Message, "OK");
            return;
        }

        var openCta = await page.DisplayAlert("Voice unavailable", ex.Message, ex.CtaLabel, "Not now");
        if (openCta)
        {
            try
            {
                await Launcher.Default.OpenAsync(new Uri(ex.CtaUrl));
            }
            catch (Exception e)
            {
                ClientLog.Write($"[HostedAiPrompt] open call-to-action failed: {e.Message}");
            }
        }
    }
}
