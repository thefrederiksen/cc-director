using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice.Interfaces;

namespace CcDirector.Core.Voice.Services;

/// <summary>
/// Speech-to-text service using OpenAI Whisper API.
/// Requires OPENAI_API_KEY environment variable.
/// </summary>
public class OpenAiSttService : ISpeechToText, IDisposable
{
    private const string WhisperApiUrl = "https://api.openai.com/v1/audio/transcriptions";
    private const int TimeoutSeconds = 60;

    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private bool? _isAvailable;
    private string? _unavailableReason;

    public OpenAiSttService()
    {
        _apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds)
        };
    }

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            if (_isAvailable == null)
                CheckAvailability();
            return _isAvailable!.Value;
        }
    }

    /// <inheritdoc />
    public string? UnavailableReason
    {
        get
        {
            if (_isAvailable == null)
                CheckAvailability();
            return _unavailableReason;
        }
    }

    /// <inheritdoc />
    public async Task<string> TranscribeAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        FileLog.Write($"[OpenAiSttService] TranscribeAsync: {audioPath}");

        if (!IsAvailable)
        {
            throw new InvalidOperationException($"OpenAI STT not available: {UnavailableReason}");
        }

        if (!File.Exists(audioPath))
        {
            throw new FileNotFoundException("Audio file not found", audioPath);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, WhisperApiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var content = new MultipartFormDataContent();

            // Add the audio file
            var audioBytes = await File.ReadAllBytesAsync(audioPath, cancellationToken);
            var audioContent = new ByteArrayContent(audioBytes);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(audioContent, "file", Path.GetFileName(audioPath));

            // Add the model parameter
            content.Add(new StringContent("whisper-1"), "model");

            request.Content = content;

            FileLog.Write("[OpenAiSttService] Sending request to OpenAI...");
            var response = await _httpClient.SendAsync(request, cancellationToken);

            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                FileLog.Write($"[OpenAiSttService] API error: {response.StatusCode} - {responseText}");
                throw new InvalidOperationException($"OpenAI API error: {response.StatusCode}");
            }

            // Parse the response
            using var doc = JsonDocument.Parse(responseText);
            var text = doc.RootElement.GetProperty("text").GetString() ?? "";

            FileLog.Write($"[OpenAiSttService] Transcription: {text.Length} chars");
            return text.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[OpenAiSttService] TranscribeAsync FAILED: {ex}");
            throw;
        }
    }

    private void CheckAvailability()
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            _isAvailable = false;
            _unavailableReason = "OPENAI_API_KEY environment variable not set. Set it to use OpenAI Whisper for speech-to-text.";
            FileLog.Write("[OpenAiSttService] No API key found");
            return;
        }

        _isAvailable = true;
        _unavailableReason = null;
        FileLog.Write("[OpenAiSttService] OpenAI STT is available");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
