using CcDirector.Core.HostedAi;
using Xunit;

namespace CcDirector.Core.Tests.HostedAi;

/// <summary>
/// Issue #938 (epic #937), extended by the Included AI mission (issue #1360): the runtime 402 mapper.
/// It must branch on the machine-readable <c>code</c> (never the shared <c>type</c>). The four known
/// codes map to their states; ANY other code - including an absent one - maps to the NEUTRAL
/// <see cref="HostedAiState.Unavailable"/>. The unknown-code tests are the Q3 revert-proof: put the
/// old NeedsCredits default back and they go red, because an unknown 402 would again show an
/// add-credits prompt to members the owner ruled must never see a cost.
/// </summary>
public sealed class HostedAiErrorMapperTests
{
    [Theory]
    [InlineData("insufficient_credits", HostedAiState.NeedsCredits)]
    [InlineData("monthly_limit_reached", HostedAiState.CapReached)]
    [InlineData("MONTHLY_LIMIT_REACHED", HostedAiState.CapReached)]
    [InlineData("  monthly_limit_reached  ", HostedAiState.CapReached)]
    [InlineData("subscription_required", HostedAiState.SubscriptionRequired)]
    [InlineData("SUBSCRIPTION_REQUIRED", HostedAiState.SubscriptionRequired)]
    [InlineData("fair_use_limit_reached", HostedAiState.FairUseLimitReached)]
    [InlineData("  fair_use_limit_reached  ", HostedAiState.FairUseLimitReached)]
    // The Q3 revert-proof rows: an unknown or absent code must NEVER claim "out of credits".
    [InlineData("some_other_code", HostedAiState.Unavailable)]
    [InlineData("unknown", HostedAiState.Unavailable)]
    [InlineData("", HostedAiState.Unavailable)]
    [InlineData(null, HostedAiState.Unavailable)]
    public void MapCode_BranchesOnCode(string? code, HostedAiState expected)
        => Assert.Equal(expected, HostedAiErrorMapper.MapCode(code));

    [Fact]
    public void ParseErrorCode_NestedErrorObject()
    {
        var body = "{ \"error\": { \"type\": \"insufficient_quota\", \"code\": \"monthly_limit_reached\" } }";
        Assert.Equal("monthly_limit_reached", HostedAiErrorMapper.ParseErrorCode(body));
    }

    [Fact]
    public void ParseErrorCode_FlatCode()
    {
        var body = "{ \"code\": \"insufficient_credits\" }";
        Assert.Equal("insufficient_credits", HostedAiErrorMapper.ParseErrorCode(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ \"error\": { \"type\": \"insufficient_quota\" } }")] // no code field
    public void ParseErrorCode_MissingOrUnparseable_IsUnknown_NeverAssumedOutOfCredits(string body)
        // Q3 revert-proof (issue #1360): the old default was insufficient_credits, which turned every
        // unreadable 402 into an add-credits prompt.
        => Assert.Equal(HostedAiErrorMapper.UnknownCode, HostedAiErrorMapper.ParseErrorCode(body));

    [Fact]
    public void Map402_NestedMonthlyLimit_CapReached()
    {
        var body = "{ \"error\": { \"type\": \"insufficient_quota\", \"code\": \"monthly_limit_reached\" } }";
        Assert.Equal(HostedAiState.CapReached, HostedAiErrorMapper.Map402(body));
    }

    [Fact]
    public void Map402_InsufficientCredits_NeedsCredits()
    {
        var body = "{ \"error\": { \"code\": \"insufficient_credits\" } }";
        Assert.Equal(HostedAiState.NeedsCredits, HostedAiErrorMapper.Map402(body));
    }

    [Theory]
    [InlineData("{ \"error\": { \"code\": \"subscription_required\" } }", HostedAiState.SubscriptionRequired)]
    [InlineData("{ \"error\": { \"code\": \"fair_use_limit_reached\" } }", HostedAiState.FairUseLimitReached)]
    public void Map402_IncludedAiCodes_MapToTheirStates(string body, HostedAiState expected)
        => Assert.Equal(expected, HostedAiErrorMapper.Map402(body));

    [Fact]
    public void Map402_NonJsonBody_IsNeutralUnavailable()
    {
        // A non-JSON 402 body is a money-shaped refusal with no readable reason. It must be reported
        // neutrally - never as "out of credits" (issue #1360).
        Assert.Equal(HostedAiState.Unavailable, HostedAiErrorMapper.Map402("Payment Required"));
    }
}
