using CcDirector.Gateway.Push;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The VAPID key store generates one P-256 key pair on first run and reuses it thereafter. A phone's
/// push subscription only keeps working while the Gateway signs with the SAME private key, so the
/// keys must survive a restart. These tests use an isolated temp key file.
/// </summary>
public sealed class WebPushVapidStoreTests : IDisposable
{
    private readonly string _storePath =
        Path.Combine(Path.GetTempPath(), $"vapid-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_storePath)) File.Delete(_storePath);
    }

    [Fact]
    public void Generate_PublicKeyIsA65ByteUncompressedPoint()
    {
        var store = new WebPushVapidStore(_storePath);

        var publicKey = FromBase64Url(store.PublicKey);
        Assert.Equal(65, publicKey.Length);
        Assert.Equal(0x04, publicKey[0]); // uncompressed-point marker
    }

    [Fact]
    public void Generate_PrivateKeyIsA32ByteScalar()
    {
        var store = new WebPushVapidStore(_storePath);

        var privateKey = FromBase64Url(store.PrivateKey);
        Assert.Equal(32, privateKey.Length);
    }

    [Fact]
    public void Reload_ReturnsTheSameKeyPair()
    {
        var first = new WebPushVapidStore(_storePath);
        var second = new WebPushVapidStore(_storePath);

        Assert.Equal(first.PublicKey, second.PublicKey);
        Assert.Equal(first.PrivateKey, second.PrivateKey);
    }

    [Fact]
    public void SeparateStores_GenerateDistinctKeyPairs()
    {
        var otherPath = Path.Combine(Path.GetTempPath(), $"vapid-{Guid.NewGuid():N}.json");
        try
        {
            var a = new WebPushVapidStore(_storePath);
            var b = new WebPushVapidStore(otherPath);
            Assert.NotEqual(a.PublicKey, b.PublicKey);
            Assert.NotEqual(a.PrivateKey, b.PrivateKey);
        }
        finally
        {
            if (File.Exists(otherPath)) File.Delete(otherPath);
        }
    }

    private static byte[] FromBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(normalized);
    }
}
