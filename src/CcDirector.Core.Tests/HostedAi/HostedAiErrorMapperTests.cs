using CcDirector.Core.HostedAi;
using Xunit;

namespace CcDirector.Core.Tests.HostedAi;

/// <summary>
/// Issue #938 (epic #937): the runtime 402 mapper. It must branch on the machine-readable
/// <c>code</c> (never the shared <c>type</c>): <c>insufficient_credits</c> -> NeedsCredits,
/// <c>monthly_limit_reached</c> -> CapReached, and any other 402 -> NeedsCredits (the common case).
/// The code parse reads both the nested and flat OpenAI-compatible shapes and defaults safely.
/// </summary>
public sealed class HostedAiErrorMapperTests
{
    [Theory]
    [InlineData("insufficient_credits", HostedAiState.NeedsCredits)]
    [InlineData("monthly_limit_reached", HostedAiState.CapReached)]
    [InlineData("MONTHLY_LIMIT_REACHED", HostedAiState.CapReached)]
    [InlineData("  monthly_limit_reached  ", HostedAiState.CapReached)]
    [InlineData("some_other_code", HostedAiState.NeedsCredits)]
    [InlineData("", HostedAiState.NeedsCredits)]
    [InlineData(null, HostedAiState.NeedsCredits)]
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
    public void ParseErrorCode_MissingOrUnparseable_DefaultsToInsufficientCredits(string body)
        => Assert.Equal(HostedAiErrorMapper.InsufficientCreditsCode, HostedAiErrorMapper.ParseErrorCode(body));

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

    [Fact]
    public void Map402_NonJsonBody_NeedsCredits()
    {
        // A non-JSON 402 body still means out of credits (the transcription path's proven behavior).
        Assert.Equal(HostedAiState.NeedsCredits, HostedAiErrorMapper.Map402("Payment Required"));
    }
}
