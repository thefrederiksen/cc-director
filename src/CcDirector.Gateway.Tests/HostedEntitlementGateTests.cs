using System;
using System.IO;
using CcDirector.Core.Account;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The hosted PAID-ENTITLEMENT gate at enrollment. Without it, enrolling against the hosted Gateway is free
/// and the billing side sells what anyone can take for nothing.
///
/// THE THING THESE TESTS EXIST TO HOLD, and it is the whole correctness of the gate: the entitlement read
/// has THREE outcomes, not two, and they must never share a code path.
///
///   Entitled     -> enroll.
///   NotEntitled  -> 402. We LOOKED and there is no valid entitlement. That is KNOWLEDGE.
///   Unknown      -> 503, retry. The read FAILED. We do not know.
///
/// The two failures are not symmetric and both are covered below by their own independent control:
///   - A false 402 locks out a customer who has PAID.
///   - A false MINT gives the product away for NOTHING - and that one is SILENT, because a successful
///     enrollment looks exactly like a correct one.
/// So the rule the tests enforce is: the mint happens ONLY on a confirmed entitlement. Ignorance mints
/// nothing and denies nothing.
///
/// Every deny path is checked for BOTH halves of the refusal - the status AND that no tenant was minted and
/// no device key issued. A 402 that still minted a tenant would satisfy a status-only assertion while having
/// given away the thing the gate exists to protect. That is the shape-versus-thing failure this mission keeps
/// paying for, so it is asserted directly.
///
/// Revert-prove: delete the entitlement block in HostedEnrollmentEndpoint.Enroll and the absence and
/// ignorance tests go RED - both wrongly enrolling - while the entitled test and the self-host control stay
/// green.
///
/// The table is created here with raw SQL rather than by a migration, deliberately and for the same reason
/// production does not migrate it: it belongs to the payment side, which creates and writes it as the
/// service role, while this Gateway holds SELECT and nothing more. Creating it here also states the schema
/// this code reads, so a contract drift shows up as a failing test rather than as a runtime surprise.
/// </summary>
public sealed class HostedEntitlementGateTests : IDisposable
{
    private const string Audience = "authenticated";
    private const string Issuer = "https://test.example.supabase.co/auth/v1";
    private const string Subject = "sub-paying-account";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _devPath = Path.Combine(Path.GetTempPath(), $"ent-dev-{Guid.NewGuid():N}.json");
    private readonly TestEs256Key _key = new();

    public void Dispose()
    {
        _harness.Dispose();
        _key.Dispose();
        if (File.Exists(_devPath)) File.Delete(_devPath);
    }

    private static EnrollSignedInRequest Req() => new()
    {
        DeviceId = "device-1",
        MachineName = "M",
        Platform = "linux",
        DeviceType = "workstation",
    };

    /// <summary>Creates the payment-side table this Gateway only reads, and optionally seeds one row.</summary>
    private GatewayDatabase OpenWithEntitlements(string? status = null, DateTime? periodEnd = null, bool? livemode = true)
    {
        var db = _harness.Open();
        using var ctx = db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS entitlements (" +
            "subject TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, " +
            "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, updated_at TEXT NULL, " +
            "livemode INTEGER NULL)");

        if (status is not null)
        {
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO entitlements (subject, status, current_period_end, livemode) VALUES ({0}, {1}, {2}, {3})",
                Subject, status, periodEnd, livemode);
        }
        return db;
    }

    private (DeviceRegistry devices, TenantRegistry tenants, JwtAccessTokenValidator validator) Wire(GatewayDatabase db)
    {
        var devices = new DeviceRegistry(_devPath);
        var tenants = new TenantRegistry(db);
        var validator = new JwtAccessTokenValidator(
            "test-signing-secret", timeProvider: null, publicKeySetJson: _key.PublicKeySetJson(),
            expectedAudience: Audience, expectedIssuer: Issuer, allowSymmetricHs256: false);
        return (devices, tenants, validator);
    }

    private string Token() => _key.Token(Subject, "payer@example.com", Audience, Issuer);

    /// <summary>Did anything get given away? Both halves of what the gate protects.</summary>
    private void AssertNothingWasGivenAway(TenantRegistry tenants)
    {
        Assert.Null(tenants.LookupBySubject(Subject));                       // no tenant minted
        Assert.False(File.Exists(_devPath) && File.ReadAllText(_devPath).Contains("device-1", StringComparison.Ordinal));
    }

    [Fact]
    public void An_active_subscription_enrolls()
    {
        // The positive control for every refusal below. Without it, "unpaid accounts are refused" would also
        // hold if the gate refused EVERYONE - which would pass every deny test and ship a dead product.
        var db = OpenWithEntitlements(EntitlementRegistry.StatusActive);
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false), DateTime.UtcNow);

        Assert.Equal(200, result.Status);
        Assert.NotNull(result.Response);
        Assert.False(string.IsNullOrWhiteSpace(result.Response!.DeviceKey));
        Assert.NotNull(tenants.LookupBySubject(Subject));
    }

    [Fact]
    public void ABSENCE_denies_with_402_and_mints_nothing()
    {
        // CONTROL ONE: the read SUCCEEDS and finds nothing. That is knowledge, so it denies - and it must
        // leave no trace, because minting a tenant is itself giving something away.
        var db = OpenWithEntitlements();   // table exists, no row for this subject
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false), DateTime.UtcNow);

        Assert.Equal(402, result.Status);
        Assert.Null(result.Response);
        AssertNothingWasGivenAway(tenants);
    }

    [Fact]
    public void IGNORANCE_does_not_deny_and_does_not_mint()
    {
        // CONTROL TWO, and the one that matters most. The read FAILS - here for real, because the table the
        // payment side owns does not exist at all, which is exactly what a lost SELECT grant or an
        // un-migrated database looks like from this side.
        //
        // It must answer RETRY: not 402, because we have not established that the account is unpaid and a
        // false 402 locks out a payer; and above all not a MINT, because a false mint gives the product away
        // silently. This is the assertion that stops a database hiccup from turning a paid product free.
        var db = _harness.Open();          // no entitlements table created
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false), DateTime.UtcNow);

        Assert.Equal(503, result.Status);
        Assert.NotEqual(402, result.Status);   // stated separately: ignorance must not masquerade as refusal
        Assert.Null(result.Response);
        AssertNothingWasGivenAway(tenants);
    }

    [Fact]
    public void A_payment_being_retried_is_entitled_until_the_paid_period_ends()
    {
        // The dunning window. The customer has paid for this period and their card merely failed on renewal;
        // cutting them off mid-period would refuse someone who has paid.
        var now = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var db = OpenWithEntitlements(EntitlementRegistry.StatusPastDue, now.AddDays(3));
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false), now);

        Assert.Equal(200, result.Status);
    }

    [Fact]
    public void A_payment_being_retried_is_refused_once_the_paid_period_has_ended()
    {
        // ...and the window is FINITE. Without this, past_due would be an open-ended free pass, which is the
        // same defect as no gate at all wearing a policy's clothes.
        var now = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var db = OpenWithEntitlements(EntitlementRegistry.StatusPastDue, now.AddSeconds(-1));
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false), now);

        Assert.Equal(402, result.Status);
        AssertNothingWasGivenAway(tenants);
    }

    [Fact]
    public void A_payment_being_retried_is_refused_AT_the_exact_instant_the_paid_period_ends()
    {
        // THE BOUNDARY ITSELF, which the two tests above deliberately straddle without ever landing on. One
        // probes three days inside the window and the other one second past it, so an inclusive comparison
        // at the end instant passes both of them while granting access on an entitlement that has expired.
        // At exactly CurrentPeriodEnd the paid period has ended - that is what the field means - so this is
        // a refusal. Small in duration, but it is a grant in the deny-OPEN direction, and an untested
        // boundary is where a policy quietly becomes the opposite of what it says.
        var now = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var db = OpenWithEntitlements(EntitlementRegistry.StatusPastDue, now);
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false), now);

        Assert.Equal(402, result.Status);
        AssertNothingWasGivenAway(tenants);
    }

    [Theory]
    [InlineData("canceled")]
    [InlineData("incomplete_expired")]
    [InlineData("")]
    [InlineData("ACTIVE_BUT_NOT_REALLY")]
    public void Any_state_that_is_not_a_granting_one_is_refused(string status)
    {
        // An unrecognised state is NOT entitled. We do not guess in the paying direction, and a state this
        // code has never heard of is the case where guessing is most tempting and least justified.
        var db = OpenWithEntitlements(status);
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false), DateTime.UtcNow);

        Assert.Equal(402, result.Status);
        AssertNothingWasGivenAway(tenants);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void A_subscription_that_is_not_live_money_is_refused_on_hosted(bool? livemode)
    {
        // A payment-provider TEST-mode subscription costs nothing to create, so honouring one is a paywall
        // bypass - and in the deny-OPEN direction, which is the silent one: the enrollment succeeds and looks
        // exactly like a paying customer.
        //
        // NULL IS ITS OWN CASE AND IT IS THE ONE MOST LIKELY TO BE WRITTEN AS A PASS. A row written before
        // this column existed, or by a webhook that forgot to set it, arrives null - and "we did not record
        // whether this was real money" is not evidence that it was. Both are refused, which is why this is a
        // Theory over false AND null rather than a single false case.
        //
        // Note this is ABSENCE, not ignorance: the read succeeded and returned a row that is not a valid
        // entitlement. So it earns the 402 and mints nothing - it does not become a retry.
        var db = OpenWithEntitlements(EntitlementRegistry.StatusActive, livemode: livemode);
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: true), DateTime.UtcNow);

        Assert.Equal(402, result.Status);
        AssertNothingWasGivenAway(tenants);
    }

    [Fact]
    public void The_same_row_enrolls_when_live_money_is_not_required()
    {
        // The control that stops the test above passing for the wrong reason. An identical active row, with
        // livemode null, DOES enroll when the requirement is off - so the refusals above are caused by the
        // livemode rule specifically and not by the row being rejected for some unrelated defect.
        var db = OpenWithEntitlements(EntitlementRegistry.StatusActive, livemode: null);
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false), DateTime.UtcNow);

        Assert.Equal(200, result.Status);
    }

    [Fact]
    public void Self_host_has_no_gate_at_all()
    {
        // THE CONTROL. Self-host has no billing and no boundary, so passing no entitlement registry must
        // leave enrollment exactly as it was. If this ever reddens, the gate has leaked into the unpaid,
        // un-billed deployment where there is nothing to sell.
        var db = _harness.Open();          // no entitlements table, and none needed
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            entitlements: null);

        Assert.Equal(200, result.Status);
        Assert.NotNull(result.Response);
    }
}
