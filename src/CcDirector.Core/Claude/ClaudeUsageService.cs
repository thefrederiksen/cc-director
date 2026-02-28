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
    private readonly Dictionary<string, int> _consecutiveFailures = new();
    private bool _disposed;

    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string BetaHeader = "oauth-2025-04-20";
    private const string UserAgent = "claude-code/2.0.32";
    private const int PollIntervalMs = 60_000;
    private const int MaxConsecutiveFailures = 3;

    public event Action<List<ClaudeUsageInfo>>? UsageUpdated;

    public ClaudeUsageService(ClaudeAccountStore store, HttpClient? httpClient = null)
    {
        _store = store;
        _httpClient = httpClient ?? new HttpClient();
        _pollTimer = new Timer(OnPollTimer, null, Timeout.Infinite, Timeout.Infinite);
        _store.TokensRefreshed += OnTokensRefreshed;
    }

    public void Start()
    {
        FileLog.Write("[ClaudeUsageService] Start: beginning usage polling");

        // Check for expired tokens at startup and proactively refresh
        CheckAndRefreshExpiredTokens();

        _pollTimer.Change(0, PollIntervalMs);
        StartCredentialsWatcher();
    }

    private void CheckAndRefreshExpiredTokens()
    {
        FileLog.Write("[ClaudeUsageService] CheckAndRefreshExpiredTokens");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var account in _store.GetAll())
        {
            if (account.ExpiresAt > 0 && account.ExpiresAt < now)
            {
                FileLog.Write($"[ClaudeUsageService] CheckAndRefreshExpiredTokens: account '{account.Label}' token expired, refreshing from credentials");
                _store.RefreshActiveTokenFromCredentials();
                break; // RefreshActiveTokenFromCredentials updates the active account
            }
        }
    }

    private void OnTokensRefreshed()
    {
        FileLog.Write("[ClaudeUsageService] OnTokensRefreshed: clearing failure counts, triggering immediate poll");
        _consecutiveFailures.Clear();
        _pollTimer.Change(1000, PollIntervalMs);
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
            // Skip accounts with too many consecutive failures
            if (_consecutiveFailures.TryGetValue(account.Id, out var failures) && failures >= MaxConsecutiveFailures)
            {
                FileLog.Write($"[ClaudeUsageService] PollAllAccountsAsync: skipping account '{account.Label}' ({failures} consecutive failures)");
                results.Add(CreateStaleInfo(account, "Token expired"));
                continue;
            }

            var usage = await FetchUsageForAccountAsync(account);
            results.Add(usage);
        }

        FileLog.Write($"[ClaudeUsageService] PollAllAccountsAsync: fetched {results.Count} results");
        UsageUpdated?.Invoke(results);
    }

    internal async Task<ClaudeUsageInfo> FetchUsageForAccountAsync(ClaudeAccount account, bool isRetry = false)
    {
        FileLog.Write($"[ClaudeUsageService] FetchUsageForAccountAsync: account={account.Label}, isRetry={isRetry}");

        if (string.IsNullOrEmpty(account.AccessToken))
        {
            FileLog.Write($"[ClaudeUsageService] FetchUsageForAccountAsync: no token for account '{account.Label}'");
            TrackFailure(account.Id);
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

                // Try to recover by reading fresh credentials (only once)
                if (!isRetry)
                {
                    FileLog.Write($"[ClaudeUsageService] FetchUsageForAccountAsync: attempting token recovery for '{account.Label}'");
                    var oldToken = account.AccessToken;
                    _store.RefreshActiveTokenFromCredentials();

                    if (account.AccessToken != oldToken)
                    {
                        FileLog.Write($"[ClaudeUsageService] FetchUsageForAccountAsync: token changed, retrying for '{account.Label}'");
                        return await FetchUsageForAccountAsync(account, isRetry: true);
                    }
                }

                TrackFailure(account.Id);
                return CreateStaleInfo(account, "Token expired");
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var info = ParseUsageResponse(json, account);
            _lastKnownUsage[account.Id] = info;
            _consecutiveFailures.Remove(account.Id);
            FileLog.Write($"[ClaudeUsageService] FetchUsageForAccountAsync: account={account.Label}, 5h={info.FiveHourUtilization}%, 7d={info.SevenDayUtilization}%");
            return info;
        }
        catch (HttpRequestException ex)
        {
            FileLog.Write($"[ClaudeUsageService] FetchUsageForAccountAsync FAILED: {ex.Message}");
            TrackFailure(account.Id);
            return CreateStaleInfo(account, ex.Message);
        }
        catch (TaskCanceledException)
        {
            FileLog.Write($"[ClaudeUsageService] FetchUsageForAccountAsync: timeout for account '{account.Label}'");
            TrackFailure(account.Id);
            return CreateStaleInfo(account, "Timeout");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeUsageService] FetchUsageForAccountAsync FAILED: {ex.Message}");
            TrackFailure(account.Id);
            return CreateStaleInfo(account, ex.Message);
        }
    }

    private void TrackFailure(string accountId)
    {
        _consecutiveFailures.TryGetValue(accountId, out var count);
        _consecutiveFailures[accountId] = count + 1;
        FileLog.Write($"[ClaudeUsageService] TrackFailure: account={accountId}, consecutiveFailures={count + 1}");
    }

    internal ClaudeUsageInfo CreateStaleInfo(ClaudeAccount account, string reason)
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
                ExtraUsageSpent = lastKnown.ExtraUsageSpent,
                ExtraUsageLimit = lastKnown.ExtraUsageLimit,
                FetchedAt = lastKnown.FetchedAt,
                IsStale = true,
                StaleReason = reason,
                HasData = true,
            };
        }

        return new ClaudeUsageInfo
        {
            AccountId = account.Id,
            AccountLabel = account.Label,
            SubscriptionType = account.SubscriptionType,
            RateLimitTier = account.RateLimitTier,
            IsStale = true,
            StaleReason = reason,
            HasData = false,
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

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"API returned {root.ValueKind} instead of Object");

        double fiveHourUtil = 0;
        DateTimeOffset? fiveHourResets = null;
        double sevenDayUtil = 0;
        DateTimeOffset? sevenDayResets = null;
        double? opusUtil = null;
        DateTimeOffset? opusResets = null;

        if (root.TryGetProperty("five_hour", out var fiveHour) && fiveHour.ValueKind == JsonValueKind.Object)
        {
            fiveHourUtil = fiveHour.TryGetProperty("utilization", out var u) ? u.GetDouble() : 0;
            fiveHourResets = ParseResetTime(fiveHour);
        }

        if (root.TryGetProperty("seven_day", out var sevenDay) && sevenDay.ValueKind == JsonValueKind.Object)
        {
            sevenDayUtil = sevenDay.TryGetProperty("utilization", out var u) ? u.GetDouble() : 0;
            sevenDayResets = ParseResetTime(sevenDay);
        }

        if (root.TryGetProperty("seven_day_opus", out var opus) && opus.ValueKind == JsonValueKind.Object)
        {
            opusUtil = opus.TryGetProperty("utilization", out var u) ? u.GetDouble() : 0;
            opusResets = ParseResetTime(opus);
        }

        // Parse extra_usage: API returns cents, convert to dollars
        double? extraSpent = null;
        double? extraLimit = null;
        if (root.TryGetProperty("extra_usage", out var extra) && extra.ValueKind == JsonValueKind.Object)
        {
            if (extra.TryGetProperty("used_credits", out var uc))
                extraSpent = uc.GetDouble() / 100.0;
            if (extra.TryGetProperty("monthly_limit", out var ml))
                extraLimit = ml.GetDouble() / 100.0;
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
            ExtraUsageSpent = extraSpent,
            ExtraUsageLimit = extraLimit,
            FetchedAt = DateTimeOffset.UtcNow,
            IsStale = false,
            HasData = true,
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

        _store.TokensRefreshed -= OnTokensRefreshed;
        Stop();
        _pollTimer.Dispose();
        _httpClient.Dispose();
    }
}
