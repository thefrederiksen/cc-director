using System;
using System.Collections.Generic;
using System.Linq;
using CcDirector.ControlApi;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.UnitTests;

/// <summary>
/// The capability handshake on Hello (#2457, #2459).
///
/// On 2026-08-05 the hosted Gateway was older than every Director talking to it - built before the
/// RegisterSessionKey hub method existed. Each Director went on minting a Gateway session key for
/// every session it launched and sending a registration that could never be accepted, so every
/// agent's command line answered 401 across the whole fleet. Nothing said "this Gateway is too old
/// for me", because nothing had ever asked. The Director discovered it one failed call at a time,
/// from a transport error carrying no version and no list.
///
/// These tests pin the two halves of the answer: that the Gateway reports what it can actually do,
/// and that the Director says something a person can act on when it cannot.
/// </summary>
public class GatewayCapabilityHandshakeTests
{
    // ----- the Gateway's half -----

    [Fact]
    public void TheHubReportsTheMethodsItReallyHas()
    {
        // Reflected from the hub, so it cannot drift from the truth. Hello and RegisterSessionKey are
        // asserted by name because they are the two the outage turned on.
        var capabilities = InvokeHelloCapabilities();

        Assert.Contains("Hello", capabilities.HubMethods);
        Assert.Contains("RegisterSessionKey", capabilities.HubMethods);
        Assert.Contains("RevokeSessionKey", capabilities.HubMethods);
    }

    [Fact]
    public void TheReportedMethodsAreExactlyTheHubsOwnPublicSurface()
    {
        // The list must be neither short (a method a Director needs, reported missing when it is
        // there) nor long (a method reported present that nothing can call). Compared against
        // reflection over the type itself rather than a written list, because a written list here
        // would just be the same drift risk moved into the test.
        var expected = typeof(DirectorHub)
            .GetMethods(System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Where(m => m.GetBaseDefinition().DeclaringType == typeof(DirectorHub))
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, InvokeHelloCapabilities().HubMethods);
    }

    [Fact]
    public void TheHubDoesNotReportInheritedPlumbingAsACallableMethod()
    {
        // Hub's own members are not things a Director invokes; listing them would pad the answer with
        // names that mean nothing to the caller.
        var methods = InvokeHelloCapabilities().HubMethods;

        Assert.DoesNotContain("Dispose", methods);
        Assert.DoesNotContain("ToString", methods);
        Assert.DoesNotContain("OnConnectedAsync", methods);
    }

    /// <summary>
    /// The capabilities the hub would return, read from the same static the hub returns. Building a
    /// DirectorHub needs a connection context this test has no business faking - the value under test
    /// is process-wide and computed once, so it is read directly.
    /// </summary>
    private static GatewayCapabilities InvokeHelloCapabilities()
    {
        var field = typeof(DirectorHub).GetField("Capabilities",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(field);
        var value = field!.GetValue(null) as GatewayCapabilities;
        Assert.NotNull(value);
        return value!;
    }

    // ----- the Director's half -----

    [Fact]
    public void ANullAnswerIsReadAsAGatewayOlderThanThisDirector()
    {
        // THE case this exists for. SignalR returns null when a hub method returns nothing, so a
        // Gateway built before this work answers null - and that alone dates it.
        var report = GatewayStreamClient.DescribeGatewayCapabilities(null);

        Assert.Contains("OLDER than this Director", report, StringComparison.Ordinal);
        Assert.Contains("deploying the Gateway", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMissingRegisterSessionKeyIsNamed_AndSaysWhatItCostsTheFleet()
    {
        // The exact 2026-08-05 Gateway: everything else present, session keys absent.
        var report = GatewayStreamClient.DescribeGatewayCapabilities(new GatewayCapabilities
        {
            Version = "1.9.6",
            Commit = "dae83a3",
            HubMethods = new List<string> { "Hello", "PushSnapshot", "PushDelta", "PushRepoSnapshot" },
        });

        Assert.Contains("RegisterSessionKey", report, StringComparison.Ordinal);
        // The version is what makes the two halves comparable in one line.
        Assert.Contains("1.9.6", report, StringComparison.Ordinal);
        Assert.Contains("dae83a3", report, StringComparison.Ordinal);
        // And the consequence, in the words the person reading it will have already heard from an agent.
        Assert.Contains("401", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AGatewayWithEverythingNeededSaysSo_WithoutNamingAMissingMethod()
    {
        var report = GatewayStreamClient.DescribeGatewayCapabilities(new GatewayCapabilities
        {
            Version = "1.9.10",
            Commit = "860f76e",
            HubMethods = new List<string>
            {
                "Hello", "RegisterSessionKey", "RevokeSessionKey",
                "PushSnapshot", "PushDelta", "PushRepoSnapshot",
                // Terminal Rules (issue #2644). A Gateway too old for PushScreen stores no screens, so
                // every reader falls back to a tunnel pull and nothing anywhere says why - which is
                // exactly the class of silence this handshake exists to break. This list is written out
                // by hand ON PURPOSE: deriving it from MethodsThisDirectorNeeds would make the test
                // incapable of failing, and its failing is what caught this addition.
                "PushScreen",
            },
        });

        Assert.DoesNotContain("MISSING", report, StringComparison.Ordinal);
        Assert.Contains("1.9.10", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ARealGatewaySatisfiesEveryMethodThisDirectorNeeds()
    {
        // The two lists are written in different files for different reasons, and nothing but this
        // test stops them parting company: a Director that "needs" a method this Gateway never had
        // would report a healthy, current Gateway as out of date on every reseed.
        var report = GatewayStreamClient.DescribeGatewayCapabilities(InvokeHelloCapabilities());

        Assert.DoesNotContain("MISSING", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnstampedBuildReportsTheVersionWithoutAnEmptyCommitBracket()
    {
        // A local development run has no COCKPIT_COMMIT. The line must still read as a sentence.
        var report = GatewayStreamClient.DescribeGatewayCapabilities(new GatewayCapabilities
        {
            Version = "1.9.10",
            Commit = "",
            HubMethods = new List<string>
            {
                "Hello", "RegisterSessionKey", "RevokeSessionKey",
                "PushSnapshot", "PushDelta", "PushRepoSnapshot", "PushScreen",
            },
        });

        Assert.DoesNotContain("()", report, StringComparison.Ordinal);
        Assert.Contains("Gateway v1.9.10", report, StringComparison.Ordinal);
    }
}
