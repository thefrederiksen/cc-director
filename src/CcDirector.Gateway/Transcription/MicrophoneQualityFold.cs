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

    /// <summary>The clipped-fraction floor above which one recording COUNTS as distorted. One rule,
    /// used by every share computed anywhere in this fold, so a device summary and its own trend
    /// points can never disagree about the same recording.</summary>
    public const double ClippedFractionCounts = 0.001;

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

        var devices = GroupByDevice(records)
            .Select(BuildDevice)
            .OrderByDescending(d => d.Samples)
            .ToList();

        var (status, headline, detail) = Verdict(devices);
        return new MicrophoneQualitySummary
        {
            TotalSamples = records.Count,
            Headline = headline,
            Detail = detail,
            Status = status,
            Devices = devices,
        };
    }

    /// <summary>
    /// The whole-account verdict over the folded devices. The headline speaks about the WORST device
    /// that has earned a verdict, not the average of everything: averaging a bad headset with a good
    /// desk microphone hides exactly the finding the user needs - that one of their microphones is
    /// the problem. Shared by the summary and the detail views so the two can never disagree.
    /// </summary>
    private static (string Status, string Headline, string Detail) Verdict(IReadOnlyList<MicrophoneDeviceSummary> devices)
    {
        var judged = devices.Where(d => d.Samples >= MinSamplesForVerdict).ToList();
        var worst = judged.FirstOrDefault(d => d.Status == "bad");

        if (worst is not null)
            return ("bad", $"{worst.Device} is holding your transcription back.", worst.Advice);

        if (judged.Count == 0)
        {
            return ("learning", "Still learning how your microphones sound.",
                $"A microphone needs {MinSamplesForVerdict} measured dictations before this screen will "
                + "judge it, so a single odd recording never accuses good hardware.");
        }

        var headline = judged.Count == 1
            ? "Your microphone sounds good."
            : $"All {judged.Count} of your microphones sound good.";
        return ("good", headline, "Nothing about your audio is holding transcription back. The numbers below are the evidence.");
    }

    /// <summary>
    /// How many individual measurements the detail view carries per device. Thirty days of heavy
    /// dictating fits well under this; when a device does exceed it the response SAYS so
    /// (<see cref="MicrophoneDeviceDetail.MeasurementsTotal"/> versus the list length) rather than
    /// truncating silently.
    /// </summary>
    public const int MaxDetailMeasurements = 200;

    /// <summary>
    /// The DETAILED per-device picture the Cockpit's Transcription Health page renders: the same
    /// verdicts as <see cref="Summarize"/> - the two share every fold, so they cannot disagree -
    /// plus, per device, the daily quality-over-time series and the individual measurements behind
    /// it. Still no audio and no transcript: every field is a number, a date, or a verdict string.
    /// </summary>
    public static MicrophoneQualityDetail Detail(IReadOnlyList<MicrophoneQualityRecord> records)
    {
        if (records is null || records.Count == 0)
        {
            var empty = Summarize(records ?? Array.Empty<MicrophoneQualityRecord>());
            return new MicrophoneQualityDetail
            {
                TotalSamples = 0,
                Status = empty.Status,
                Headline = empty.Headline,
                Detail = empty.Detail,
                Devices = Array.Empty<MicrophoneDeviceDetail>(),
            };
        }

        var built = GroupByDevice(records)
            .Select(group => (Summary: BuildDevice(group), Records: group))
            .OrderByDescending(pair => pair.Summary.Samples)
            .ToList();

        var (status, headline, detail) = Verdict(built.Select(p => p.Summary).ToList());
        return new MicrophoneQualityDetail
        {
            TotalSamples = records.Count,
            Status = status,
            Headline = headline,
            Detail = detail,
            Devices = built.Select(p => BuildDeviceDetail(p.Summary, p.Records)).ToList(),
        };
    }

    private static MicrophoneDeviceDetail BuildDeviceDetail(
        MicrophoneDeviceSummary summary,
        IReadOnlyList<MicrophoneQualityRecord> group)
    {
        var newestFirst = group.OrderByDescending(r => r.TimestampUtc).ToList();

        // The over-time series: one point per calendar day with data, oldest first so a chart reads
        // left to right. Medians per day for the same reason the device summary uses them - one odd
        // recording must not move the line.
        var trend = newestFirst
            .GroupBy(r => r.TimestampUtc.Date)
            .OrderBy(g => g.Key)
            .Select(g => new MicrophoneTrendPoint
            {
                Date = g.Key.ToString("yyyy-MM-dd"),
                Samples = g.Count(),
                MedianSpeechLevelDb = Round(Median(g.Select(r => r.SpeechLevelDb))),
                MedianSignalToNoiseDb = Round(Median(g.Select(r => r.SignalToNoiseDb))),
                NarrowbandShare = Round((double)g.Count(r => r.Narrowband) / g.Count()),
                ClippingShare = Round((double)g.Count(r => r.ClippedFraction >= ClippedFractionCounts) / g.Count()),
            })
            .ToList();

        return new MicrophoneDeviceDetail
        {
            Summary = summary,
            PlatformRaw = newestFirst.Select(r => r.PlatformRaw).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "",
            Trend = trend,
            MeasurementsTotal = newestFirst.Count,
            Measurements = newestFirst.Take(MaxDetailMeasurements).Select(r => new MicrophoneMeasurement
            {
                TimestampUtc = r.TimestampUtc,
                Source = r.Source,
                DurationSeconds = r.DurationSeconds,
                SampleRate = r.SampleRate,
                SpeechLevelDb = r.SpeechLevelDb,
                NoiseFloorDb = r.NoiseFloorDb,
                SignalToNoiseDb = r.SignalToNoiseDb,
                ClippedFraction = r.ClippedFraction,
                Narrowband = r.Narrowband,
                Rating = r.Rating,
                Issues = r.Issues,
            }).ToList(),
        };
    }

    /// <summary>
    /// One physical microphone = one group. The grouping key is the stable deviceId when the client
    /// sent one - the display name is metadata that a driver update or an operating system language
    /// change rewrites, and a grouping keyed on it silently splits one microphone into two histories
    /// (issue #2183). Records from before the id existed carry none; they are folded INTO the id
    /// group whose display name matches theirs when exactly one does, so shipping the id does not
    /// reset every device's earned history to "learning". A nameless, idless record still lands
    /// under "Unnamed microphone".
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<MicrophoneQualityRecord>> GroupByDevice(
        IReadOnlyList<MicrophoneQualityRecord> records)
    {
        static string DisplayLabel(MicrophoneQualityRecord r)
            => string.IsNullOrWhiteSpace(r.Device) ? "Unnamed microphone" : r.Device.Trim();

        var byId = records
            .Where(r => !string.IsNullOrWhiteSpace(r.DeviceId))
            .GroupBy(r => r.DeviceId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Which id-group does a given display name belong to? Only an UNAMBIGUOUS name may adopt
        // legacy records - two ids sharing one name is exactly the case the id exists to separate.
        var idGroupByLabel = byId.Values
            .SelectMany(list => list.Select(DisplayLabel).Distinct().Select(label => (label, list)))
            .GroupBy(pair => pair.label)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().list);

        var labelGroups = new Dictionary<string, List<MicrophoneQualityRecord>>();
        foreach (var record in records.Where(r => string.IsNullOrWhiteSpace(r.DeviceId)))
        {
            var label = DisplayLabel(record);
            if (idGroupByLabel.TryGetValue(label, out var adoptedBy))
            {
                adoptedBy.Add(record);
                continue;
            }
            if (!labelGroups.TryGetValue(label, out var group))
            {
                group = new List<MicrophoneQualityRecord>();
                labelGroups[label] = group;
            }
            group.Add(record);
        }

        return byId.Values.Concat(labelGroups.Values).ToList();
    }

    /// <summary>The platform buckets the fold recognises. Anything else - including the empty value
    /// on records from before the field existed - is reported as unknown rather than guessed.</summary>
    public static string NormalizePlatform(string? platform) => platform switch
    {
        "mobile" or "mac" or "windows" => platform,
        _ => "unknown",
    };

    /// <summary>The finished display string for a platform bucket. Empty for unknown, so a screen
    /// shows nothing rather than the word "unknown" next to a working microphone.</summary>
    public static string PlatformLabel(string platform) => platform switch
    {
        "mobile" => "Phone or tablet",
        "mac" => "Mac",
        "windows" => "Windows",
        _ => "",
    };

    private static MicrophoneDeviceSummary BuildDevice(IReadOnlyList<MicrophoneQualityRecord> group)
    {
        var list = group.OrderByDescending(r => r.TimestampUtc).ToList();
        var n = list.Count;
        // The newest values win for display metadata: the latest name is the one the operating
        // system currently uses, and the latest platform is where the microphone now lives.
        var label = list.Select(r => r.Device).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d))?.Trim()
                    ?? "Unnamed microphone";
        var deviceId = list.Select(r => r.DeviceId).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)) ?? "";
        var platform = NormalizePlatform(list.Select(r => r.Platform)
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)));
        var narrowbandShare = (double)list.Count(r => r.Narrowband) / n;
        var clippingShare = (double)list.Count(r => r.ClippedFraction >= ClippedFractionCounts) / n;

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
            Device = label,
            DeviceId = deviceId,
            Platform = platform,
            PlatformLabel = PlatformLabel(platform),
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
    /// <summary>The stable identifier the group was keyed on. Empty when only legacy or idless
    /// records exist for this device.</summary>
    public string DeviceId { get; init; } = "";
    /// <summary>"mobile", "mac", "windows" or "unknown".</summary>
    public string Platform { get; init; } = "unknown";
    /// <summary>The finished display string for the platform ("Phone or tablet", "Mac", "Windows").
    /// Empty when the platform is unknown, so screens render nothing rather than a guess.</summary>
    public string PlatformLabel { get; init; } = "";
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

/// <summary>The detailed per-device picture for the Cockpit's Transcription Health page: the same
/// verdicts as <see cref="MicrophoneQualitySummary"/>, plus each device's over-time series and the
/// measurements behind it. Rendered verbatim by the Cockpit.</summary>
public sealed record MicrophoneQualityDetail
{
    public required int TotalSamples { get; init; }
    /// <summary>"empty", "learning", "good" or "bad" - drives the banner colour only.</summary>
    public required string Status { get; init; }
    public required string Headline { get; init; }
    public required string Detail { get; init; }
    public required IReadOnlyList<MicrophoneDeviceDetail> Devices { get; init; }
}

/// <summary>One microphone, in full: its folded verdict, where it lives, how its quality has moved
/// over time, and every measurement inside the window.</summary>
public sealed record MicrophoneDeviceDetail
{
    /// <summary>The same folded verdict the summary view shows for this device.</summary>
    public required MicrophoneDeviceSummary Summary { get; init; }

    /// <summary>The raw evidence behind the platform bucket, so a wrong bucket can be diagnosed.</summary>
    public string PlatformRaw { get; init; } = "";

    /// <summary>One point per calendar day with data, oldest first, for the quality-over-time chart.</summary>
    public required IReadOnlyList<MicrophoneTrendPoint> Trend { get; init; }

    /// <summary>How many measurements the window really holds for this device. When it exceeds the
    /// length of <see cref="Measurements"/>, the list was capped at the newest
    /// <see cref="MicrophoneQualityFold.MaxDetailMeasurements"/> - the count is how a screen says so
    /// instead of silently pretending the cap is the total.</summary>
    public required int MeasurementsTotal { get; init; }

    /// <summary>The individual measurements, newest first, capped. No audio, no transcript.</summary>
    public required IReadOnlyList<MicrophoneMeasurement> Measurements { get; init; }
}

/// <summary>One day of one device's dictation, folded to medians and shares.</summary>
public sealed record MicrophoneTrendPoint
{
    /// <summary>The calendar day, UTC, as yyyy-MM-dd.</summary>
    public required string Date { get; init; }
    public required int Samples { get; init; }
    public required double MedianSpeechLevelDb { get; init; }
    public required double MedianSignalToNoiseDb { get; init; }
    public required double NarrowbandShare { get; init; }
    public required double ClippingShare { get; init; }
}

/// <summary>One dictation's measurement as the detail view shows it - how the audio sounded, never
/// what was said.</summary>
public sealed record MicrophoneMeasurement
{
    public required DateTime TimestampUtc { get; init; }
    public required string Source { get; init; }
    public required double DurationSeconds { get; init; }
    public required int SampleRate { get; init; }
    public required double SpeechLevelDb { get; init; }
    public required double NoiseFloorDb { get; init; }
    public required double SignalToNoiseDb { get; init; }
    public required double ClippedFraction { get; init; }
    public required bool Narrowband { get; init; }
    public required string Rating { get; init; }
    public required string Issues { get; init; }
}
