using CcDirector.Gateway.Push;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The push subscription store is the Gateway's set of subscribed devices, keyed by endpoint and
/// persisted so opt-in survives a restart. These tests use an isolated temp store.
/// </summary>
public sealed class PushSubscriptionStoreTests : IDisposable
{
    private readonly string _storePath =
        Path.Combine(Path.GetTempPath(), $"pushsub-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_storePath)) File.Delete(_storePath);
    }

    [Fact]
    public void Add_NewEndpoint_ReturnsTrueAndStoresIt()
    {
        var store = new PushSubscriptionStore(_storePath);

        var isNew = store.Add("https://push.example/aaa", "p256-a", "auth-a");

        Assert.True(isNew);
        Assert.Equal(1, store.Count);
        var only = Assert.Single(store.All());
        Assert.Equal("https://push.example/aaa", only.Endpoint);
        Assert.Equal("p256-a", only.P256dh);
        Assert.Equal("auth-a", only.Auth);
    }

    [Fact]
    public void Add_SameEndpointAgain_RefreshesInPlace_ReturnsFalse()
    {
        var store = new PushSubscriptionStore(_storePath);
        store.Add("https://push.example/aaa", "p256-old", "auth-old");

        var isNew = store.Add("https://push.example/aaa", "p256-new", "auth-new");

        Assert.False(isNew);
        Assert.Equal(1, store.Count);
        var only = Assert.Single(store.All());
        Assert.Equal("p256-new", only.P256dh);
        Assert.Equal("auth-new", only.Auth);
    }

    [Fact]
    public void Remove_ExistingEndpoint_ReturnsTrueThenFalse()
    {
        var store = new PushSubscriptionStore(_storePath);
        store.Add("https://push.example/aaa", "p", "a");

        Assert.True(store.Remove("https://push.example/aaa"));
        Assert.Equal(0, store.Count);
        Assert.False(store.Remove("https://push.example/aaa"));
    }

    [Fact]
    public void Add_MissingKeys_Throws()
    {
        var store = new PushSubscriptionStore(_storePath);

        Assert.Throws<ArgumentException>(() => store.Add("", "p", "a"));
        Assert.Throws<ArgumentException>(() => store.Add("https://push.example/aaa", "", "a"));
        Assert.Throws<ArgumentException>(() => store.Add("https://push.example/aaa", "p", ""));
    }

    [Fact]
    public void Subscriptions_SurviveAReload()
    {
        var first = new PushSubscriptionStore(_storePath);
        first.Add("https://push.example/aaa", "p256-a", "auth-a");
        first.Add("https://push.example/bbb", "p256-b", "auth-b");

        var reloaded = new PushSubscriptionStore(_storePath);

        Assert.Equal(2, reloaded.Count);
        Assert.Contains(reloaded.All(), s => s.Endpoint == "https://push.example/aaa" && s.P256dh == "p256-a");
        Assert.Contains(reloaded.All(), s => s.Endpoint == "https://push.example/bbb" && s.P256dh == "p256-b");
    }
}
