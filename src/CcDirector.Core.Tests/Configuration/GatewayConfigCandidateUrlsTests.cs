using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Tests for <see cref="GatewayConfig.CandidateUrls"/> - the ordered set of gateway addresses a
/// client tries when connecting (issue #1233). The active/manual <see cref="GatewayConfig.Url"/>
/// comes first (a manual override wins), then the discovered fallbacks in <see cref="GatewayConfig.Urls"/>,
/// de-duplicated. These construct the config directly, so they exercise the ordering with no file I/O.
/// </summary>
public class GatewayConfigCandidateUrlsTests
{
    [Fact]
    public void CandidateUrls_UrlOnly_ReturnsJustTheUrl()
    {
        var cfg = new GatewayConfig { Url = "http://machine:7878" };

        Assert.Equal(new[] { "http://machine:7878" }, cfg.CandidateUrls);
    }

    [Fact]
    public void CandidateUrls_UrlFirstThenFallbacks_InOrder()
    {
        var cfg = new GatewayConfig
        {
            Url = "http://machine:7878",
            Urls = new[] { "https://machine.tail.ts.net:7878", "http://192.168.1.20:7878" },
        };

        Assert.Equal(
            new[] { "http://machine:7878", "https://machine.tail.ts.net:7878", "http://192.168.1.20:7878" },
            cfg.CandidateUrls);
    }

    [Fact]
    public void CandidateUrls_ActiveUrlAlsoInFallbackList_IsNotDuplicated()
    {
        var cfg = new GatewayConfig
        {
            Url = "http://machine:7878",
            Urls = new[] { "http://machine:7878", "https://machine.tail.ts.net:7878" },
        };

        Assert.Equal(
            new[] { "http://machine:7878", "https://machine.tail.ts.net:7878" },
            cfg.CandidateUrls);
    }

    [Fact]
    public void CandidateUrls_DuplicateDiffersOnlyByCase_IsNotDuplicated()
    {
        var cfg = new GatewayConfig
        {
            Url = "http://MACHINE:7878",
            Urls = new[] { "http://machine:7878" },
        };

        Assert.Single(cfg.CandidateUrls);
        Assert.Equal("http://MACHINE:7878", cfg.CandidateUrls[0]);
    }

    [Fact]
    public void CandidateUrls_NoActiveUrl_ReturnsFallbacksOnly()
    {
        var cfg = new GatewayConfig
        {
            Url = "",
            Urls = new[] { "https://machine.tail.ts.net:7878", "http://192.168.1.20:7878" },
        };

        Assert.Equal(
            new[] { "https://machine.tail.ts.net:7878", "http://192.168.1.20:7878" },
            cfg.CandidateUrls);
    }

    [Fact]
    public void CandidateUrls_NothingConfigured_IsEmpty()
    {
        var cfg = new GatewayConfig();

        Assert.Empty(cfg.CandidateUrls);
    }
}
