using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice.Interfaces;

namespace CcDirector.Core.Voice.Services;

/// <summary>
/// Text-to-speech service using OpenAI TTS API.
/// Requires OPENAI_API_KEY environment variable.
/// </summary>
public class OpenAiTtsService : ITextToSpeech, IDisposable
{
    private const string TtsApiUrl = "https://api.openai.com/v1/audio/speech";
    private const int TimeoutSeconds = 30;

    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private readonly string _voice;
    private bool? _isAvailable;
    private string? _unavailableReason;

    /// <summary>
    /// Create an OpenAI TTS service.
    /// </summary>
    /// <param name="voice">Voice to use: alloy, echo, fable, onyx, nova, shimmer</param>
    public OpenAiTtsService(string voice = "nova")
    {
        _apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        _voice = voice;
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
    public async Task SynthesizeAsync(string text, string outputPath, CancellationToken cancellationToken = default)
    {
        FileLog.Write($"[OpenAiTtsService] SynthesizeAsync: text={text.Length} chars, output={outputPath}");

        if (!IsAvailable)
        {
            throw new InvalidOperationException($"OpenAI TTS not available: {UnavailableReason}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text cannot be empty", nameof(text));
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TtsApiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            var requestBody = new
            {
                model = "tts-1",
                input = text,
                voice = _voice,
                response_format = "wav"
            };

            var json = JsonSerializer.Serialize(requestBody);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            FileLog.Write("[OpenAiTtsService] Sending request to OpenAI...");
            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
                FileLog.Write($"[OpenAiTtsService] API error: {response.StatusCode} - {errorText}");
                throw new InvalidOperationException($"OpenAI API error: {response.StatusCode}");
            }

            // Save the audio file
            var audioBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            await File.WriteAllBytesAsync(outputPath, audioBytes, cancellationToken);

            FileLog.Write($"[OpenAiTtsService] Created: {outputPath} ({audioBytes.Length} bytes)");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[OpenAiTtsService] SynthesizeAsync FAILED: {ex.Message}");
            throw;
        }
    }

    private void CheckAvailability()
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            _isAvailable = false;
            _unavailableReason = "OPENAI_API_KEY environment variable not set.";
            FileLog.Write("[OpenAiTtsService] No API key found");
            return;
        }

        _isAvailable = true;
        _unavailableReason = null;
        FileLog.Write("[OpenAiTtsService] OpenAI TTS is available");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
