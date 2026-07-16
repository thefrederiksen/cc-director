using System.Net.Http;
using CcDirector.Core.Account;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tests for the headless account sign-in runner (the engine sibling of the wizard's SignInRunner, driven
/// by the setup CLI's 'signin' command). They run against a REAL <see cref="LoopbackLoginListener"/> on a
/// free loopback port (no backend), with the "browser" stood in by a direct HTTP call to the loopback
/// callback - the same hand-back the dev stand-in and the real backend perform. This proves the success,
/// cancel, timeout, and failure outcomes end to end, that the captured token pair is handed to the persist
/// seam ONLY on success, and that a persist failure is reported as a failure (no fallback).
/// </summary>
public sealed class AccountSignInRunnerTests
{
    private static string HandBackUrl(LoopbackLoginListener listener, string access, string refresh) =>
        $"{listener.CallbackUrl}?access_token={access}&refresh_token={refresh}";

    [Fact]
    public async Task RunAsync_BrowserHandsBackCredential_ReturnsSignedIn()
    {
        var listener = new LoopbackLoginListener();
        using var http = new HttpClient();

        var runner = new AccountSignInRunner(
            listenerFactory: () => listener,
            openBrowser: url => { _ = Task.Run(() => http.GetAsync(HandBackUrl(listener, "dev-access", "dev-refresh"))); },
            persistCredential: _ => { /* outcome-only test */ },
            timeout: TimeSpan.FromSeconds(10));

        var result = await runner.RunAsync();

        Assert.Equal(AccountSignInOutcome.SignedIn, result.Outcome);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task RunAsync_Success_PersistsTheExactCapturedTokenPair()
    {
        var listener = new LoopbackLoginListener();
        using var http = new HttpClient();
        DevThrottleTokens? persisted = null;

        var runner = new AccountSignInRunner(
            listenerFactory: () => listener,
            openBrowser: url => { _ = Task.Run(() => http.GetAsync(HandBackUrl(listener, "captured-access", "captured-refresh"))); },
            persistCredential: tokens => persisted = tokens,
            timeout: TimeSpan.FromSeconds(10));

        var result = await runner.RunAsync();

        Assert.True(result.Succeeded);
        Assert.NotNull(persisted);
        Assert.Equal("captured-access", persisted!.AccessToken);
        Assert.Equal("captured-refresh", persisted.RefreshToken);
    }

    [Fact]
    public async Task RunAsync_UserCancels_ReturnsCancelled_AndDoesNotPersist()
    {
        var listener = new LoopbackLoginListener();
        using var cts = new CancellationTokenSource();
        var persistCalled = false;

        var runner = new AccountSignInRunner(
            listenerFactory: () => listener,
            openBrowser: _ => cts.CancelAfter(TimeSpan.FromMilliseconds(100)),   // never hands back; the wait ends only by cancellation
            persistCredential: _ => persistCalled = true,
            timeout: TimeSpan.FromSeconds(30));

        var result = await runner.RunAsync(cts.Token);

        Assert.Equal(AccountSignInOutcome.Cancelled, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.False(persistCalled);
    }

    [Fact]
    public async Task RunAsync_NoHandBackWithinTimeout_ReturnsTimedOut_AndDoesNotPersist()
    {
        var listener = new LoopbackLoginListener();
        var persistCalled = false;

        var runner = new AccountSignInRunner(
            listenerFactory: () => listener,
            openBrowser: _ => { /* the browser never hands anything back */ },
            persistCredential: _ => persistCalled = true,
            timeout: TimeSpan.FromMilliseconds(150));

        var result = await runner.RunAsync();

        Assert.Equal(AccountSignInOutcome.TimedOut, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.False(persistCalled);
    }

    [Fact]
    public async Task RunAsync_BrowserCannotOpen_ReturnsFailed_AndDoesNotPersist()
    {
        var listener = new LoopbackLoginListener();
        var persistCalled = false;

        var runner = new AccountSignInRunner(
            listenerFactory: () => listener,
            openBrowser: _ => throw new InvalidOperationException("no browser"),
            persistCredential: _ => persistCalled = true,
            timeout: TimeSpan.FromSeconds(10));

        var result = await runner.RunAsync();

        Assert.Equal(AccountSignInOutcome.Failed, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.False(persistCalled);
    }

    [Fact]
    public async Task RunAsync_PersistFails_ReturnsFailed()
    {
        var listener = new LoopbackLoginListener();
        using var http = new HttpClient();

        var runner = new AccountSignInRunner(
            listenerFactory: () => listener,
            openBrowser: url => { _ = Task.Run(() => http.GetAsync(HandBackUrl(listener, "a", "b"))); },
            persistCredential: _ => throw new InvalidOperationException("store unavailable"),
            timeout: TimeSpan.FromSeconds(10));

        var result = await runner.RunAsync();

        Assert.Equal(AccountSignInOutcome.Failed, result.Outcome);
        Assert.False(result.Succeeded);
    }
}
