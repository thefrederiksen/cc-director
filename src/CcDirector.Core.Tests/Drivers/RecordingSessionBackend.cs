using System.Text;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;

namespace CcDirector.Core.Tests.Drivers;

internal sealed class RecordingSessionBackend : ISessionBackend
{
    public int ProcessId => 1234;
    public string Status => "Running";
    public bool IsRunning => true;
    public bool HasExited => false;
    public CircularTerminalBuffer? Buffer { get; init; }

    public List<byte[]> WrittenBytes { get; } = new();
    public List<string> SentTexts { get; } = new();
    public List<string> SubmittedTexts { get; } = new();
    public List<string> Starts { get; } = new();
    public int EnterCount { get; private set; }
    public int LostEnterCount { get; private set; }
    public List<(short Columns, short Rows)> Resizes { get; } = new();
    public int ShutdownCount { get; private set; }
    public bool SimulateBlindSubmitPath { get; init; }
    public TimeSpan BlindSubmitDelay { get; init; } = TimeSpan.FromMilliseconds(50);
    public RecordingEchoScript EchoScript { get; } = new();
    public string ParkedComposerText { get; private set; } = string.Empty;

    public event Action<string>? StatusChanged;
    public event Action<int>? ProcessExited;

    public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null)
    {
        Starts.Add($"{executable}|{args}|{workingDir}|{cols}|{rows}");
        StatusChanged?.Invoke(Status);
    }

    public void Write(byte[] data)
    {
        WrittenBytes.Add(data.ToArray());
        if (IsEnter(data))
        {
            EnterCount++;
            SubmitComposerIfReady();
            return;
        }

        if (IsEscape(data))
        {
            EchoScript.ClearComposer();
            return;
        }

        EchoScript.RecordTypedText(Encoding.UTF8.GetString(data));
        EchoScript.ScheduleEcho(Buffer, data);
    }

    public async Task SendTextAsync(string text)
    {
        SentTexts.Add(text);
        if (!SimulateBlindSubmitPath)
            return;

        Write(Encoding.UTF8.GetBytes(text));
        await Task.Delay(BlindSubmitDelay);
        Write([0x0D]);
    }

    public Task SendEnterAsync()
    {
        EnterCount++;
        SubmitComposerIfReady();
        return Task.CompletedTask;
    }

    public void Resize(short cols, short rows)
    {
        Resizes.Add((cols, rows));
    }

    public Task GracefulShutdownAsync(int timeoutMs = 5000)
    {
        ShutdownCount++;
        ProcessExited?.Invoke(0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }

    private void SubmitComposerIfReady()
    {
        if (EchoScript.IsComposerReady)
        {
            SubmittedTexts.Add(EchoScript.ComposerText);
            EchoScript.ClearComposer();
            return;
        }

        LostEnterCount++;
        ParkedComposerText = EchoScript.ComposerText;
    }

    private static bool IsEnter(byte[] data) => data.Length == 1 && data[0] == 0x0D;

    private static bool IsEscape(byte[] data) => data.Length == 1 && data[0] == 0x1B;
}

internal sealed class RecordingEchoScript
{
    private readonly Queue<RecordingEchoStep> _steps = new();
    private RecordingEchoStep _defaultStep = RecordingEchoStep.Immediate();

    public string ComposerText { get; private set; } = string.Empty;
    public bool IsComposerReady { get; private set; }

    public void UseDefault(RecordingEchoStep step)
    {
        _defaultStep = step;
    }

    public void Enqueue(RecordingEchoStep step)
    {
        _steps.Enqueue(step);
    }

    public void RecordTypedText(string text)
    {
        ComposerText += text;
        IsComposerReady = false;
    }

    public void ScheduleEcho(CircularTerminalBuffer? buffer, byte[] typedBytes)
    {
        if (buffer is null)
            return;

        var typedText = Encoding.UTF8.GetString(typedBytes);
        var step = _steps.Count > 0 ? _steps.Dequeue() : _defaultStep;
        _ = EchoAsync(buffer, typedText, step);
    }

    public void ClearComposer()
    {
        ComposerText = string.Empty;
        IsComposerReady = false;
    }

    private async Task EchoAsync(CircularTerminalBuffer buffer, string typedText, RecordingEchoStep step)
    {
        if (step.Mode == RecordingEchoMode.Withheld)
            return;

        if (!string.IsNullOrEmpty(step.PlaceholderText))
            buffer.Write(Encoding.UTF8.GetBytes(step.PlaceholderText));

        if (step.Delay > TimeSpan.Zero)
            await Task.Delay(step.Delay);

        var echoText = step.EchoText ?? typedText;
        buffer.Write(Encoding.UTF8.GetBytes(echoText));
        IsComposerReady = step.AcceptsSubmit;
    }
}

internal sealed record RecordingEchoStep(
    RecordingEchoMode Mode,
    TimeSpan Delay,
    string? PlaceholderText,
    string? EchoText,
    bool AcceptsSubmit)
{
    public static RecordingEchoStep Immediate() =>
        new(RecordingEchoMode.Delayed, TimeSpan.Zero, null, null, true);

    public static RecordingEchoStep Delayed(TimeSpan delay) =>
        new(RecordingEchoMode.Delayed, delay, null, null, true);

    public static RecordingEchoStep Withheld() =>
        new(RecordingEchoMode.Withheld, TimeSpan.Zero, null, null, false);

    public static RecordingEchoStep RepaintingPlaceholder(string placeholderText, TimeSpan delay) =>
        new(RecordingEchoMode.RepaintingPlaceholder, delay, placeholderText, null, true);

    public static RecordingEchoStep CustomEcho(string echoText) =>
        new(RecordingEchoMode.Delayed, TimeSpan.Zero, null, echoText, true);

    public static RecordingEchoStep SlashCorrupted(string text) =>
        new(RecordingEchoMode.SlashCorrupted, TimeSpan.Zero, null, "/" + text, false);
}

internal enum RecordingEchoMode
{
    Delayed,
    Withheld,
    RepaintingPlaceholder,
    SlashCorrupted,
}
