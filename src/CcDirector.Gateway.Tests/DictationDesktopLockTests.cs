using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1181, Task 3b: the DESKTOP-side (Director) enforced dictation lock. Two things are proven here:
///   1. The Director's <see cref="DictationLockReader"/> agrees with the Gateway's real writer
///      (<see cref="VoiceUploadStore"/>): a PENDING marker locks, a terminal marker unlocks. This is the
///      cross-format contract - if the Gateway ever changes the record shape, this test catches the drift.
///   2. The shared <see cref="SessionCommandExecutor"/> refuses a human (<see cref="SendSource.UserInput"/>)
///      prompt into a locked session with <see cref="DirectorCommandStatus.Locked"/>, while the dictation's
///      own arrival (<see cref="SendSource.Delivery"/>) is exempt; and <see cref="Session.SendTextAsync"/>
///      throws for a locked human send but not for the exempt sources.
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
            var store = new VoiceUploadStore(root);
            var sid = Guid.NewGuid().ToString();
            var uploadId = Guid.NewGuid().ToString();
            store.Register(uploadId);

            store.MarkPending(uploadId, sid);
            Assert.True(DictationLockReader.IsSessionLocked(root, sid), "a PENDING marker written by the Gateway must lock the Director's read");

            store.MarkDelivered(uploadId, submitted: true, movedOn: false, transcript: "hi");
            Assert.False(DictationLockReader.IsSessionLocked(root, sid), "a DELIVERED tombstone must unlock");
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
            var store = new VoiceUploadStore(root);
            var sid = Guid.NewGuid().ToString();
            var uploadId = Guid.NewGuid().ToString();
            store.Register(uploadId);
            store.MarkPending(uploadId, sid);
            Assert.True(DictationLockReader.IsSessionLocked(root, sid));

            store.MarkAbandoned(uploadId, "user_cancelled");
            Assert.False(DictationLockReader.IsSessionLocked(root, sid), "an ABANDONED tombstone must unlock");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    // ---- 2. executor + Session enforcement ---------------------------------------------------------

    [Collection("DirectorRoot")]
    public sealed class Enforcement
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
        public async Task Executor_UserInputIntoLockedSession_ReturnsLockedWithMessage()
        {
            var (sm, session) = NewSession();
            Session.DictationLockCheck = id => id == session.Id;
            try
            {
                var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", PromptCommand(session.Id.ToString()), source: SendSource.UserInput);

                Assert.Equal(DirectorCommandStatus.Locked, result.Status);
                Assert.Equal(SessionLockedException.LockMessage, result.Error);
            }
            finally
            {
                Session.DictationLockCheck = null;
                sm.Dispose();
            }
        }

        [Fact]
        public async Task Executor_DeliveryIntoLockedSession_IsExemptAndSucceeds()
        {
            var (sm, session) = NewSession();
            Session.DictationLockCheck = id => id == session.Id; // locked...
            try
            {
                // ...but the dictation's OWN arrival (Delivery) is exempt - it is what the lock is held for.
                var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", PromptCommand(session.Id.ToString()), source: SendSource.Delivery);

                Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            }
            finally
            {
                Session.DictationLockCheck = null;
                sm.Dispose();
            }
        }

        [Fact]
        public async Task Executor_UserInputIntoUnlockedSession_Succeeds()
        {
            var (sm, session) = NewSession();
            Session.DictationLockCheck = _ => false; // nothing inbound
            try
            {
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
        public async Task SendTextAsync_UserInputIntoLocked_Throws_ButExemptSourcesDoNot()
        {
            var (sm, session) = NewSession();
            Session.DictationLockCheck = id => id == session.Id;
            try
            {
                await Assert.ThrowsAsync<SessionLockedException>(() => session.SendTextAsync("hi", SendSource.UserInput));
                await Assert.ThrowsAsync<SessionLockedException>(() => session.SendTextAsync("hi")); // default is UserInput

                // The dictation delivery and framework sends must go through even while locked.
                await session.SendTextAsync("delivered", SendSource.Delivery);
                await session.SendTextAsync("internal", SendSource.Internal);
            }
            finally
            {
                Session.DictationLockCheck = null;
                sm.Dispose();
            }
        }
    }
}
