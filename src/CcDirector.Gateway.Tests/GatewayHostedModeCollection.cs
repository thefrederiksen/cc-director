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
/// other collection can flip the mode underneath it. That also subsumes what <see cref="DirectorRootCollection"/>
/// does for its members, which is why the classes moved in here no longer carry that attribute.
/// </summary>
[CollectionDefinition("GatewayHostedMode", DisableParallelization = true)]
public sealed class GatewayHostedModeCollection
{
}
