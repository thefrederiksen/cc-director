namespace CcDirector.Gateway.Transcription;

/// <summary>
/// Turns a tenant's raw microphone measurements into the finished verdict the Cockpit renders.
///
/// THE CLIENT IS DUMB (CLAUDE.md #7). Every judgement here - which microphone is worst, whether it is
/// worth telling the user about, and the sentence that says so - is decided ONCE, on the Gateway, and
/// stamped onto the response. The Cockpit lays it out; it never re-derives what a state means. Adding
/// a new verdict is an edit here, not a new branch in a React component.
///
/// WHAT IT DELIBERATELY DOES NOT DO: nag. Measured against 212 real dictations from a healthy setup,
/// reporting every imperfection would have flagged about one dictation in nine - which is the rate
/// that teaches somebody to ignore the screen. So a microphone is only ever called BAD on the two
/// defects that are unambiguous and worth acting on (a band-limited link, and real distortion), and
/// the softer readings are shown as measurements rather than as complaints.
/// </summary>
public static class MicrophoneQualityFold
{
    /// <summary>Below this a device has not been used enough for its average to mean anything, and a
    /// verdict on two clips would swing wildly with the next one.</summary>
    public const int MinSamplesForVerdict = 5;

    /// <summary>A device is called band-limited only when MOST of its dictation is. One narrowband
    /// reading can be a Bluetooth link that connected in hands-free mode for a single call; a
    /// majority is the headset itself.</summary>
    public const double NarrowbandShareToWarn = 0.5;

    /// <summary>Distortion is a fault at a much lower share than band-limiting, because it is caused
    /// by a gain setting that will keep doing it, and it is trivially fixable.</summary>
    public const double ClippingShareToWarn = 0.2;

    public static MicrophoneQualitySummary Summarize(IReadOnlyList<MicrophoneQualityRecord> records)
    {
        if (records is null || records.Count == 0)
        {
            return new MicrophoneQualitySummary
            {
                TotalSamples = 0,
                Headline = "No dictation has been measured yet.",
                Detail = "Dictate something and your microphone quality will appear here automatically.",
                Status = "empty",
                Devices = Array.Empty<MicrophoneDeviceSummary>(),
            };
        }

        var devices = records
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Device) ? "Unnamed microphone" : r.Device)
            .Select(BuildDevice)
            .OrderByDescending(d => d.Samples)
            .ToList();

        // The headline speaks about the WORST device that has earned a verdict, not the average of
        // everything. Averaging a bad headset with a good desk microphone hides exactly the finding
        // the user needs: that one of their microphones is the problem.
        var judged = devices.Where(d => d.Samples >= MinSamplesForVerdict).ToList();
        var worst = judged.FirstOrDefault(d => d.Status == "bad");

        string headline;
        string detail;
        string status;
        if (worst is not null)
        {
            status = "bad";
            headline = $"{worst.Device} is holding your transcription back.";
            detail = worst.Advice;
        }
        else if (judged.Count == 0)
        {
            status = "learning";
            headline = "Still learning how your microphones sound.";
            detail =
                $"A microphone needs {MinSamplesForVerdict} measured dictations before this screen will "
                + "judge it, so a single odd recording never accuses good hardware.";
        }
        else
        {
            status = "good";
            headline = judged.Count == 1
                ? "Your microphone sounds good."
                : $"All {judged.Count} of your microphones sound good.";
            detail = "Nothing about your audio is holding transcription back. The numbers below are the evidence.";
        }

        return new MicrophoneQualitySummary
        {
            TotalSamples = records.Count,
            Headline = headline,
            Detail = detail,
            Status = status,
            Devices = devices,
        };
    }

    private static MicrophoneDeviceSummary BuildDevice(IGrouping<string, MicrophoneQualityRecord> group)
    {
        var list = group.ToList();
        var n = list.Count;
        var narrowbandShare = (double)list.Count(r => r.Narrowband) / n;
        var clippingShare = (double)list.Count(r => r.ClippedFraction >= 0.001) / n;

        var status = "good";
        var advice = "";
        if (n >= MinSamplesForVerdict && narrowbandShare >= NarrowbandShareToWarn)
        {
            status = "bad";
            advice =
                "It is sending telephone-quality audio, which strips out the consonants the transcriber "
                + "needs. This is almost always a Bluetooth headset running in hands-free mode. Switch it "
                + "to its headphones profile and pick a different microphone, use its USB dongle instead "
                + "of Bluetooth, or use a wired microphone. This one change usually matters more than "
                + "everything else on this page put together.";
        }
        else if (n >= MinSamplesForVerdict && clippingShare >= ClippingShareToWarn)
        {
            status = "bad";
            advice =
                "Its audio is distorting, which means the input level is too high and the loudest parts "
                + "are being cut flat. Move it further from your mouth, or turn the input level down in "
                + "your operating system's sound settings.";
        }
        else if (n < MinSamplesForVerdict)
        {
            status = "learning";
            advice = $"Measured {n} times so far; {MinSamplesForVerdict} are needed before this is judged.";
        }
        else
        {
            advice = "Nothing wrong with this microphone.";
        }

        return new MicrophoneDeviceSummary
        {
            Device = group.Key,
            Samples = n,
            Status = status,
            Advice = advice,
            NarrowbandShare = Round(narrowbandShare),
            ClippingShare = Round(clippingShare),
            MedianSpeechLevelDb = Round(Median(list.Select(r => r.SpeechLevelDb))),
            MedianSignalToNoiseDb = Round(Median(list.Select(r => r.SignalToNoiseDb))),
            LastSeenUtc = list.Max(r => r.TimestampUtc),
            // What good looks like, carried next to the reading so the Cockpit compares rather than
            // just displays. Folded here so the two can never drift apart.
            TargetSpeechLevelDb = TargetSpeechLevelDb,
            TargetSignalToNoiseDb = TargetSignalToNoiseDb,
        };
    }

    /// <summary>
    /// Rank a tenant's microphones best first, so a report can say WHICH ONE to prefer rather than only
    /// that one of them is bad. Devices with too few measurements are excluded entirely - ranking a
    /// device on two recordings would recommend hardware on noise.
    ///
    /// The order is by how much each defect actually costs a transcript, which is not the order their
    /// numbers suggest:
    ///   1. Band-limiting first, and by a distance. It removes the consonants outright, and no amount of
    ///      level or quiet compensates for information that is not in the signal.
    ///   2. Distortion second. It corrupts what IS there, but the vowels usually survive it.
    ///   3. Then how far the voice stands above the room, capped - past the target, more is not better,
    ///      and without the cap a silent room would outrank a better microphone.
    ///   4. Then closeness to a healthy level, as a tie-break only.
    /// A single number is returned rather than a sort over several keys so the reason for an ordering can
    /// be stated, and so two devices that differ only trivially do not flip places between reports.
    /// </summary>
    public static IReadOnlyList<MicrophoneDeviceSummary> RankBest(IReadOnlyList<MicrophoneDeviceSummary> devices)
        => devices is null
            ? Array.Empty<MicrophoneDeviceSummary>()
            : devices
                .Where(d => d.Samples >= MinSamplesForVerdict)
                .OrderByDescending(ComparableScore)
                .ThenByDescending(d => d.Samples)
                .ThenBy(d => d.Device, StringComparer.Ordinal)
                .ToList();

    /// <summary>
    /// How good a microphone is, 0..100, for ORDERING devices against each other. Deliberately not shown
    /// to anyone: a score invites "why is it 71" and the honest answer is the four measurements it came
    /// from, which the report shows instead.
    /// </summary>
    public static double ComparableScore(MicrophoneDeviceSummary d)
    {
        if (d is null) return 0;
        var score = 100.0;
        score -= 60 * d.NarrowbandShare;
        score -= 25 * d.ClippingShare;

        // Distance below the signal-to-noise target, in decibels, worth a point each. Above the target
        // costs nothing: a quieter room stops helping once the voice is already clear of it.
        var snrShortfall = Math.Max(0, TargetSignalToNoiseDb - d.MedianSignalToNoiseDb);
        score -= Math.Min(10, snrShortfall);

        // Level is a tie-break: it is the easiest thing for a user to fix and the least damaging when
        // wrong, so it must never reorder two microphones that differ on anything above.
        score -= Math.Min(5, Math.Abs(TargetSpeechLevelDb - d.MedianSpeechLevelDb) / 4);
        return Math.Max(0, score);
    }

    /// <summary>A healthy speaking level: loud enough that the encoder spends its bits on the voice.</summary>
    public const double TargetSpeechLevelDb = -20;

    /// <summary>Where a voice stands clear of the room well enough for the model not to struggle.</summary>
    public const double TargetSignalToNoiseDb = 20;

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static double Round(double value) => Math.Round(value, 2);
}

/// <summary>The finished microphone-quality verdict, rendered verbatim by the Cockpit.</summary>
public sealed record MicrophoneQualitySummary
{
    public required int TotalSamples { get; init; }
    /// <summary>"empty", "learning", "good" or "bad" - drives the banner colour only.</summary>
    public required string Status { get; init; }
    public required string Headline { get; init; }
    public required string Detail { get; init; }
    public required IReadOnlyList<MicrophoneDeviceSummary> Devices { get; init; }
}

/// <summary>How one microphone is doing, with what good looks like carried alongside.</summary>
public sealed record MicrophoneDeviceSummary
{
    public required string Device { get; init; }
    public required int Samples { get; init; }
    /// <summary>"good", "learning" or "bad".</summary>
    public required string Status { get; init; }
    public required string Advice { get; init; }
    public required double NarrowbandShare { get; init; }
    public required double ClippingShare { get; init; }
    public required double MedianSpeechLevelDb { get; init; }
    public required double MedianSignalToNoiseDb { get; init; }
    public required double TargetSpeechLevelDb { get; init; }
    public required double TargetSignalToNoiseDb { get; init; }
    public required DateTime LastSeenUtc { get; init; }
}
