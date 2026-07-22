using System;
using System.IO;
using System.Linq;
using System.Reflection;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The upload staging store is bound to a tenant AT CONSTRUCTION, and there is no way to construct one
/// that is not.
///
/// This is a structural guarantee rather than a runtime check, and the distinction is the point. The
/// partition itself (issue #1884) is enforced by the directory a store resolves to; that work assumed every
/// store in existence had been given a tenant. Two public constructors could produce one WITHOUT being
/// given a tenant, so the widest scope was the shape reached by writing the LEAST code - which is the
/// inverse of what a reviewer's attention rewards, because there is nothing on the line to question.
///
/// Making the tenant a required argument removes the shape entirely: the failure mode is a BUILD ERROR at
/// the call site of anyone who does not name a partition, so there is no unscoped store to guard, to
/// enumerate, or to forget to guard. The tests below stand watch over that property so it cannot be
/// re-opened by a well-meaning convenience overload, and pin the two runtime edges of construction that a
/// compiler cannot decide: a struct default that never went through validation, and the partition a named
/// account tenant actually lands in.
/// </summary>
public sealed class VoiceUploadStoreRequiredTenantTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cc-required-tenant-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>
    /// EVERY public way to construct this store names the partition it belongs to. This is the guard that
    /// keeps the guarantee from quietly regressing: a convenience overload added later - a no-argument one,
    /// or a root-only one - reddens here, at the moment it is written, rather than the first time a new
    /// endpoint uses it against real accounts' audio.
    /// </summary>
    [Fact]
    public void EveryPublicConstructor_RequiresATenant()
    {
        var constructors = typeof(VoiceUploadStore).GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        Assert.NotEmpty(constructors);
        foreach (var constructor in constructors)
        {
            var signature = string.Join(
                ", ", constructor.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
            Assert.True(
                constructor.GetParameters().Any(p => p.ParameterType == typeof(TenantId)),
                $"Public constructor VoiceUploadStore({signature}) does not take a TenantId. "
                + "An upload store must name its partition at construction; a store that can be built "
                + "without one makes the widest scope the default.");
        }
    }

    /// <summary>
    /// A tenant that never went through validation is REFUSED, not silently read as the local partition.
    /// <c>default(TenantId)</c> is the one value that reaches this type without its owner having decided
    /// anything, because a struct default bypasses the validating constructor.
    ///
    /// The MESSAGE is asserted, not merely the exception type, and that is not decoration. Measured, not
    /// assumed: with the validity check removed, an unresolved tenant still threw - from the partition-naming
    /// rule further down, which refuses a null value because it is not a canonical identifier. Asserting only
    /// the type therefore passed whether or not the check that is supposed to own this decision existed at
    /// all, and could not tell the two apart. Pinning the message makes this test a canary for THAT check.
    /// </summary>
    [Fact]
    public void Construction_WithAnUnresolvedTenant_IsRefusedByTheValidityCheck()
    {
        var error = Assert.Throws<ArgumentException>(() => new VoiceUploadStore(_root, default));

        Assert.Contains("unresolved tenant is denied", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Naming an account tenant at construction puts the store INSIDE that tenant's partition - the same
    /// directory the re-scoping path resolves - so the direct constructor is not a second, unpartitioned
    /// way in.
    /// </summary>
    [Fact]
    public void Construction_WithAnAccountTenant_StagesInsideThatTenantsPartition()
    {
        var tenant = new TenantId(Guid.NewGuid().ToString("D"));

        var constructed = new VoiceUploadStore(_root, tenant);
        var rescoped = new VoiceUploadStore(_root, TenantId.Local).ForTenant(tenant);

        Assert.Equal(rescoped.Root, constructed.Root);
        Assert.Equal(tenant, constructed.Tenant);
        Assert.NotEqual(
            Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(constructed.Root).TrimEnd(Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Naming the local tenant keeps the exact root self-host has always used: requiring the argument
    /// changes what an author must WRITE, never where an existing install's audio lives.
    /// </summary>
    [Fact]
    public void Construction_WithTheLocalTenant_KeepsTheRootUnchanged()
    {
        var store = new VoiceUploadStore(_root, TenantId.Local);

        Assert.Equal(
            Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(store.Root).TrimEnd(Path.DirectorySeparatorChar));
        Assert.True(store.Tenant.IsLocal);
    }
}
