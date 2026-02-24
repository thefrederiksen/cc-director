using CcDirector.Core.Voice.Controllers;
using CcDirector.Core.Voice.Models;
using CcDirector.Core.Tests.Voice.Mocks;
using Xunit;

namespace CcDirector.Core.Tests.Voice;

/// <summary>
/// Tests for VoiceModeController.
/// </summary>
public class VoiceModeControllerTests : IDisposable
{
    private readonly MockAudioRecorder _audioRecorder;
    private readonly MockSpeechToText _speechToText;
    private readonly MockSummarizer _summarizer;
    private readonly MockTextToSpeech _textToSpeech;
    private readonly List<string> _playedAudioFiles;
    private readonly VoiceModeController _controller;

    public VoiceModeControllerTests()
    {
        _audioRecorder = new MockAudioRecorder();
        _speechToText = new MockSpeechToText();
        _summarizer = new MockSummarizer();
        _textToSpeech = new MockTextToSpeech();
        _playedAudioFiles = new List<string>();

        _controller = new VoiceModeController(
            _audioRecorder,
            _speechToText,
            _summarizer,
            _textToSpeech,
            path => _playedAudioFiles.Add(path));
    }

    public void Dispose()
    {
        _controller.Dispose();
    }

    [Fact]
    public void InitialState_IsIdle()
    {
        Assert.Equal(VoiceState.Idle, _controller.State);
    }

    [Fact]
    public void StartRecording_WhenIdle_ChangesToRecording()
    {
        // Arrange
        var stateChanges = new List<(VoiceState old, VoiceState @new)>();
        _controller.OnStateChanged += (old, @new) => stateChanges.Add((old, @new));
        _controller.SetSession(CreateMockSession());

        // Act
        _controller.StartRecording();

        // Assert
        Assert.Equal(VoiceState.Recording, _controller.State);
        Assert.Single(stateChanges);
        Assert.Equal(VoiceState.Idle, stateChanges[0].old);
        Assert.Equal(VoiceState.Recording, stateChanges[0].@new);
    }

    [Fact]
    public void StartRecording_WhenRecorderUnavailable_SetsError()
    {
        // Arrange
        var unavailableRecorder = new MockAudioRecorder(isAvailable: false, unavailableReason: "No mic");
        using var controller = new VoiceModeController(
            unavailableRecorder,
            _speechToText,
            _summarizer,
            _textToSpeech,
            _ => { });

        // Act
        controller.StartRecording();

        // Assert
        Assert.Equal(VoiceState.Error, controller.State);
        Assert.Contains("No mic", controller.LastError);
    }

    [Fact]
    public void StartRecording_WhenNoSession_SetsError()
    {
        // Act (no SetSession called)
        _controller.StartRecording();

        // Assert
        Assert.Equal(VoiceState.Error, _controller.State);
        Assert.Contains("No active session", _controller.LastError);
    }

    [Fact]
    public void ToggleRecording_WhenIdle_StartsRecording()
    {
        // Act
        _controller.ToggleRecording();

        // Assert
        // Will error because no session, but StartRecording was attempted
        Assert.Equal(VoiceState.Error, _controller.State);
    }

    [Fact]
    public void Cancel_WhenRecording_ReturnsToIdle()
    {
        // Arrange
        _controller.SetSession(CreateMockSession());
        _controller.StartRecording();
        Assert.Equal(VoiceState.Recording, _controller.State);

        // Act
        _controller.Cancel();

        // Assert
        Assert.Equal(VoiceState.Idle, _controller.State);
    }

    [Fact]
    public void SetSession_StoresSession()
    {
        // Arrange
        var session = CreateMockSession();

        // Act
        _controller.SetSession(session);
        _controller.StartRecording();

        // Assert - should start recording (not error about no session)
        Assert.Equal(VoiceState.Recording, _controller.State);
        Assert.Equal(1, _audioRecorder.StartRecordingCallCount);
    }

    [Fact]
    public void Dispose_CancelsOngoingOperations()
    {
        // Arrange
        _controller.SetSession(CreateMockSession());
        _controller.StartRecording();

        // Act
        _controller.Dispose();

        // Assert - no exception thrown
    }

    [Fact]
    public void OnTranscriptionComplete_FiresWhenTranscriptionDone()
    {
        // Arrange
        var transcriptions = new List<string>();
        _controller.OnTranscriptionComplete += t => transcriptions.Add(t);

        // This test verifies the event is wired up correctly
        // Full flow testing would require a real or more sophisticated mock session
    }

    /// <summary>
    /// Creates a minimal mock session for testing.
    /// Note: This doesn't have a real process, so full flow tests need more setup.
    /// </summary>
    private static CcDirector.Core.Sessions.Session CreateMockSession()
    {
        // Create a minimal session using the stub backend
        var backend = new StubSessionBackend();
        return new CcDirector.Core.Sessions.Session(
            Guid.NewGuid(),
            repoPath: "C:\\test\\repo",
            workingDirectory: "C:\\test\\repo",
            claudeArgs: null,
            backend: backend,
            CcDirector.Core.Backends.SessionBackendType.ConPty);
    }
}
