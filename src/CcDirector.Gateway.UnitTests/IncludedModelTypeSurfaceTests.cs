using System.Reflection;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.CarMode;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The phase-2 inspection (round 2) bypassed the previous runtime guards by CONSTRUCTION, two ways:
/// <see cref="HostedInferenceBrain"/> accepted a catalog id when the base URL was spelled
/// <c>https://devthrottle.com:443/api/v1</c> (same endpoint, defeats string equality), and
/// <see cref="HostedCarModeChat"/> / <see cref="CarModeWarmup"/> publicly accepted raw resolver
/// tuples around their guarded default resolver. These tests are those two constructions converted
/// into permanent evidence: every public seam that puts a chat model on a request authenticated by
/// the deployment credential is typed <see cref="IncludedModelId"/>, so the bypasses are no longer
/// expressible with a raw catalog string. Weaken any signature back to a raw string and this goes red.
/// </summary>
public sealed class IncludedModelTypeSurfaceTests
{
    [Fact]
    public void HostedInferenceBrain_Constructor_TakesTheProvenType_NeverARawModelString()
    {
        var ctors = typeof(HostedInferenceBrain).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(ctors);
        Assert.All(ctors, c =>
        {
            var model = c.GetParameters().SingleOrDefault(p => p.Name == "model");
            Assert.NotNull(model);
            Assert.Equal(typeof(IncludedModelId), model!.ParameterType);
        });
    }

    [Fact]
    public void CarModeTransports_ResolverTuples_CarryTheProvenType_NeverARawModelString()
    {
        // The model resolver a caller can hand HostedCarModeChat or CarModeWarmup must produce
        // IncludedModelId - the raw (BaseUrl, string Model, Key) tuple of the round-2 bypass does not
        // compile any more. Checked on the constructor signatures, where the bypass was constructed.
        var typedResolver = typeof(Func<TenantId, (string BaseUrl, IncludedModelId Model, string Key)>);

        var chatCtor = Assert.Single(typeof(HostedCarModeChat).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(typedResolver, chatCtor.GetParameters()[0].ParameterType);

        var warmupCtor = Assert.Single(typeof(CarModeWarmup).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(typedResolver, warmupCtor.GetParameters()[0].ParameterType);

        // And the default resolver produces the same typed tuple, so wiring stays one seam.
        var defaultResolver = typeof(HostedCarModeChat).GetMethod(nameof(HostedCarModeChat.DefaultResolver));
        Assert.NotNull(defaultResolver);
        Assert.Equal(typedResolver, defaultResolver!.ReturnType);
    }
}
