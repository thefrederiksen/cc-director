using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;

namespace CcDirector.Gateway.Push;

/// <summary>
/// Sends one encrypted, VAPID-signed Web Push message to a single subscription. The seam
/// (<see cref="IWebPushSender"/>) lets the notifier's fan-out and expired-subscription pruning be
/// unit-tested with a fake, while production uses <see cref="VapidWebPushSender"/> over
/// <c>Lib.Net.Http.WebPush</c>.
/// </summary>
public interface IWebPushSender
{
    /// <summary>
    /// Deliver <paramref name="payloadJson"/> to <paramref name="subscription"/>. Throws
    /// <see cref="PushServiceClientException"/> (carrying the push service's HTTP status) when the
    /// push service rejects it - the notifier reads <c>StatusCode</c> to prune Gone/NotFound
    /// subscriptions.
    /// </summary>
    Task SendAsync(StoredPushSubscription subscription, string payloadJson, CancellationToken cancellationToken);
}

/// <summary>
/// Production <see cref="IWebPushSender"/>: signs each message with the Gateway's VAPID key pair and
/// encrypts it to the subscription's client keys via <c>Lib.Net.Http.WebPush</c>. One instance is
/// held for the Gateway's lifetime (it owns a pooled <see cref="HttpClient"/> and the VAPID
/// authentication), so construct it once and reuse it.
/// </summary>
public sealed class VapidWebPushSender : IWebPushSender, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly VapidAuthentication _authentication;
    private readonly PushServiceClient _client;

    /// <param name="publicKey">VAPID public key (unpadded base64url, 65-byte point).</param>
    /// <param name="privateKey">VAPID private key (unpadded base64url, 32-byte scalar). Never logged.</param>
    /// <param name="subject">The VAPID contact - a <c>mailto:</c> or <c>https:</c> URI identifying this
    /// application server to the push service.</param>
    public VapidWebPushSender(string publicKey, string privateKey, string subject)
    {
        if (string.IsNullOrWhiteSpace(publicKey))
            throw new ArgumentException("publicKey is required", nameof(publicKey));
        if (string.IsNullOrWhiteSpace(privateKey))
            throw new ArgumentException("privateKey is required", nameof(privateKey));

        _authentication = new VapidAuthentication(publicKey, privateKey) { Subject = subject };
        _client = new PushServiceClient(_http) { DefaultAuthentication = _authentication };
    }

    public Task SendAsync(StoredPushSubscription subscription, string payloadJson, CancellationToken cancellationToken)
    {
        var pushSubscription = new PushSubscription { Endpoint = subscription.Endpoint };
        pushSubscription.SetKey(PushEncryptionKeyName.P256DH, subscription.P256dh);
        pushSubscription.SetKey(PushEncryptionKeyName.Auth, subscription.Auth);
        return _client.RequestPushMessageDeliveryAsync(pushSubscription, new PushMessage(payloadJson), cancellationToken);
    }

    public void Dispose()
    {
        _authentication.Dispose();
        _http.Dispose();
    }
}
