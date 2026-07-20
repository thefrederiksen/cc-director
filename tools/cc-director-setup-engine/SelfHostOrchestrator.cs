namespace CcDirector.Setup.Engine;

/// <summary>The outcome of one step attempt. <paramref name="Owned"/> drives rollback safety.</summary>
/// <param name="Success">Whether the step's artifact is now in place.</param>
/// <param name="Owned">
/// True only when THIS attempt created the artifact. False when it was already there - an
/// already-signed-in machine, an already-installed Gateway - which must never be rolled back.
/// </param>
/// <param name="Message">Plain-English detail for the screen and the log.</param>
public sealed record SelfHostStepResult(bool Success, bool Owned, string Message)
{
    public static SelfHostStepResult Created(string message) => new(true, true, message);
    public static SelfHostStepResult AlreadyThere(string message) => new(true, false, message);
    public static SelfHostStepResult Failed(string message) => new(false, false, message);
}

/// <summary>The steps the orchestrator drives, injected so every path is testable off a real machine.</summary>
public sealed class SelfHostSteps
{
    public required Func<CancellationToken, Task<SelfHostStepResult>> SignIn { get; init; }
    public required Func<CancellationToken, Task<SelfHostStepResult>> PlaceGatewayAsset { get; init; }
    public required Func<CancellationToken, Task<SelfHostStepResult>> StartGateway { get; init; }
    public required Func<CancellationToken, Task<SelfHostStepResult>> EnrollDirector { get; init; }

    /// <summary>
    /// Probe whether inference is ready (the runtime auto-mints the dt_live_ key). BEST-EFFORT and
    /// deliberately NOT a success condition - a healthy Gateway is not the same thing as a ready
    /// inference path, and conflating them is how a green screen ends up next to an agent that
    /// cannot think.
    /// </summary>
    public required Func<CancellationToken, Task<bool>> ProbeInferenceReady { get; init; }

    /// <summary>Compensating undo per step. Called ONLY for steps this run owns, newest first.</summary>
    public required Func<SelfHostStep, CancellationToken, Task> Compensate { get; init; }
}

/// <summary>What happened, in a shape a screen can render without deciding anything itself.</summary>
public sealed record SelfHostResult(
    bool Success,
    string Message,
    IReadOnlyList<string> Steps,
    bool InferenceReady,
    bool RolledBack,
    bool Cancelled);

/// <summary>
/// The connect-time self-host orchestrator: sign in, place the Gateway, start it, enroll this
/// Director - in that order, resumable, cancellable, and safe to abandon.
///
/// Why this is not a happy path. Provisioning a Gateway touches four things that outlive the
/// process: a credential, a binary, a running service with an autostart key, and a device
/// enrolment. A sequence that fails at step three and simply reports an error leaves a stranger's
/// machine half-provisioned, at the exact moment they are deciding whether the product works. So
/// every hop is idempotent (safe to re-run), the journal lets the next attempt resume rather than
/// restart, and failure compensates in reverse - but ONLY for artifacts this run created.
///
/// The one deliberate asymmetry: inference readiness is probed and reported, never gating. The
/// runtime mints the dt_live_ key on its own schedule; making the connect flow wait on it would
/// turn a working Gateway into a failed provision.
/// </summary>
public sealed class SelfHostOrchestrator
{
    private readonly SelfHostSteps _steps;
    private readonly Func<string?> _readJournal;
    private readonly Action<string> _writeJournal;
    private readonly Action<string> _progress;

    public SelfHostOrchestrator(
        SelfHostSteps steps,
        Func<string?> readJournal,
        Action<string> writeJournal,
        Action<string>? progress = null)
    {
        _steps = steps ?? throw new ArgumentNullException(nameof(steps));
        _readJournal = readJournal ?? throw new ArgumentNullException(nameof(readJournal));
        _writeJournal = writeJournal ?? throw new ArgumentNullException(nameof(writeJournal));
        _progress = progress ?? (_ => { });
    }

    /// <summary>The user-facing label for each hop. One place, so the screen renders and never decides.</summary>
    public static string Describe(SelfHostStep step) => step switch
    {
        SelfHostStep.SignIn => "Signing in to DevThrottle",
        SelfHostStep.PlaceGatewayAsset => "Downloading and verifying the Gateway",
        SelfHostStep.StartGateway => "Starting your Gateway",
        SelfHostStep.EnrollDirector => "Connecting this Director to it",
        _ => step.ToString(),
    };

    public async Task<SelfHostResult> RunAsync(CancellationToken ct = default)
    {
        var journal = SelfHostJournal.FromJson(_readJournal());
        var log = new List<string>();

        var order = new (SelfHostStep Step, Func<CancellationToken, Task<SelfHostStepResult>> Run)[]
        {
            (SelfHostStep.SignIn, _steps.SignIn),
            (SelfHostStep.PlaceGatewayAsset, _steps.PlaceGatewayAsset),
            (SelfHostStep.StartGateway, _steps.StartGateway),
            (SelfHostStep.EnrollDirector, _steps.EnrollDirector),
        };

        foreach (var (step, run) in order)
        {
            if (ct.IsCancellationRequested)
                return await AbandonAsync(journal, log, "Cancelled.", cancelled: true);

            if (journal.IsComplete(step))
            {
                // Resume: a step this or an earlier run already finished is not repeated. This is
                // why a dropped connection does not send the user back to the browser login.
                var note = $"{Describe(step)}: already done, skipping.";
                log.Add(note);
                _progress(note);
                continue;
            }

            _progress($"{Describe(step)}...");

            SelfHostStepResult result;
            try
            {
                result = await run(ct);
            }
            catch (OperationCanceledException)
            {
                return await AbandonAsync(journal, log, "Cancelled.", cancelled: true);
            }
            catch (Exception ex)
            {
                // A step that throws is a failed step, not a crashed connect screen.
                result = SelfHostStepResult.Failed(ex.Message);
            }

            if (!result.Success)
            {
                journal.LastFailure = $"{Describe(step)}: {result.Message}";
                log.Add(journal.LastFailure);
                return await AbandonAsync(journal, log, journal.LastFailure, cancelled: false);
            }

            journal.MarkComplete(step, result.Owned);
            _writeJournal(journal.ToJson());
            log.Add($"{Describe(step)}: {result.Message}");
            _progress($"{Describe(step)}: done.");
        }

        journal.LastFailure = null;
        _writeJournal(journal.ToJson());

        // Best-effort, never gating: a Gateway that is up but whose inference key has not been
        // minted yet is a SUCCESSFUL provision with one capability still warming up.
        var inferenceReady = false;
        try
        {
            inferenceReady = await _steps.ProbeInferenceReady(ct);
        }
        catch (Exception ex)
        {
            log.Add($"Inference readiness could not be checked yet: {ex.Message}");
        }

        log.Add(inferenceReady
            ? "Inference is ready."
            : "Your Gateway is running. Inference is still warming up - it will be ready shortly.");

        return new SelfHostResult(
            Success: true,
            Message: "Your Gateway is running and this Director is connected to it.",
            Steps: log,
            InferenceReady: inferenceReady,
            RolledBack: false,
            Cancelled: false);
    }

    /// <summary>
    /// Give up cleanly: undo ONLY what this run created, newest first, then persist what survived
    /// so the next attempt resumes from the right place.
    ///
    /// A compensation that itself fails is logged and does not stop the rest - the user is already
    /// in a failure path and stopping halfway through the cleanup would leave MORE behind, not less.
    /// </summary>
    private async Task<SelfHostResult> AbandonAsync(
        SelfHostJournal journal, List<string> log, string message, bool cancelled)
    {
        var rolledBack = false;

        foreach (var step in journal.OwnedNewestFirst().ToList())
        {
            try
            {
                _progress($"Undoing: {Describe(step)}...");
                // CancellationToken.None deliberately: cleanup must finish even when the reason we
                // are here is that the user cancelled.
                await _steps.Compensate(step, CancellationToken.None);
                journal.Forget(step);
                log.Add($"Undid {Describe(step)}.");
                rolledBack = true;
            }
            catch (Exception ex)
            {
                log.Add($"Could not undo {Describe(step)}: {ex.Message}");
            }
        }

        _writeJournal(journal.ToJson());

        return new SelfHostResult(
            Success: false,
            Message: message,
            Steps: log,
            InferenceReady: false,
            RolledBack: rolledBack,
            Cancelled: cancelled);
    }
}
