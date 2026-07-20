using System.Diagnostics.CodeAnalysis;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Voice;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The ONE door onto the dictation upload staging, and the reason a dictation leg can no longer be written
/// unscoped (issue #1884).
///
/// THE DEFECT SHAPE THIS REMOVES. Every dictation leg used to be scoped by hand:
///
///   <code>
///   if (ResolveTenant(ctx, tenantBoundary) is not { } tenant) return NoTenantResult();
///   var store = uploads.ForTenant(tenant);
///   </code>
///
/// Two lines, repeated five times, with the raw un-partitioned <see cref="VoiceUploadStore"/> sitting in
/// scope beside them. That is not one safeguard used five times - it is FIVE INDEPENDENT CHANCES TO FORGET.
/// Any one leg could drop its <c>ForTenant</c> while the tenant boundary and the partition computation both
/// stayed perfectly correct, and that leg alone would then read, overwrite, retire or discard another
/// account's recorded audio and its transcript. Nothing would fail to compile and nothing would fail loud.
/// The realistic version of that defect is not someone editing an existing leg: it is the SIXTH leg somebody
/// adds next month, who will never read any of the reasoning that surrounds these five.
///
/// So the fix is not to prove the five legs correct today. It is to make the mistake INEXPRESSIBLE:
/// <see cref="GatewayDictationEndpoint.Map"/> does not take a <see cref="VoiceUploadStore"/> at all. It takes
/// this gate. There is no unscoped store in scope anywhere in that file, so "forget to scope this leg" stops
/// being something a person can type - a new leg has nothing to call except <see cref="TryOpen"/>, and
/// <see cref="TryOpen"/> cannot hand back an unscoped store. A guarantee held by the type system does not
/// depend on the next author knowing why it matters.
///
/// It also folds the DENY into the same call. Resolving a tenant and refusing when there is none are one
/// decision, and splitting them across two statements is what let a leg keep the resolve and lose the scope.
/// Here a caller that does not check the return value has no store to use.
/// </summary>
internal sealed class DictationTenantGate
{
    private readonly VoiceUploadStore _uploads;
    private readonly Tenancy.HostedTenantBoundary? _boundary;

    /// <param name="uploads">
    /// The base staging store. Held PRIVATELY and never handed out un-partitioned - that is the whole point
    /// of this type, and it is why the endpoint takes the gate rather than the store.
    /// </param>
    /// <param name="boundary">
    /// The hosted tenant boundary. Required, so omitting it is a compile error rather than a silent runtime
    /// downgrade to the shared self-host root. It is still checked for null at the moment of use, because a
    /// test can force one through to reproduce the miswire and must be refused on hosted even so.
    /// </param>
    internal DictationTenantGate(VoiceUploadStore uploads, Tenancy.HostedTenantBoundary boundary)
    {
        _uploads = uploads ?? throw new ArgumentNullException(nameof(uploads));
        _boundary = boundary;
    }

    /// <summary>
    /// Open this request's own partition of the dictation staging, or refuse.
    ///
    /// Returns true with a store bound to the caller's tenant, or false with the 403 the caller must return.
    /// On the hosted Gateway a missing boundary, a boundary that is not hosted-wired, and an authenticated key
    /// with no bound tenant are ALL refusals - never the local partition, never the reserved system tenant.
    /// Off hosted mode every authenticated caller is the single local tenant, exactly as before.
    ///
    /// The out-parameter shape is deliberate: there is no way to obtain the store except through the branch
    /// that has already proven a tenant, so the check cannot be separated from the use.
    /// </summary>
    internal bool TryOpen(
        HttpContext ctx,
        [NotNullWhen(true)] out VoiceUploadStore? store,
        out TenantId tenant,
        [NotNullWhen(false)] out IResult? deny)
    {
        if (GatewayDictationEndpoint.ResolveTenant(ctx, _boundary) is not { } resolved)
        {
            store = null;
            tenant = default;
            deny = GatewayDictationEndpoint.NoTenantResult();
            return false;
        }

        store = _uploads.ForTenant(resolved);
        tenant = resolved;
        deny = null;
        return true;
    }
}
