using System.Text.Json;

namespace CcDirector.Setup.Engine;

/// <summary>The ordered hops of a self-host provision. Order is the contract; do not reorder.</summary>
public enum SelfHostStep
{
    /// <summary>DevThrottle browser login; persists the LOCAL Gateway credential.</summary>
    SignIn = 0,

    /// <summary>Fetch, hash-check and place the independently-published Gateway asset.</summary>
    PlaceGatewayAsset = 1,

    /// <summary>Start the managed local Gateway and wait for it to answer.</summary>
    StartGateway = 2,

    /// <summary>Enroll this Director against the local Gateway for a device key.</summary>
    EnrollDirector = 3,
}

/// <summary>
/// The durable record of one self-host provision.
///
/// Two jobs, and the second is the one that matters most:
///
/// 1. **Resume.** A provision that dies at step three (a dropped network, a closed lid, a killed
///    process) must not restart from the browser login. On the next attempt each completed step is
///    skipped.
/// 2. **Ownership.** Rollback must only ever remove what THIS run created. If the machine already
///    had a Gateway installed, or was already signed in, a failed provision must leave those exactly
///    as it found them. Recording ownership per step is what makes a compensating rollback safe;
///    without it, "clean up after yourself" quietly means "delete the user's working Gateway".
/// </summary>
public sealed class SelfHostJournal
{
    /// <summary>Steps completed by any run, in order of completion.</summary>
    public List<SelfHostStep> Completed { get; set; } = [];

    /// <summary>Steps whose artifact THIS provision created and may therefore undo.</summary>
    public List<SelfHostStep> Owned { get; set; } = [];

    /// <summary>Set when a run gave up, so a later attempt can explain itself.</summary>
    public string? LastFailure { get; set; }

    public bool IsComplete(SelfHostStep step) => Completed.Contains(step);

    public bool Owns(SelfHostStep step) => Owned.Contains(step);

    /// <summary>
    /// Record a finished step. <paramref name="owned"/> is false when the artifact was ALREADY
    /// there and this run merely observed it - the case that must never be rolled back.
    /// </summary>
    public void MarkComplete(SelfHostStep step, bool owned)
    {
        if (!Completed.Contains(step))
            Completed.Add(step);
        if (owned && !Owned.Contains(step))
            Owned.Add(step);
    }

    /// <summary>Steps to compensate, newest first - undo runs in reverse order of creation.</summary>
    public IEnumerable<SelfHostStep> OwnedNewestFirst()
    {
        for (var i = Owned.Count - 1; i >= 0; i--)
            yield return Owned[i];
    }

    public void Forget(SelfHostStep step)
    {
        Completed.Remove(step);
        Owned.Remove(step);
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Read a journal. A missing or unreadable journal yields a FRESH one rather than throwing:
    /// a corrupt journal must cost the user a repeated step, never a dead connect screen. Nothing
    /// is deleted on this path, because a journal we cannot parse is also a journal whose ownership
    /// claims we cannot trust.
    /// </summary>
    public static SelfHostJournal FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new SelfHostJournal();

        try
        {
            return JsonSerializer.Deserialize<SelfHostJournal>(json) ?? new SelfHostJournal();
        }
        catch (JsonException)
        {
            return new SelfHostJournal();
        }
    }
}
