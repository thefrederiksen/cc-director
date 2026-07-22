using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The dictation-inbound marker on the desktop (Director) side. Two things are proven here:
///   1. The Director's <see cref="DictationLockReader"/> agrees with the Gateway's real writer
///      (<see cref="VoiceUploadStore"/>): a PENDING marker reads as inbound, a terminal marker clears.
///      This is the cross-format contract behind the informational orange "receiving a dictation"
///      overlay - if the Gateway ever changes the record shape, this test catches the drift.
///   2. The marker is INFORMATIONAL ONLY. Sends are never refused because of it: this is a
///      single-operator tool, and a collision between the operator's own phone dictation and their own
///      typed send is theirs to make, not the Director's to police (issue #1308 removed the old
///      enforcement, which had also falsely blocked desktop dictation whenever the marker wedged).
/// </summary>
public sealed class DictationDesktopLockTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- 1. cross-format: the Director reader agrees with the Gateway writer ------------------------

    [Fact]
    public void Reader_AgreesWithGatewayWriter_PendingLocks_TerminalUnlocks()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-dictlock-xfmt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new VoiceUploadStore(root, TenantId.Local);
            var sid = Guid.NewGuid().ToString();
            var uploadId = Guid.NewGuid().ToString();
            store.Register(uploadId);

            store.MarkPending(uploadId, sid);
            Assert.True(DictationLockReader.IsSessionLocked(root, sid), "a PENDING marker written by the Gateway must read as dictation-inbound");

            store.MarkDelivered(uploadId, submitted: true, movedOn: false, transcript: "hi");
            Assert.False(DictationLockReader.IsSessionLocked(root, sid), "a DELIVERED tombstone must clear");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Reader_AgreesWithGatewayWriter_AbandonedUnlocks()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-dictlock-xfmt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new VoiceUploadStore(root, TenantId.Local);
            var sid = Guid.NewGuid().ToString();
            var uploadId = Guid.NewGuid().ToString();
            store.Register(uploadId);
            store.MarkPending(uploadId, sid);
            Assert.True(DictationLockReader.IsSessionLocked(root, sid));

            store.MarkAbandoned(uploadId, "user_cancelled");
            Assert.False(DictationLockReader.IsSessionLocked(root, sid), "an ABANDONED tombstone must clear");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    // ---- 2. the marker never blocks a send ----------------------------------------------------------

    [Collection("DirectorRoot")]
    public sealed class MarkerDoesNotBlockSends
    {
        private static (SessionManager sm, Session session) NewSession()
        {
            var sm = new SessionManager(new Core.Configuration.AgentOptions());
            var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
            return (sm, session);
        }

        private static DirectorCommand PromptCommand(string sessionId) => new()
        {
            CommandId = "cmd-lock",
            Verb = "prompt",
            SessionId = sessionId,
            PayloadJson = JsonSerializer.Serialize(new PromptRequest { Text = "typed while a dictation arrives", AppendEnter = true }, Json),
        };

        [Fact]
        public async Task Executor_UserInputWhileDictationInbound_Succeeds()
        {
            var (sm, session) = NewSession();
            Session.DictationLockCheck = id => id == session.Id; // a dictation is inbound...
            try
            {
                // ...and the operator's typed send goes through anyway - the marker only paints the rail orange.
                var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", PromptCommand(session.Id.ToString()), source: SendSource.UserInput);

                Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            }
            finally
            {
                Session.DictationLockCheck = null;
                sm.Dispose();
            }
        }

        [Fact]
        public async Task SendTextAsync_AllSourcesGoThroughWhileDictationInbound()
        {
            var (sm, session) = NewSession();
            Session.DictationLockCheck = id => id == session.Id;
            try
            {
                await session.SendTextAsync("typed", SendSource.UserInput);
                await session.SendTextAsync("typed default"); // default is UserInput
                await session.SendTextAsync("delivered", SendSource.Delivery);
                await session.SendTextAsync("internal", SendSource.Framework);
            }
            finally
            {
                Session.DictationLockCheck = null;
                sm.Dispose();
            }
        }
    }
}
