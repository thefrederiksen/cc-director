using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The self-host transaction. Every path here is exercised without touching a real machine: no
/// Gateway is installed, no process is started, nothing is enrolled. What these prove is the
/// TRANSACTION - ordering, resume, ownership-safe rollback, cancellation, and the deliberate
/// refusal to treat inference readiness as success.
///
/// What they do NOT prove is that the real steps work on a real machine. That needs an install run
/// on an isolated box and is stated as unproven on the pull request.
/// </summary>
public class SelfHostOrchestratorTests
{
    private sealed class Harness
    {
        public List<string> Calls { get; } = [];
        public List<SelfHostStep> Compensated { get; } = [];
        public string? Journal { get; set; }
        public bool InferenceReady { get; set; } = true;

        public Dictionary<SelfHostStep, Func<SelfHostStepResult>> Behaviour { get; } = [];

        public SelfHostSteps Steps(CancellationTokenSource? cancelOn = null, SelfHostStep? cancelAt = null)
        {
            Func<SelfHostStep, Func<CancellationToken, Task<SelfHostStepResult>>> make = step => _ =>
            {
                Calls.Add(step.ToString());
                if (cancelOn is not null && cancelAt == step)
                    cancelOn.Cancel();
                var behaviour = Behaviour.TryGetValue(step, out var b)
                    ? b
                    : () => SelfHostStepResult.Created("done");
                return Task.FromResult(behaviour());
            };

            return new SelfHostSteps
            {
                SignIn = make(SelfHostStep.SignIn),
                PlaceGatewayAsset = make(SelfHostStep.PlaceGatewayAsset),
                StartGateway = make(SelfHostStep.StartGateway),
                EnrollDirector = make(SelfHostStep.EnrollDirector),
                ProbeInferenceReady = _ => Task.FromResult(InferenceReady),
                Compensate = (step, _) => { Compensated.Add(step); return Task.CompletedTask; },
            };
        }

        public SelfHostOrchestrator Build(CancellationTokenSource? cancelOn = null, SelfHostStep? cancelAt = null)
            => new(Steps(cancelOn, cancelAt), () => Journal, j => Journal = j);
    }

    [Fact]
    public async Task RunAsync_HappyPath_RunsEveryStepInOrder()
    {
        var h = new Harness();

        var result = await h.Build().RunAsync();

        Assert.True(result.Success);
        Assert.Equal(
            ["SignIn", "PlaceGatewayAsset", "StartGateway", "EnrollDirector"],
            h.Calls);
        Assert.Empty(h.Compensated);
    }

    [Fact]
    public async Task RunAsync_StepFails_LaterStepsNeverRun()
    {
        var h = new Harness();
        h.Behaviour[SelfHostStep.PlaceGatewayAsset] = () => SelfHostStepResult.Failed("no network");

        var result = await h.Build().RunAsync();

        Assert.False(result.Success);
        Assert.Contains("no network", result.Message);
        // Starting a Gateway we never placed, or enrolling against one that is not running, would
        // turn one clear failure into a confusing one.
        Assert.DoesNotContain("StartGateway", h.Calls);
        Assert.DoesNotContain("EnrollDirector", h.Calls);
    }

    [Fact]
    public async Task RunAsync_Failure_UndoesOnlyWhatThisRunCreated_NewestFirst()
    {
        var h = new Harness();
        // The machine was ALREADY signed in - this run merely observed it.
        h.Behaviour[SelfHostStep.SignIn] = () => SelfHostStepResult.AlreadyThere("already signed in");
        h.Behaviour[SelfHostStep.StartGateway] = () => SelfHostStepResult.Failed("port in use");

        var result = await h.Build().RunAsync();

        Assert.False(result.Success);
        Assert.True(result.RolledBack);
        // ONLY the asset this run placed is undone. The pre-existing sign-in is untouched - undoing
        // it would sign the user out of something this run did not create.
        Assert.Equal([SelfHostStep.PlaceGatewayAsset], h.Compensated);
    }

    [Fact]
    public async Task RunAsync_NothingOwned_NothingIsUndone()
    {
        var h = new Harness();
        h.Behaviour[SelfHostStep.SignIn] = () => SelfHostStepResult.Failed("user closed the browser");

        var result = await h.Build().RunAsync();

        Assert.False(result.Success);
        Assert.False(result.RolledBack);
        Assert.Empty(h.Compensated);
    }

    [Fact]
    public async Task RunAsync_Resumes_DoesNotRepeatCompletedSteps()
    {
        var h = new Harness();
        h.Behaviour[SelfHostStep.StartGateway] = () => SelfHostStepResult.Failed("gateway did not answer");
        await h.Build().RunAsync();

        // Second attempt: the browser sign-in must NOT happen again.
        h.Calls.Clear();
        h.Behaviour.Remove(SelfHostStep.StartGateway);
        // The placed asset was rolled back, so it legitimately re-runs; sign-in was owned and undone
        // too, so seed a journal representing a run whose sign-in survived.
        h.Journal = new SelfHostJournal { Completed = [SelfHostStep.SignIn], Owned = [] }.ToJson();

        var result = await h.Build().RunAsync();

        Assert.True(result.Success);
        Assert.DoesNotContain("SignIn", h.Calls);
        Assert.Contains("StartGateway", h.Calls);
    }

    [Fact]
    public async Task RunAsync_Cancelled_StopsAndUndoesWhatItCreated()
    {
        var h = new Harness();
        var cts = new CancellationTokenSource();
        var orchestrator = h.Build(cts, SelfHostStep.PlaceGatewayAsset);

        var result = await orchestrator.RunAsync(cts.Token);

        Assert.False(result.Success);
        Assert.True(result.Cancelled);
        Assert.DoesNotContain("StartGateway", h.Calls);
        // Cleanup must still happen even though the token is already cancelled.
        Assert.Contains(SelfHostStep.PlaceGatewayAsset, h.Compensated);
    }

    [Fact]
    public async Task RunAsync_StepThrows_IsAFailedStepNotACrash()
    {
        var h = new Harness();
        var steps = h.Steps();
        var throwing = new SelfHostSteps
        {
            SignIn = steps.SignIn,
            PlaceGatewayAsset = _ => throw new InvalidOperationException("disk full"),
            StartGateway = steps.StartGateway,
            EnrollDirector = steps.EnrollDirector,
            ProbeInferenceReady = steps.ProbeInferenceReady,
            Compensate = steps.Compensate,
        };

        var result = await new SelfHostOrchestrator(throwing, () => h.Journal, j => h.Journal = j).RunAsync();

        Assert.False(result.Success);
        Assert.Contains("disk full", result.Message);
    }

    [Fact]
    public async Task RunAsync_InferenceNotReady_IsStillASuccessfulProvision()
    {
        // A healthy Gateway is NOT the same thing as a ready inference path. The runtime mints the
        // key on its own schedule; gating the connect flow on it would turn a working Gateway into
        // a failed provision.
        var h = new Harness { InferenceReady = false };

        var result = await h.Build().RunAsync();

        Assert.True(result.Success);
        Assert.False(result.InferenceReady);
        Assert.Contains(result.Steps, s => s.Contains("warming up"));
    }

    [Fact]
    public async Task RunAsync_InferenceProbeThrows_DoesNotFailTheProvision()
    {
        var h = new Harness();
        var steps = h.Steps();
        var probeThrows = new SelfHostSteps
        {
            SignIn = steps.SignIn,
            PlaceGatewayAsset = steps.PlaceGatewayAsset,
            StartGateway = steps.StartGateway,
            EnrollDirector = steps.EnrollDirector,
            ProbeInferenceReady = _ => throw new HttpRequestException("not up yet"),
            Compensate = steps.Compensate,
        };

        var result = await new SelfHostOrchestrator(probeThrows, () => h.Journal, j => h.Journal = j).RunAsync();

        Assert.True(result.Success);
        Assert.False(result.InferenceReady);
    }

    [Fact]
    public async Task RunAsync_CompensationThatFails_DoesNotStopTheRest()
    {
        var h = new Harness();
        var steps = h.Steps();
        var compensated = new List<SelfHostStep>();
        var failing = new SelfHostSteps
        {
            SignIn = steps.SignIn,
            PlaceGatewayAsset = steps.PlaceGatewayAsset,
            StartGateway = _ => Task.FromResult(SelfHostStepResult.Failed("nope")),
            EnrollDirector = steps.EnrollDirector,
            ProbeInferenceReady = steps.ProbeInferenceReady,
            Compensate = (step, _) =>
            {
                compensated.Add(step);
                // The newest-first one blows up; the older one must still be undone.
                if (step == SelfHostStep.PlaceGatewayAsset)
                    throw new IOException("file locked");
                return Task.CompletedTask;
            },
        };

        var result = await new SelfHostOrchestrator(failing, () => h.Journal, j => h.Journal = j).RunAsync();

        Assert.False(result.Success);
        Assert.Equal([SelfHostStep.PlaceGatewayAsset, SelfHostStep.SignIn], compensated);
        Assert.Contains(result.Steps, s => s.Contains("Could not undo"));
    }

    [Fact]
    public async Task RunAsync_CorruptJournal_StartsFreshRatherThanFailing()
    {
        var h = new Harness { Journal = "{not json at all" };

        var result = await h.Build().RunAsync();

        Assert.True(result.Success);
        Assert.Equal(4, h.Calls.Count);
    }

    [Fact]
    public void Describe_EveryStepHasPlainEnglish()
    {
        foreach (var step in Enum.GetValues<SelfHostStep>())
        {
            var text = SelfHostOrchestrator.Describe(step);
            Assert.False(string.IsNullOrWhiteSpace(text));
            // The screen renders this verbatim, so it must not leak an enum name at the user.
            Assert.NotEqual(step.ToString(), text);
        }
    }

    [Fact]
    public void Journal_OwnershipSurvivesARoundTrip()
    {
        var journal = new SelfHostJournal();
        journal.MarkComplete(SelfHostStep.SignIn, owned: false);
        journal.MarkComplete(SelfHostStep.PlaceGatewayAsset, owned: true);

        var restored = SelfHostJournal.FromJson(journal.ToJson());

        Assert.True(restored.IsComplete(SelfHostStep.SignIn));
        Assert.False(restored.Owns(SelfHostStep.SignIn));
        Assert.True(restored.Owns(SelfHostStep.PlaceGatewayAsset));
    }
}
