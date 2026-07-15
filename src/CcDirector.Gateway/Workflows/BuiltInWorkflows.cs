namespace CcDirector.Gateway.Workflows;

/// <summary>
/// One step of a workflow: a named piece of work, who does it, who reviews it, and what finishing it
/// means. Reviewer is null when the step has no separate review seat - which is itself a statement the
/// workflow is making, not an omission.
/// </summary>
public sealed record WorkflowStep(
    string Name,
    string Description,
    string Doer,
    string? Reviewer,
    string Done);

/// <summary>
/// A workflow: a named, saved definition of how a piece of work gets done by agents. The workflow -
/// not the agent, and not a skill file in a repository - decides the shape of the work: which seats
/// exist, which seat starts, which seat reviews, and where the human is asked.
/// </summary>
public sealed record WorkflowDefinition(
    string Id,
    string Name,
    string Summary,
    string WhenToUse,
    string HumanCheckpoint,
    IReadOnlyList<WorkflowStep> Steps);

/// <summary>
/// The workflows the Gateway ships with (issue #1617). These are the three shapes of work this fleet
/// already runs by hand, written down so they can be seen and chosen rather than being implied by
/// which skill file an agent happened to read.
///
/// They are BUILT IN and read-only at this step, on purpose. The Gateway is the home for workflows -
/// it serves them, and every Director asks it rather than carrying a private copy - but authoring and
/// editing them (in the Cockpit, and later per-tenant for an organisation) is a later step. Serving a
/// fixed set first means the Cockpit page reads real Gateway data from day one instead of a stub that
/// has to be torn out.
/// </summary>
public static class BuiltInWorkflows
{
    private static readonly IReadOnlyList<WorkflowDefinition> Definitions = new[]
    {
        new WorkflowDefinition(
            Id: "mission",
            Name: "Mission",
            Summary: "An Architect settles the design, a Manager drives the phases, and Workers build. "
                   + "The owner is bothered once, at the report.",
            WhenToUse: "Work big enough to need a design settled before anyone builds, or work that runs "
                     + "across more than one phase.",
            HumanCheckpoint: "Once, at the quality report. The Architect directs the Manager; only "
                           + "owner-level calls reach the human.",
            Steps: new[]
            {
                new WorkflowStep(
                    Name: "Settle the design",
                    Description: "The Architect decides what is being built and why, and writes the phases down. "
                               + "Nothing is built until the design is settled.",
                    Doer: "Architect",
                    Reviewer: null,
                    Done: "The phases are written down and the why is stated."),
                new WorkflowStep(
                    Name: "Drive the phase",
                    Description: "The Manager takes one phase and drives it to merged. A fresh Manager is seated "
                               + "per phase so context does not silt up across phases.",
                    Doer: "Manager",
                    Reviewer: "Architect",
                    Done: "The phase is merged to the main branch."),
                new WorkflowStep(
                    Name: "Build",
                    Description: "Workers do the building under the Manager, each in its own worktree so two "
                               + "workstreams never share a tree.",
                    Doer: "Worker",
                    Reviewer: "Manager",
                    Done: "A merged pull request. Committed and pushed is still in progress."),
                new WorkflowStep(
                    Name: "Report",
                    Description: "The quality report goes to the owner. This is the one interruption the mission "
                               + "is allowed to spend.",
                    Doer: "Manager",
                    Reviewer: "Architect",
                    Done: "The owner has the report."),
            }),

        new WorkflowDefinition(
            Id: "standalone",
            Name: "Standalone",
            Summary: "One agent picks up the work and finishes it. No manager, no review seat.",
            WhenToUse: "Work small enough that a second pair of eyes would cost more than it catches - a "
                     + "typo, a version bump, a one-line fix with a test already around it.",
            HumanCheckpoint: "Once, when the work is merged.",
            Steps: new[]
            {
                new WorkflowStep(
                    Name: "Do the work",
                    Description: "One agent takes the work from the request to a merged pull request, in its own "
                               + "worktree cut from the main branch.",
                    Doer: "Worker",
                    Reviewer: null,
                    Done: "A merged pull request."),
            }),

        new WorkflowDefinition(
            Id: "standalone-with-review",
            Name: "Standalone with review",
            Summary: "One agent does the work, a second and separate agent reviews it before it is called done.",
            WhenToUse: "The default for ordinary work. Small enough not to need an Architect, big enough that "
                     + "nobody should mark their own homework.",
            HumanCheckpoint: "Once, when the review passes.",
            Steps: new[]
            {
                new WorkflowStep(
                    Name: "Do the work",
                    Description: "One agent takes the work to a pull request, with the proof that it does what it "
                               + "is supposed to.",
                    Doer: "Worker",
                    Reviewer: null,
                    Done: "A pull request with proof attached."),
                new WorkflowStep(
                    Name: "Review",
                    Description: "A SEPARATE agent - never the one that wrote it - verifies the work against the "
                               + "reported symptom rather than trusting the report, then passes it or sends it back "
                               + "with a written defect.",
                    Doer: "Reviewer",
                    Reviewer: null,
                    Done: "The reviewer passed it, and the pull request is merged."),
            }),
    };

    /// <summary>Every workflow the Gateway serves, in the order the Cockpit lists them.</summary>
    public static IReadOnlyList<WorkflowDefinition> All() => Definitions;
}
