using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Api
{
    /// <summary>
    /// TEMPORARY, and it lives in a DIFFERENT NAMESPACE from the probe that uses it on purpose: a guard
    /// that filters on the rules namespace never looks inside this type, so a rules type calling it
    /// reaches the typing seam without the guard seeing anything. Removed two commits from now.
    /// </summary>
    internal static class ZzTemporaryPromptRelay
    {
        public static Task<(bool ok, PromptResponse? body, string? error)> Send(
            SessionVerbClient route, string sessionId, string text, CancellationToken ct) =>
            route.PostPromptAsync(sessionId, new PromptRequest { Text = text, AppendEnter = true }, ct);
    }
}

namespace CcDirector.Gateway.Rules
{
    /// <summary>
    /// TEMPORARY. Two deliberately BAD inputs, so the two guards this fix round tightens can be run
    /// against something that ought to fail them before either is trusted. This file is removed two
    /// commits from now, and both probe commits are left in the history on purpose so the reds reproduce
    /// by checkout - exactly as phase 1 left its types-nothing probe.
    ///
    /// PROBE ONE: a type in the rules namespace that types into a session ONE HELPER AWAY. The
    /// independent inspection said the types-nothing guard examines only each method's immediate
    /// operands, so a rules type that reaches the send through a helper in another namespace leaves the
    /// guard green. This is that shape, written out.
    ///
    /// PROBE TWO: a SIXTH attributed check. The plan says five ship and that adding one is a reviewed
    /// product change; the inspection said the registry tests only assert the five are PRESENT and never
    /// compare that list with the whole registry, so a sixth is legal and both tests stay green. This is
    /// that shape too.
    /// </summary>
    internal static class ZzTemporaryIndirectTypistProbe
    {
        /// <summary>Reaches the typing seam through the relay rather than directly, which is the whole
        /// point of the probe.</summary>
        public static Task<(bool ok, PromptResponse? body, string? error)> TypeSomething(
            SessionVerbClient route, string sessionId, CancellationToken ct) =>
            ZzTemporaryPromptRelay.Send(route, sessionId, "/usage-credits", ct);
    }

    /// <summary>The sixth check, which the ruling does not name among the five that ship.</summary>
    public static class ZzTemporaryExtraPrimitive
    {
        [RulePrimitive("A sixth check that the ruling does not name - the registry suite must notice it.")]
        public static bool ZzTemporaryExtraCheck(string text) => text.Length > 0;
    }
}
