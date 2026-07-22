namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// A tenant-scoped entity whose primary key is a <see cref="Guid"/> the GATEWAY ITSELF mints, and which is
/// therefore allowed to keep a primary key that does NOT include <c>tenant_id</c>.
///
/// WHY THIS IS A TYPE AND NOT AN ALLOWLIST
///
/// Every other tenant-scoped table must put <c>tenant_id</c> in its primary key, because its key comes from
/// the caller: on a caller-supplied key, one tenant presenting an identifier another tenant already holds
/// cannot insert (a cross-tenant SQUAT) and learns from the failure that someone else holds it (an EXISTENCE
/// ORACLE). A freshly minted <see cref="Guid"/> has neither problem - no caller can present it, so there is
/// nothing to squat and nothing to disclose.
///
/// That exemption used to be a written allowlist: a table name plus a sentence asserting "the store mints
/// this with Guid.NewGuid()". A sentence is not enforceable. The guard beside it could only re-check the
/// SHAPE of the key - that it was still one Guid - which is true both before and after the one change that
/// actually matters. Had any store switched from minting the value to accepting one from the caller, the key
/// would still have been a single Guid and every guard would still have been green: the check could not fail
/// in the precise case it existed to catch, which is worse than no check, because it reads as protection.
///
/// So the claim is now carried by this type instead of by prose:
///
///  - <see cref="Id"/> has a PRIVATE setter. No store, endpoint, test or caller can assign it. Writing
///    <c>new CronRunEntity { Id = callerSuppliedGuid }</c> is not a test failure to be discovered later -
///    it DOES NOT COMPILE.
///  - The value is minted by the initializer below, inside a class whose entire scope is this one property.
///    There is no caller input reachable from here, so there is no expression this initializer could be
///    changed to that would smuggle a caller's value in.
///  - EF Core still materializes rows normally: it writes the compiler-generated backing field directly when
///    loading, so a persisted key round-trips unchanged and only NEW rows take a minted value.
///
/// The remaining ways to defeat it are both covered by <c>TenantScopeGuardTests</c>, by name and by
/// construction rather than by argument: widening this setter is caught by the reflection test on this type,
/// and re-declaring an <c>Id</c> on a derived entity - or dropping this base class entirely - is caught by
/// the primary-key test, which requires the mapped key property to be THIS class's <see cref="Id"/>, the one
/// only this class can write. Deriving from this type is therefore a claim the compiler and the guard both
/// hold you to, and adding a new one costs a base class rather than a sentence.
/// </summary>
public abstract class GatewayMintedKeyEntity : TenantScopedEntity
{
    /// <summary>
    /// The primary key: a fresh <see cref="Guid"/> minted by the Gateway at construction, globally unique by
    /// construction and never a value a caller can present or choose.
    ///
    /// The setter is PRIVATE on purpose and must stay private - it is the whole mechanism. See the type
    /// remarks: it is what makes "the Gateway mints this key" a fact the compiler enforces rather than a
    /// sentence in a list. EF Core sets the backing field directly when it materializes a loaded row, so a
    /// private setter costs nothing at read time.
    /// </summary>
    public Guid Id { get; private set; } = Guid.NewGuid();
}
