using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

public sealed class ClaudeUsageService : IDisposable
{
    private readonly ClaudeAccountStore _store;
    private readonly HttpClient _httpClient;
    private readonly Timer _pollTimer;
    private FileSystemWatcher? _credentialsWatcher;
    private readonly Dictionary<string, ClaudeUsageInfo> _lastKnownUsage = new();
    private bool _disposed;

    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string BetaHeader = "oauth-2025-04-20";
    private const string UserAgent = "claude-code/2.0.32";
    private const int PollIntervalMs = 60_000;

    public event Action<List<ClaudeUsageInfo>>? UsageUpdated;

    public ClaudeUsageService(ClaudeAccountStore store, HttpClient? httpClient = null)
    {
        _store = store;
        _httpClient = httpClient ?? new HttpClient();
        _pollTimer = new Timer(OnPollTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        FileLog.Write("[ClaudeUsageService] Start: beginning usage polling");
        _pollTimer.Change(0, PollIntervalMs);
        StartCredentialsWatcher();
    }

    public void Stop()
    {
        FileLog.Write("[ClaudeUsageService] Stop: stopping usage polling");
        _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
        StopCredentialsWatcher();
    }

    private void StartCredentialsWatcher()
    {
        FileLog.Write("[ClaudeUsageService] StartCredentialsWatcher");
        try
        {
            var credPath = ClaudeAccountStore.GetDefaultCredentialsPath();
            var dir = Path.GetDirectoryName(credPath);
            var file = Path.GetFileName(credPath);

            if (dir == null || !Directory.Exists(dir))
            {
                FileLog.Write("[ClaudeUsageService] StartCredentialsWatcher: credentials directory does not exist");
                return;
            }

            _credentialsWatcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _credentialsWatcher.Changed += OnCredentialsChanged;
            _credentialsWatcher.Created += OnCredentialsChanged;
            FileLog.Write($"[ClaudeUsageService] StartCredentialsWatcher: watching {credPath}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeUsageService] StartCredentialsWatcher FAILED: {ex.Message}");
        }
    }

    private void StopCredentialsWatcher()
    {
        if (_credentialsWatcher != null)
        {
            _credentialsWatcher.EnableRaisingEvents = false;
            _credentialsWatcher.Dispose();
            _credentialsWatcher = null;
        }
    }

    private void OnCredentialsChanged(object sender, FileSystemEventArgs e)
    {
        FileLog.Write($"[ClaudeUsageService] OnCredentialsChanged: {e.ChangeType}");
        try
        {
            // Small delay to let the file finish writing
            Thread.Sleep(500);
            _store.RefreshActiveTokenFromCredentials();

            // Trigger an immediate poll after credentials change
            _pollTimer.Change(1000, PollIntervalMs);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeUsageService] OnCredentialsChanged FAILED: {ex.Message}");
        }
    }

    private async void OnPollTimer(object? state)
    {
        try
        {
            await PollAllAccountsAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeUsageService] OnPollTimer FAILED: {ex.Message}");
        }
    }

    public async Task PollAllAccountsAsync()
    {
        FileLog.Write("[ClaudeUsageService] PollAllAccountsAsync: polling all accounts");
        var accounts = _store.GetAll();
        var results = new List<ClaudeUsageInfo>();

        foreach (var account in accounts)
        {
            var usage = await FetchUsageForAccountAsync(account);
            results.Add(usage);
        }

        FileLog.Write($"[ClaudeUsageService] PollAllAccountsAsync: fetched {results.Count} results");
        UsageUpdated?.Invoke(results);
    }

    internal async Task<ClaudeUsageInfo> FetchUsageForAccountAsync(ClaudeAccount account)
    {
        FileLog.Write($"[ClaudeUsageService] FetchUsageForAccountAsync: account={account.Label}");

        if (string.IsNullOrEmpty(account.AccessToken))
        {
            FileLog.Write($"[ClaudeUsageService] FetchUsageForAccountAsync: no token for account '{account.Label}'");
            return CreateStaleInfo(account, "No token");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
            request.Headers.TryAddWithoutValidation("anthropic-beta", BetaHeader);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(request);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                FileLog.Write($"[ClaudeUsageService] FetchUsageForAccountAsync: 401 for account '{account.Label}' - token expired");
                return CreateStaleInfo(account, "Token expired");
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var info = ParseUsageResponse(json, account);
            _lastKnownUsage[account.Id] = info;
            FileLog.Write($"[ClaudeUsageService] FetchUsageForAccountAsync: account={account.Label}, 5h={info.FiveHourUtilization}%, 7d={info.SevenDayUtilization}%");
            return info;
        }
        catch (HttpRequestException ex)
        {
            FileLog.Write($"[ClaudeUsageService] FetchUsageForAccountAsync FAILED: {ex.Message}");
            return CreateStaleInfo(account, ex.Message);
        }
        catch (TaskCanceledException)
        {
            FileLog.Write($"[ClaudeUsageService] FetchUsageForAccountAsync: timeout for account '{account.Label}'");
            return CreateStaleInfo(account, "Timeout");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeUsageService] FetchUsageForAccountAsync FAILED: {ex.Message}");
            return CreateStaleInfo(account, ex.Message);
        }
    }

    private ClaudeUsageInfo CreateStaleInfo(ClaudeAccount account, string reason)
    {
        // Return last known data if available, marked as stale
        if (_lastKnownUsage.TryGetValue(account.Id, out var lastKnown))
        {
            return new ClaudeUsageInfo
            {
                AccountId = lastKnown.AccountId,
                AccountLabel = lastKnown.AccountLabel,
                SubscriptionType = lastKnown.SubscriptionType,
                RateLimitTier = lastKnown.RateLimitTier,
                FiveHourUtilization = lastKnown.FiveHourUtilization,
                FiveHourResetsAt = lastKnown.FiveHourResetsAt,
                SevenDayUtilization = lastKnown.SevenDayUtilization,
                SevenDayResetsAt = lastKnown.SevenDayResetsAt,
                OpusUtilization = lastKnown.OpusUtilization,
                OpusResetsAt = lastKnown.OpusResetsAt,
                FetchedAt = lastKnown.FetchedAt,
                IsStale = true,
            };
        }

        return new ClaudeUsageInfo
        {
            AccountId = account.Id,
            AccountLabel = account.Label,
            SubscriptionType = account.SubscriptionType,
            RateLimitTier = account.RateLimitTier,
            IsStale = true,
            FetchedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Parses the usage API response JSON.
    /// Expected format: { "five_hour": { "utilization": 6.0, "resets_at": "..." }, "seven_day": { ... }, "seven_day_opus": { ... } }
    /// </summary>
    internal static ClaudeUsageInfo ParseUsageResponse(string json, ClaudeAccount account)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        double fiveHourUtil = 0;
        DateTimeOffset? fiveHourResets = null;
        double sevenDayUtil = 0;
        DateTimeOffset? sevenDayResets = null;
        double? opusUtil = null;
        DateTimeOffset? opusResets = null;

        if (root.TryGetProperty("five_hour", out var fiveHour))
        {
            fiveHourUtil = fiveHour.TryGetProperty("utilization", out var u) ? u.GetDouble() : 0;
            fiveHourResets = ParseResetTime(fiveHour);
        }

        if (root.TryGetProperty("seven_day", out var sevenDay))
        {
            sevenDayUtil = sevenDay.TryGetProperty("utilization", out var u) ? u.GetDouble() : 0;
            sevenDayResets = ParseResetTime(sevenDay);
        }

        if (root.TryGetProperty("seven_day_opus", out var opus))
        {
            opusUtil = opus.TryGetProperty("utilization", out var u) ? u.GetDouble() : 0;
            opusResets = ParseResetTime(opus);
        }

        return new ClaudeUsageInfo
        {
            AccountId = account.Id,
            AccountLabel = account.Label,
            SubscriptionType = account.SubscriptionType,
            RateLimitTier = account.RateLimitTier,
            FiveHourUtilization = fiveHourUtil,
            FiveHourResetsAt = fiveHourResets,
            SevenDayUtilization = sevenDayUtil,
            SevenDayResetsAt = sevenDayResets,
            OpusUtilization = opusUtil,
            OpusResetsAt = opusResets,
            FetchedAt = DateTimeOffset.UtcNow,
            IsStale = false,
        };
    }

    private static DateTimeOffset? ParseResetTime(JsonElement element)
    {
        if (!element.TryGetProperty("resets_at", out var ra))
            return null;

        if (ra.ValueKind == JsonValueKind.Null)
            return null;

        var str = ra.GetString();
        if (string.IsNullOrEmpty(str))
            return null;

        return DateTimeOffset.TryParse(str, out var dt) ? dt : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _pollTimer.Dispose();
        _httpClient.Dispose();
    }
}
