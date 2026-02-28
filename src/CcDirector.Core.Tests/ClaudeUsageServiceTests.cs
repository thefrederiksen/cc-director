using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests;

public class ClaudeUsageServiceTests
{
    [Fact]
    public void ParseUsageResponse_FullResponse_ParsesAllFields()
    {
        var json = """
            {
                "five_hour": { "utilization": 6.0, "resets_at": "2025-11-04T04:59:59Z" },
                "seven_day": { "utilization": 35.0, "resets_at": "2025-11-06T03:59:59Z" },
                "seven_day_opus": { "utilization": 12.5, "resets_at": "2025-11-06T03:59:59Z" }
            }
            """;

        var account = new ClaudeAccount
        {
            Id = "test-id",
            Label = "Test",
            SubscriptionType = "max",
            RateLimitTier = "default_claude_max_20x",
        };

        var result = ClaudeUsageService.ParseUsageResponse(json, account);

        Assert.Equal("test-id", result.AccountId);
        Assert.Equal("Test", result.AccountLabel);
        Assert.Equal("max", result.SubscriptionType);
        Assert.Equal(6.0, result.FiveHourUtilization);
        Assert.NotNull(result.FiveHourResetsAt);
        Assert.Equal(35.0, result.SevenDayUtilization);
        Assert.NotNull(result.SevenDayResetsAt);
        Assert.Equal(12.5, result.OpusUtilization);
        Assert.NotNull(result.OpusResetsAt);
        Assert.False(result.IsStale);
        Assert.True(result.HasData);
        Assert.Equal("", result.StaleReason);
    }

    [Fact]
    public void ParseUsageResponse_NullResetTimes_ParsesAsNull()
    {
        var json = """
            {
                "five_hour": { "utilization": 0.0, "resets_at": null },
                "seven_day": { "utilization": 0.0, "resets_at": null },
                "seven_day_opus": { "utilization": 0.0, "resets_at": null }
            }
            """;

        var account = new ClaudeAccount { Id = "test", Label = "Test" };
        var result = ClaudeUsageService.ParseUsageResponse(json, account);

        Assert.Equal(0.0, result.FiveHourUtilization);
        Assert.Null(result.FiveHourResetsAt);
        Assert.Equal(0.0, result.SevenDayUtilization);
        Assert.Null(result.SevenDayResetsAt);
        Assert.Equal(0.0, result.OpusUtilization);
        Assert.Null(result.OpusResetsAt);
    }

    [Fact]
    public void ParseUsageResponse_MissingOpus_OpusIsNull()
    {
        var json = """
            {
                "five_hour": { "utilization": 10.0, "resets_at": "2025-11-04T04:59:59Z" },
                "seven_day": { "utilization": 20.0, "resets_at": "2025-11-06T03:59:59Z" }
            }
            """;

        var account = new ClaudeAccount { Id = "test", Label = "Test" };
        var result = ClaudeUsageService.ParseUsageResponse(json, account);

        Assert.Equal(10.0, result.FiveHourUtilization);
        Assert.Equal(20.0, result.SevenDayUtilization);
        Assert.Null(result.OpusUtilization);
        Assert.Null(result.OpusResetsAt);
    }

    [Fact]
    public void ParseUsageResponse_HighUtilization_ParsesCorrectly()
    {
        var json = """
            {
                "five_hour": { "utilization": 95.5, "resets_at": "2025-11-04T04:59:59Z" },
                "seven_day": { "utilization": 100.0, "resets_at": "2025-11-06T03:59:59Z" }
            }
            """;

        var account = new ClaudeAccount { Id = "test", Label = "Test" };
        var result = ClaudeUsageService.ParseUsageResponse(json, account);

        Assert.Equal(95.5, result.FiveHourUtilization);
        Assert.Equal(100.0, result.SevenDayUtilization);
    }

    [Fact]
    public void ParseUsageResponse_EmptyJson_DefaultsToZero()
    {
        var json = "{}";

        var account = new ClaudeAccount { Id = "test", Label = "Test" };
        var result = ClaudeUsageService.ParseUsageResponse(json, account);

        Assert.Equal(0.0, result.FiveHourUtilization);
        Assert.Null(result.FiveHourResetsAt);
        Assert.Equal(0.0, result.SevenDayUtilization);
        Assert.Null(result.SevenDayResetsAt);
        Assert.Null(result.OpusUtilization);
    }

    [Fact]
    public void ParseUsageResponse_MissingUtilization_DefaultsToZero()
    {
        var json = """
            {
                "five_hour": { "resets_at": "2025-11-04T04:59:59Z" },
                "seven_day": { "resets_at": "2025-11-06T03:59:59Z" }
            }
            """;

        var account = new ClaudeAccount { Id = "test", Label = "Test" };
        var result = ClaudeUsageService.ParseUsageResponse(json, account);

        Assert.Equal(0.0, result.FiveHourUtilization);
        Assert.Equal(0.0, result.SevenDayUtilization);
    }

    [Fact]
    public void ParseUsageResponse_SetsAccountMetadata()
    {
        var json = """
            {
                "five_hour": { "utilization": 5.0, "resets_at": "2025-11-04T04:59:59Z" },
                "seven_day": { "utilization": 15.0, "resets_at": "2025-11-06T03:59:59Z" }
            }
            """;

        var account = new ClaudeAccount
        {
            Id = "acc-123",
            Label = "Work Account",
            SubscriptionType = "pro",
            RateLimitTier = "default_claude_pro",
        };

        var result = ClaudeUsageService.ParseUsageResponse(json, account);

        Assert.Equal("acc-123", result.AccountId);
        Assert.Equal("Work Account", result.AccountLabel);
        Assert.Equal("pro", result.SubscriptionType);
        Assert.Equal("default_claude_pro", result.RateLimitTier);
    }

    [Fact]
    public void ParseUsageResponse_ResetTimeFormat_ParsesIso8601()
    {
        var json = """
            {
                "five_hour": { "utilization": 1.0, "resets_at": "2025-11-04T04:59:59Z" },
                "seven_day": { "utilization": 2.0, "resets_at": "2025-11-06T03:59:59+00:00" }
            }
            """;

        var account = new ClaudeAccount { Id = "test", Label = "Test" };
        var result = ClaudeUsageService.ParseUsageResponse(json, account);

        Assert.NotNull(result.FiveHourResetsAt);
        Assert.Equal(2025, result.FiveHourResetsAt!.Value.Year);
        Assert.Equal(11, result.FiveHourResetsAt!.Value.Month);
        Assert.Equal(4, result.FiveHourResetsAt!.Value.Day);

        Assert.NotNull(result.SevenDayResetsAt);
        Assert.Equal(2025, result.SevenDayResetsAt!.Value.Year);
    }

    [Fact]
    public void ParseUsageResponse_FullResponse_HasDataIsTrue()
    {
        var json = """
            {
                "five_hour": { "utilization": 10.0, "resets_at": "2025-11-04T04:59:59Z" },
                "seven_day": { "utilization": 20.0, "resets_at": "2025-11-06T03:59:59Z" }
            }
            """;

        var account = new ClaudeAccount { Id = "test", Label = "Test" };
        var result = ClaudeUsageService.ParseUsageResponse(json, account);

        Assert.True(result.HasData);
        Assert.False(result.IsStale);
        Assert.Equal("", result.StaleReason);
    }

    [Fact]
    public void CreateStaleInfo_NeverFetched_HasDataIsFalse()
    {
        var store = new ClaudeAccountStore(Path.Combine(Path.GetTempPath(), $"usage_test_{Guid.NewGuid():N}", "accounts.json"));
        store.Load();
        var service = new ClaudeUsageService(store);

        var account = new ClaudeAccount { Id = "test", Label = "Test" };
        var result = service.CreateStaleInfo(account, "Token expired");

        Assert.False(result.HasData);
        Assert.True(result.IsStale);
        Assert.Equal("Token expired", result.StaleReason);
        Assert.Equal("test", result.AccountId);

        service.Dispose();
    }

    [Fact]
    public void ParseUsageResponse_NullRootElement_Throws()
    {
        var json = "null";
        var account = new ClaudeAccount { Id = "test", Label = "Test" };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ClaudeUsageService.ParseUsageResponse(json, account));
        Assert.Contains("Null", ex.Message);
        Assert.Contains("instead of Object", ex.Message);
    }

    [Fact]
    public void ParseUsageResponse_NullOpusProperty_DoesNotThrow()
    {
        // This is the exact crash scenario: API returns "seven_day_opus": null
        // TryGetProperty succeeds (property exists) but the value is JSON null,
        // so calling TryGetProperty on it throws InvalidOperationException.
        var json = """
            {
                "five_hour": { "utilization": 36.0, "resets_at": "2026-02-28T02:59:59Z" },
                "seven_day": { "utilization": 23.0, "resets_at": "2026-03-06T03:00:00Z" },
                "seven_day_opus": null
            }
            """;

        var account = new ClaudeAccount { Id = "test", Label = "Test" };
        var result = ClaudeUsageService.ParseUsageResponse(json, account);

        Assert.Equal(36.0, result.FiveHourUtilization);
        Assert.Equal(23.0, result.SevenDayUtilization);
        Assert.Null(result.OpusUtilization);
        Assert.Null(result.OpusResetsAt);
        Assert.True(result.HasData);
    }

    [Fact]
    public void ParseUsageResponse_RealApiResponse_ParsesCorrectly()
    {
        // Actual response captured from https://api.anthropic.com/api/oauth/usage
        var json = """
            {
                "five_hour": {"utilization": 36.0, "resets_at": "2026-02-28T02:59:59.760173+00:00"},
                "seven_day": {"utilization": 23.0, "resets_at": "2026-03-06T03:00:00.760190+00:00"},
                "seven_day_oauth_apps": null,
                "seven_day_opus": null,
                "seven_day_sonnet": {"utilization": 0.0, "resets_at": "2026-03-06T20:00:00.760198+00:00"},
                "seven_day_cowork": null,
                "iguana_necktie": null,
                "extra_usage": {"is_enabled": true, "monthly_limit": 7000, "used_credits": 7155.0, "utilization": 100.0}
            }
            """;

        var account = new ClaudeAccount
        {
            Id = "test-id",
            Label = "TestOrg",
            SubscriptionType = "max",
            RateLimitTier = "default_claude_max_20x",
        };

        var result = ClaudeUsageService.ParseUsageResponse(json, account);

        Assert.Equal(36.0, result.FiveHourUtilization);
        Assert.NotNull(result.FiveHourResetsAt);
        Assert.Equal(23.0, result.SevenDayUtilization);
        Assert.NotNull(result.SevenDayResetsAt);
        Assert.Null(result.OpusUtilization);
        Assert.Null(result.OpusResetsAt);
        // Extra usage: API returns cents, should be divided by 100
        Assert.Equal(71.55, result.ExtraUsageSpent);
        Assert.Equal(70.0, result.ExtraUsageLimit);
        Assert.True(result.HasData);
        Assert.False(result.IsStale);
    }

    [Fact]
    public void ParseUsageResponse_ExtraUsage_ConvertsCentsToDollars()
    {
        var json = """
            {
                "five_hour": { "utilization": 10.0, "resets_at": "2025-11-04T04:59:59Z" },
                "seven_day": { "utilization": 20.0, "resets_at": "2025-11-06T03:59:59Z" },
                "extra_usage": { "is_enabled": true, "monthly_limit": 7000, "used_credits": 7155.0, "utilization": 100.0 }
            }
            """;

        var account = new ClaudeAccount { Id = "test", Label = "Test" };
        var result = ClaudeUsageService.ParseUsageResponse(json, account);

        Assert.Equal(71.55, result.ExtraUsageSpent);
        Assert.Equal(70.0, result.ExtraUsageLimit);
    }

    [Fact]
    public void ParseUsageResponse_NoExtraUsage_NullValues()
    {
        var json = """
            {
                "five_hour": { "utilization": 10.0, "resets_at": "2025-11-04T04:59:59Z" },
                "seven_day": { "utilization": 20.0, "resets_at": "2025-11-06T03:59:59Z" }
            }
            """;

        var account = new ClaudeAccount { Id = "test", Label = "Test" };
        var result = ClaudeUsageService.ParseUsageResponse(json, account);

        Assert.Null(result.ExtraUsageSpent);
        Assert.Null(result.ExtraUsageLimit);
    }

    [Fact]
    public void ParseUsageResponse_NullExtraUsage_NullValues()
    {
        var json = """
            {
                "five_hour": { "utilization": 10.0, "resets_at": "2025-11-04T04:59:59Z" },
                "seven_day": { "utilization": 20.0, "resets_at": "2025-11-06T03:59:59Z" },
                "extra_usage": null
            }
            """;

        var account = new ClaudeAccount { Id = "test", Label = "Test" };
        var result = ClaudeUsageService.ParseUsageResponse(json, account);

        Assert.Null(result.ExtraUsageSpent);
        Assert.Null(result.ExtraUsageLimit);
    }

    [Fact]
    public void ParseUsageResponse_AllNullProperties_DefaultsToZero()
    {
        // All optional sections are null
        var json = """
            {
                "five_hour": null,
                "seven_day": null,
                "seven_day_opus": null
            }
            """;

        var account = new ClaudeAccount { Id = "test", Label = "Test" };
        var result = ClaudeUsageService.ParseUsageResponse(json, account);

        Assert.Equal(0.0, result.FiveHourUtilization);
        Assert.Null(result.FiveHourResetsAt);
        Assert.Equal(0.0, result.SevenDayUtilization);
        Assert.Null(result.SevenDayResetsAt);
        Assert.Null(result.OpusUtilization);
        Assert.True(result.HasData);
    }
}
