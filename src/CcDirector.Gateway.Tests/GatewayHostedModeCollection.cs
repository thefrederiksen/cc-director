using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The collection for every test whose result depends on the PROCESS-WIDE <c>CC_GATEWAY_HOSTED</c>
/// variable - both the tests that SET it and the tests that would be broken by somebody else setting it.
///
/// Two rules put a class in here:
///   1. It sets <c>CC_GATEWAY_HOSTED</c> itself. The owner-settings deny classes set it to OPPOSITE values -
///      hosted in the deny classes, explicitly not-hosted in the self-host controls - so two of them running
///      concurrently would each be reading the other's mode.
///   2. It drives a route that is REFUSED on hosted (issue #1863 denied the whole owner-settings group).
///      Before that deny those routes answered the same way in both modes, so a class driving them did not
///      care what the variable said. Now it does: a hosted-mode class running in parallel would turn its
///      200 into a 404, intermittently and for reasons entirely outside that test file.
///
/// <c>DisableParallelization</c> is what actually delivers rule 2 - the collection runs on its own, so no
/// other collection can flip the mode underneath it. That is STRICTLY STRONGER than what
/// <see cref="DirectorRootCollection"/> gives its members - running alone subsumes running serially with a
/// subset - which is why the five classes moved in here no longer carry that attribute. Nothing was
/// weakened by the move.
///
/// WHY THIS SHIPS WITH THE DENY RATHER THAN SEPARATELY. Before issue #1863 these routes answered the same
/// way in both modes, so none of this was needed. The deny is what creates the need, so the fix belongs in
/// the change that creates it: shipping the deny without this collection would leave six test classes
/// nobody here wrote failing intermittently, for reasons entirely outside their own files. A change that
/// makes someone else's tests flaky is a change that shipped a defect.
/// </summary>
[CollectionDefinition("GatewayHostedMode", DisableParallelization = true)]
public sealed class GatewayHostedModeCollection
{
}
