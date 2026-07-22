using CcDirector.Core.Storage;
using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// MTR-10 Gap H3. <see cref="MobileCaptureHealthLog.Persist"/> is the ONE hosted write path into the
/// dictation session log - both the Voice complete path and the durable Terminal/Chat Send complete path
/// route through it. That log appends every record to ONE process/user file
/// (<c>dictation/sessions/YYYY-MM-DD.jsonl</c>) under a single static lock, with no tenant in the path,
/// no tenant in the API and no per-tenant lock. The record carries <c>CleanedTranscript</c> - the
/// customer's spoken text - so on a multi-tenant hosted Gateway two tenants' records would mix at rest in
/// that one file and contend on that one lock. It is a LOCAL self-host diagnostic aid - write-only, no
/// reader, no hosted read route - so the fix mirrors the archive skip beside it (#1985): stop the write on
/// hosted.
///
/// These tests pin BOTH directions of that gate, because a guard has two failure directions: under-refusing
/// on hosted mixes cross-tenant transcript text at rest, and over-refusing on self-host silently deletes the
/// diagnostic the log exists to provide.
///
/// In the <c>GatewayHostedMode</c> collection because it sets the process-wide <c>CC_GATEWAY_HOSTED</c>
/// variable (that collection runs alone, so no other test reads the mode - or the storage root - this one is
/// flipping).
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class MobileCaptureHealthLogHostedTests : IDisposable
{
    private readonly string? _prevHosted;
    private readonly string? _prevRoot;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cc-capturehealth-hosted-tests", Guid.NewGuid().ToString("N"));

    public MobileCaptureHealthLogHostedTests()
    {
        _prevHosted = Environment.GetEnvironmentVariable(GatewayHostedMode.HostedEnvVar);
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(GatewayHostedMode.HostedEnvVar, _prevHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* scratch cleanup is best-effort */ }
    }

    [Fact]
    public void Persist_OnHosted_TwoTenantsWriteNothingToTheSharedLog()
    {
        // The Gap H3 property: on hosted, no capture-health record lands at rest, so two tenants can neither
        // SHARE the one daily file nor contend on the one static lock. Before the fix BOTH records append to
        // that single file, mixing tenant A's and tenant B's cleaned transcript text; after the fix the
        // hosted write is skipped and there is nothing to mix. (The gate returns before the background
        // append is ever queued, so the file is never created - a bounded grace still lets any stray append
        // land before we assert.)
        Environment.SetEnvironmentVariable(GatewayHostedMode.HostedEnvVar, "1");

        MobileCaptureHealthLog.Persist(
            uploadId: "tenant-a-upload", source: "mobile", recordedMs: 5_000, decodedSeconds: 4.5,
            sourceBytes: 4096, audioBytes: 8192, cleaned: "tenant A confidential words");
        MobileCaptureHealthLog.Persist(
            uploadId: "tenant-b-upload", source: "mobile-send", recordedMs: 6_000, decodedSeconds: 5.5,
            sourceBytes: 5120, audioBytes: 9216, cleaned: "tenant B confidential words");

        Thread.Sleep(250); // grace for any (mis)queued background append to land before we assert emptiness
        Assert.Empty(SessionLogLines());
    }

    [Fact]
    public void Persist_OnSelfHost_StillWritesTheDiagnostic()
    {
        // The OTHER failure direction: the hosted gate must not become a blanket break. Self-host - and a
        // self-host Gateway - is single-tenant, and this log is the diagnostic that catches a mobile capture
        // silently dropping half the speech, so it must keep working exactly as before.
        Environment.SetEnvironmentVariable(GatewayHostedMode.HostedEnvVar, "0");

        MobileCaptureHealthLog.Persist(
            uploadId: "self-host-upload", source: "mobile", recordedMs: 5_000, decodedSeconds: 4.5,
            sourceBytes: 4096, audioBytes: 8192, cleaned: "self host words");

        var lines = WaitForSessionLogLines(expected: 1);
        Assert.Single(lines);
        Assert.Contains("self host words", lines[0]);
    }

    // ===== helpers =================================================================================

    private static string SessionLogPath() => Path.Combine(
        CcStorage.DictationSessions(), DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl");

    private static string[] SessionLogLines()
    {
        var path = SessionLogPath();
        return File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
    }

    // Persist appends on a background task, so the self-host write is not visible synchronously. Poll for it
    // up to a bounded deadline rather than sleeping a fixed amount.
    private static string[] WaitForSessionLogLines(int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var lines = SessionLogLines();
            if (lines.Length >= expected) return lines;
            Thread.Sleep(50);
        }
        return SessionLogLines();
    }
}
