using System;
using System.Linq;
using CcDirector.Core.Tenancy;
using Xunit;

namespace CcDirector.Core.Tests.Tenancy;

/// <summary>
/// The derivation contract for <see cref="TenantSessionKey"/>: the same raw session identifier under two
/// tenants derives two different internal keys, the same tenant derives the same key every time, the RAW
/// identifier survives untouched for external protocols, the raw tenant identifier never appears in the
/// key or in a log rendering, and an identifier that cannot be namespaced is refused rather than escaped.
/// </summary>
public sealed class TenantSessionKeyTests
{
    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantB = new("22222222-2222-2222-2222-222222222222");
    private const string SharedSessionId = "sess-abc123";

    [Fact]
    public void TwoTenants_SameRawSessionId_DeriveDifferentKeys()
    {
        var a = TenantSessionKey.For(TenantA, SharedSessionId);
        var b = TenantSessionKey.For(TenantB, SharedSessionId);

        Assert.NotEqual(a, b);
        Assert.NotEqual(a.Value, b.Value);
    }

    [Fact]
    public void SameTenant_SameRawSessionId_DerivesTheSameKeyEveryTime()
    {
        // Every write, read, removal, expiry pass and disconnect path must be able to derive the IDENTICAL
        // key, or a partition that holds on the write side strands state on the read side.
        var first = TenantSessionKey.For(TenantA, SharedSessionId);
        var second = TenantSessionKey.For(TenantA, SharedSessionId);

        Assert.Equal(first, second);
        Assert.Equal(first.Value, second.Value);
    }

    [Fact]
    public void RawSessionIdentifier_SurvivesUnchanged()
    {
        // Rule four of the map: external protocols keep the raw identifier. Route parameters, links, tunnel
        // commands, deletion, dictation progress, governance history and brief file paths all read this.
        var key = TenantSessionKey.For(TenantA, SharedSessionId);

        Assert.Equal(SharedSessionId, key.SessionId);
    }

    [Fact]
    public void Value_IsDomainTagged_AndCarriesTheRawIdentifierLast()
    {
        var key = TenantSessionKey.For(TenantA, SharedSessionId);
        var parts = key.Value.Split(TenantSessionKey.Separator);

        Assert.Equal(3, parts.Length);
        Assert.Equal(TenantSessionKey.Domain, parts[1]);
        Assert.Equal(SharedSessionId, parts[2]);
        Assert.Equal(64, parts[0].Length); // a Secure Hash Algorithm 256 digest as hex
    }

    [Fact]
    public void Value_NeverContainsTheRawTenantIdentifier()
    {
        var key = TenantSessionKey.For(TenantA, SharedSessionId);

        Assert.DoesNotContain(TenantA.Value, key.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToString_IsLogSafe_AndNeitherRawTenantNorFullHash()
    {
        var key = TenantSessionKey.For(TenantA, SharedSessionId);
        var rendered = key.ToString();

        Assert.DoesNotContain(TenantA.Value, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(key.Value.Split(TenantSessionKey.Separator)[0], rendered, StringComparison.Ordinal);
        Assert.Contains(TenantA.ToLogString(), rendered, StringComparison.Ordinal);
        Assert.Contains(SharedSessionId, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalTenant_StillDerivesAKey_SoSelfHostIsUnchangedInShape()
    {
        var key = TenantSessionKey.For(TenantId.Local, SharedSessionId);

        Assert.True(key.IsValid);
        Assert.Equal(SharedSessionId, key.SessionId);
    }

    [Fact]
    public void InvalidTenant_IsDenied_NotDefaulted()
    {
        Assert.Throws<ArgumentException>(() => TenantSessionKey.For(default, SharedSessionId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptySessionIdentifier_IsRefused(string sessionId)
    {
        Assert.Throws<ArgumentException>(() => TenantSessionKey.For(TenantA, sessionId));
    }

    [Theory]
    [InlineData("has|separator")]
    [InlineData("has/slash")]
    [InlineData("has\\backslash")]
    [InlineData("..")]
    [InlineData(".")]
    public void UnusableSessionIdentifier_IsRefused_NotEscaped(string sessionId)
    {
        // Refusing rather than sanitizing keeps ONE canonical form. Two different identifiers must never be
        // escaped into the same key, and a key later used as a directory name must not climb out.
        Assert.Throws<ArgumentException>(() => TenantSessionKey.For(TenantA, sessionId));
    }

    [Theory]
    [InlineData((char)0)]
    [InlineData((char)9)]
    [InlineData((char)10)]
    [InlineData((char)13)]
    public void ControlCharacterInSessionIdentifier_IsRefused(char control)
    {
        // Given by character code rather than as an escaped literal, so no control character sits in source.
        Assert.Throws<ArgumentException>(() => TenantSessionKey.For(TenantA, "has" + control + "control"));
    }

    // ===== SAME-TENANT IDENTITY =====
    // The tenancy dimension is only half the job. Within ONE tenant this type must keep distinct raw
    // identifiers distinct, or the partition it builds merges the very things it exists to separate.

    [Theory]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData(" both ")]
    public void SurroundingWhitespace_IsRefused_NotTrimmed(string sessionId)
    {
        // Trimming would map "x", " x" and "x " onto ONE entry, so a write for one would overwrite,
        // suppress or delete another. Refusal keeps the injectivity promise instead of tidying input.
        Assert.Throws<ArgumentException>(() => TenantSessionKey.For(TenantA, sessionId));
        Assert.False(TenantSessionKey.TryFor(TenantA, sessionId, out _));
    }

    [Fact]
    public void SameTenant_NearMissIdentifiers_AreNeverMergedIntoOneKey()
    {
        // The three that a trim would collapse. Exactly one of them is a valid identifier; the other two
        // are refused. What must NOT happen is all three arriving at one key.
        const string bare = "sess-near-miss";

        var accepted = TenantSessionKey.For(TenantA, bare);

        Assert.Throws<ArgumentException>(() => TenantSessionKey.For(TenantA, " " + bare));
        Assert.Throws<ArgumentException>(() => TenantSessionKey.For(TenantA, bare + " "));
        Assert.Equal(bare, accepted.SessionId);
    }

    [Fact]
    public void SameTenant_DistinctIdentifiers_AlwaysDeriveDistinctKeys()
    {
        // The injectivity property stated directly: not-equal in, never-equal out. Deliberately includes
        // pairs a normalizing implementation would fold - case, an inner space, a trailing digit.
        var identifiers = new[]
        {
            "sess-1", "sess-2", "sess-10", "sess_1", "Sess-1", "SESS-1",
            "sess 1", "sess  1", "a", "aa", "sess-1-", "-sess-1",
        };

        var keys = identifiers.Select(id => TenantSessionKey.For(TenantA, id)).ToList();

        Assert.Equal(identifiers.Length, keys.Select(k => k.Value).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(identifiers.Length, keys.Distinct().Count());
        Assert.Equal(identifiers.Length, keys.Select(k => k.SessionId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SessionIdentifier_IsStoredByteForByte_WithNoNormalization()
    {
        // An inner space is legitimate and must survive: it is neither leading nor trailing, so it is not
        // the collision case, and altering it would change the identifier every external protocol carries.
        const string inner = "sess with inner space";

        var key = TenantSessionKey.For(TenantA, inner);

        Assert.Equal(inner, key.SessionId);
        Assert.EndsWith(inner, key.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void SeparatorInjection_CannotForgeAnotherTenantsKey()
    {
        // The attack the domain tag and the refusal together close: a caller choosing a session identifier
        // that LOOKS like a whole namespaced key for another tenant.
        var victim = TenantSessionKey.For(TenantB, SharedSessionId);

        Assert.Throws<ArgumentException>(() => TenantSessionKey.For(TenantA, victim.Value));
    }

    [Fact]
    public void TryFor_YieldsAnInvalidKey_RatherThanThrowing()
    {
        Assert.False(TenantSessionKey.TryFor(TenantA, null, out var fromNull));
        Assert.False(fromNull.IsValid);

        Assert.False(TenantSessionKey.TryFor(default, SharedSessionId, out var fromNoTenant));
        Assert.False(fromNoTenant.IsValid);

        Assert.True(TenantSessionKey.TryFor(TenantA, SharedSessionId, out var good));
        Assert.Equal(TenantSessionKey.For(TenantA, SharedSessionId), good);
    }

    [Fact]
    public void DefaultKey_IsNotValid()
    {
        Assert.False(default(TenantSessionKey).IsValid);
        Assert.Equal("<invalid-session-key>", default(TenantSessionKey).ToString());
    }
}
