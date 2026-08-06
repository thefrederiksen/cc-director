using System.Reflection;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// <see cref="IncludedModelId"/> is the structural guard of the Included AI mission (issue #1360):
/// the ONLY way a chat model id reaches a request authenticated by the DevThrottle deployment
/// credential is as this type, and the only way to obtain the type is the mint on the class itself.
/// These tests pin the producer surface with reflection - so a new public way to conjure an instance
/// goes red here instead of waiting for an inspection - and prove the mint's two behaviours: refuse
/// (null) for the callers that answer 400, and fall forward to the included default for the
/// resolution legs.
/// </summary>
public sealed class IncludedModelIdTests
{
    [Fact]
    public void TheMintIsTheOnlyProducer_NoPublicConstructor_AndAPinnedStaticSurface()
    {
        // Phase-2 inspection round 2 bypassed the earlier runtime guards by construction. The fix is
        // that construction itself is impossible: no constructor is visible outside the class, and
        // the public static members able to produce an instance are exactly the pinned set below -
        // the two mint methods plus the three known included ids. Add a producer and this fails.
        var type = typeof(IncludedModelId);

        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.All(
            type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance),
            c => Assert.True(c.IsPrivate, $"constructor {c} must be private - the mint is the only producer"));

        var producers = type
            .GetMembers(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m switch
            {
                MethodInfo method => method.ReturnType == type && !method.IsSpecialName,
                PropertyInfo property => property.PropertyType == type,
                _ => false,
            })
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new[] { "DictationCleanup", "MintOrFallForward", "TryMint", "Wingman", "WingmanFast" },
            producers);
    }

    [Fact]
    public void AReflectionForgedInstance_ThrowsAtFirstUseOfValue()
    {
        // Phase-2 inspection round 3 invoked the private constructor through reflection with the
        // catalog id both earlier bypasses carried, and the transports trusted the forged instance.
        // The Value getter now validates on every read, so a forged instance throws at its first
        // use and can never reach a transport. This is the round-3 inspection's construction made
        // a permanent test.
        var constructor = Assert.Single(
            typeof(IncludedModelId).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance));

        var forged = (IncludedModelId)constructor.Invoke(new object[] { "Qwen/Qwen2.5-72B-Instruct" });

        var thrown = Assert.Throws<InvalidOperationException>(() => forged.Value);
        Assert.Contains("outside the mint", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryMint_RefusesACatalogId_AndMintsAnIncludedId()
    {
        // The exact catalog id both inspection bypasses carried must not mint.
        Assert.Null(IncludedModelId.TryMint("Qwen/Qwen2.5-72B-Instruct"));
        Assert.Null(IncludedModelId.TryMint("glm-5.2"));
        Assert.Null(IncludedModelId.TryMint("opus"));
        Assert.Null(IncludedModelId.TryMint(null));
        Assert.Null(IncludedModelId.TryMint(""));
        Assert.Null(IncludedModelId.TryMint("   "));

        var minted = IncludedModelId.TryMint("devthrottle/wingman");
        Assert.NotNull(minted);
        Assert.Equal("devthrottle/wingman", minted!.Value);
        Assert.Equal(IncludedModelId.Wingman, minted);
    }

    [Fact]
    public void MintOrFallForward_FallsForwardToTheIncludedDefault_ForACatalogId()
    {
        // The fall-forward rule every resolution leg (saved setting, tenant override, environment
        // override) now runs through: a catalog id degrades to the included default instead of
        // billing credits on an internal feature.
        Assert.Equal(IncludedModelId.WingmanFast,
            IncludedModelId.MintOrFallForward("Qwen/Qwen2.5-72B-Instruct", IncludedModelId.WingmanFast));
        Assert.Equal(IncludedModelId.Wingman,
            IncludedModelId.MintOrFallForward(null, IncludedModelId.Wingman));

        // An included candidate is honored, never swapped for the default.
        Assert.Equal("devthrottle/wingman",
            IncludedModelId.MintOrFallForward("devthrottle/wingman", IncludedModelId.WingmanFast).Value);
    }

    [Fact]
    public void TheKnownStatics_CarryTheIncludedConstants()
    {
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleWingmanModel, IncludedModelId.Wingman.Value);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleWingmanFastModel, IncludedModelId.WingmanFast.Value);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleDictationCleanupModel, IncludedModelId.DictationCleanup.Value);
        Assert.True(TranscriptionEndpointResolver.IsDevThrottleIncludedModel(IncludedModelId.Wingman.Value));
        Assert.True(TranscriptionEndpointResolver.IsDevThrottleIncludedModel(IncludedModelId.WingmanFast.Value));
        Assert.True(TranscriptionEndpointResolver.IsDevThrottleIncludedModel(IncludedModelId.DictationCleanup.Value));
    }

    [Fact]
    public void EqualityIsByValue()
    {
        Assert.Equal(IncludedModelId.TryMint("devthrottle/wingman"), IncludedModelId.TryMint("devthrottle/wingman"));
        Assert.NotEqual(IncludedModelId.Wingman, IncludedModelId.WingmanFast);
        Assert.Equal(IncludedModelId.Wingman.GetHashCode(), IncludedModelId.TryMint("devthrottle/wingman")!.GetHashCode());
        Assert.Equal("devthrottle/wingman", IncludedModelId.Wingman.ToString());
    }
}
