using CcDirector.Core.Storage;
using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Retirement regression for issue #509. <see cref="MobileCaptureHealthLog.Persist"/> used to append every
/// capture-health record - INCLUDING the customer's cleaned transcript - to the host-global
/// <c>dictation/sessions/YYYY-MM-DD.jsonl</c> flat file, which had to be SKIPPED on hosted because that one
/// unpartitioned file mixed every tenant's transcript at rest. Issue #509 retired that flat file entirely: the
/// transcript is now stored per-tenant in the <c>dictation_transcripts</c> table by the transcription service,
/// and this hook is a log-only diagnostic. So the flat file must now be written on NEITHER surface - the old
/// hosted skip-gate is gone because there is nothing left to gate.
///
/// These pin the retirement in BOTH directions the old gate cared about: it must stay unwritten on hosted (no
/// cross-tenant transcript text at rest) AND on self-host (the flat file is genuinely gone, not merely gated).
///
/// In the <c>GatewayHostedMode</c> collection because it sets the process-wide storage root and hosted-mode
/// variables (that collection runs alone, so no other test reads the root or mode this one is flipping).
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
    public void Persist_OnHosted_WritesNothingToTheRetiredFlatLog()
    {
        // Still true after the retirement, now because the flat file is gone entirely rather than gated: no
        // capture-health record lands at rest, so two tenants can neither share the one daily file nor contend
        // on the one static lock.
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
    public void Persist_OnSelfHost_AlsoWritesNothingToTheRetiredFlatLog()
    {
        // The other direction: the flat log is RETIRED, not merely hosted-gated, so even single-tenant
        // self-host no longer writes it. The transcript that used to justify this file is now stored
        // per-tenant by the transcription service (dictation_transcripts), so nothing is lost.
        Environment.SetEnvironmentVariable(GatewayHostedMode.HostedEnvVar, "0");

        MobileCaptureHealthLog.Persist(
            uploadId: "self-host-upload", source: "mobile", recordedMs: 5_000, decodedSeconds: 4.5,
            sourceBytes: 4096, audioBytes: 8192, cleaned: "self host words");

        Thread.Sleep(250); // grace for any (mis)queued background append to land before we assert emptiness
        Assert.Empty(SessionLogLines());
    }

    // ===== helpers =================================================================================

    private static string SessionLogPath() => Path.Combine(
        CcStorage.DictationSessions(), DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl");

    private static string[] SessionLogLines()
    {
        var path = SessionLogPath();
        return File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
    }
}
