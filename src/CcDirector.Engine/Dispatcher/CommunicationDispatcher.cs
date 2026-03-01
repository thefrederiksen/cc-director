using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Engine.Archival;
using CcDirector.Engine.Events;
using Microsoft.Data.Sqlite;

namespace CcDirector.Engine.Dispatcher;

public sealed class CommunicationDispatcher : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _communicationsDbPath;
    private readonly string _ccOutlookPath;
    private readonly string _ccGmailPath;
    private readonly HashSet<string> _gmailSendFromAccounts;
    private readonly int _pollIntervalSeconds;
    private readonly VaultArchiver _archiver = new();
    private Timer? _timer;
    private int _polling; // 0 = idle, 1 = polling (used with Interlocked for thread safety)

    public event Action<EngineEvent>? OnEvent;

    public CommunicationDispatcher(
        string communicationsDbPath,
        string ccOutlookPath,
        string ccGmailPath,
        IEnumerable<string> gmailSendFromAccounts,
        int pollIntervalSeconds = 5)
    {
        _communicationsDbPath = communicationsDbPath;
        _ccOutlookPath = ccOutlookPath;
        _ccGmailPath = ccGmailPath;
        _gmailSendFromAccounts = new HashSet<string>(
            gmailSendFromAccounts, StringComparer.OrdinalIgnoreCase);
        _pollIntervalSeconds = pollIntervalSeconds;

        FileLog.Write($"[CommunicationDispatcher] Gmail accounts: [{string.Join(", ", _gmailSendFromAccounts)}]");
    }

    public void Start()
    {
        FileLog.Write("[CommunicationDispatcher] Starting");
        _timer = new Timer(Poll, null, TimeSpan.Zero, TimeSpan.FromSeconds(_pollIntervalSeconds));
    }

    public void Stop()
    {
        FileLog.Write("[CommunicationDispatcher] Stopping");
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    // Timer callback -- async void is correct here (entry point/boundary, same as event handler)
    private async void Poll(object? state)
    {
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;

        try
        {
            if (!File.Exists(_communicationsDbPath))
            {
                FileLog.Write($"[CommunicationDispatcher] DB not found: {_communicationsDbPath}");
                return;
            }

            var approved = GetApprovedEmails();
            foreach (var email in approved)
            {
                await DispatchEmailAsync(email);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CommunicationDispatcher] Poll error: {ex.Message}");
            RaiseEvent(new EngineEvent(EngineEventType.Error, Message: $"Dispatcher poll error: {ex.Message}"));
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private List<ApprovedEmail> GetApprovedEmails()
    {
        var emails = new List<ApprovedEmail>();
        var connectionString = $"Data Source={_communicationsDbPath};Mode=ReadWrite";

        using var conn = new SqliteConnection(connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, ticket_number, content, email_specific, persona, send_from
            FROM communications
            WHERE status = 'approved' AND platform = 'email'
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var ticket = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            var body = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var emailSpecific = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var persona = reader.IsDBNull(4) ? "personal" : reader.GetString(4);
            var sendFrom = reader.IsDBNull(5) ? null : reader.GetString(5);

            try
            {
                var spec = JsonSerializer.Deserialize<EmailSpecific>(emailSpecific, JsonOptions);

                if (spec?.To != null && spec.To.Count > 0)
                {
                    emails.Add(new ApprovedEmail
                    {
                        Id = id,
                        TicketNumber = ticket,
                        Body = body,
                        To = string.Join(",", spec.To),
                        Cc = spec.Cc != null && spec.Cc.Count > 0 ? string.Join(",", spec.Cc) : null,
                        Bcc = spec.Bcc != null && spec.Bcc.Count > 0 ? string.Join(",", spec.Bcc) : null,
                        Subject = spec.Subject ?? "(no subject)",
                        Attachments = spec.Attachments ?? new List<string>(),
                        Persona = persona,
                        SendFrom = sendFrom
                    });
                }
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[CommunicationDispatcher] Failed to parse email_specific for ticket #{ticket}: {ex.Message}");
            }
        }

        return emails;
    }

    /// <summary>
    /// Determines whether to use cc-gmail or cc-outlook for this email.
    /// Routes to Gmail if send_from is in the configured Gmail accounts list,
    /// or if send_from contains "@gmail.com".
    /// </summary>
    private bool IsGmailEmail(ApprovedEmail email)
    {
        var sendFrom = email.SendFrom ?? email.Persona;

        // Check configured Gmail accounts
        if (_gmailSendFromAccounts.Contains(sendFrom))
            return true;

        // Check if send_from looks like a Gmail address
        if (sendFrom.Contains("@gmail.com", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private async Task DispatchEmailAsync(ApprovedEmail email)
    {
        var useGmail = IsGmailEmail(email);
        var toolName = useGmail ? "cc-gmail" : "cc-outlook";
        var toolPath = useGmail ? _ccGmailPath : _ccOutlookPath;

        FileLog.Write($"[CommunicationDispatcher] Sending ticket #{email.TicketNumber} to {email.To} via {toolName} (send_from={email.SendFrom ?? "null"}, persona={email.Persona})");

        try
        {
            var args = BuildSendArgs(email, useGmail);
            var result = await RunToolProcessAsync(toolPath, args);

            if (result.ExitCode == 0)
            {
                HandleSendSuccess(email, toolName);
            }
            else
            {
                var error = string.IsNullOrEmpty(result.Stderr) ? result.Stdout : result.Stderr;
                MarkFailed(email.Id, error);
                FileLog.Write($"[CommunicationDispatcher] Ticket #{email.TicketNumber} FAILED via {toolName}: {error}");
                RaiseEvent(new EngineEvent(EngineEventType.Error,
                    Message: $"Email ticket #{email.TicketNumber} failed via {toolName}: {error}"));
            }
        }
        catch (Exception ex)
        {
            MarkFailed(email.Id, ex.Message);
            FileLog.Write($"[CommunicationDispatcher] Ticket #{email.TicketNumber} exception: {ex.Message}");
        }
    }

    private static List<string> BuildSendArgs(ApprovedEmail email, bool useGmail)
    {
        var args = new List<string>
        {
            "send",
            "-t", email.To,
            "-s", email.Subject,
            "-b", email.Body,
            "--html"
        };

        if (!string.IsNullOrEmpty(email.Cc))
        {
            args.Add("--cc");
            args.Add(email.Cc);
        }

        if (!string.IsNullOrEmpty(email.Bcc))
        {
            args.Add("--bcc");
            args.Add(email.Bcc);
        }

        // Attachment flag differs: cc-outlook uses "-a", cc-gmail uses "--attach"
        var attachFlag = useGmail ? "--attach" : "-a";
        foreach (var attachment in email.Attachments)
        {
            if (File.Exists(attachment))
            {
                args.Add(attachFlag);
                args.Add(attachment);
            }
        }

        return args;
    }

    private static async Task<ToolProcessResult> RunToolProcessAsync(string toolPath, List<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = toolPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException($"Failed to start process: {toolPath}");

        // Read both streams concurrently to avoid deadlock when both buffers fill
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await stderrTask;
        await process.WaitForExitAsync();

        return new ToolProcessResult(process.ExitCode, stdout, stderr);
    }

    private void HandleSendSuccess(ApprovedEmail email, string toolName)
    {
        MarkPosted(email.Id);
        try
        {
            _archiver.ArchiveEmail(email.To, email.Subject, email.Body);
        }
        catch (Exception archiveEx)
        {
            FileLog.Write($"[CommunicationDispatcher] Vault archive FAILED (email still sent): {archiveEx.Message}");
        }
        FileLog.Write($"[CommunicationDispatcher] Ticket #{email.TicketNumber} sent OK via {toolName}");
        RaiseEvent(new EngineEvent(EngineEventType.CommunicationDispatched,
            Message: $"Email ticket #{email.TicketNumber} sent to {email.To} via {toolName}"));
    }

    private void MarkPosted(string id)
    {
        using var conn = new SqliteConnection($"Data Source={_communicationsDbPath};Mode=ReadWrite");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE communications
            SET status = 'posted', posted_at = @now, posted_by = 'cc-director'
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private void MarkFailed(string id, string error)
    {
        using var conn = new SqliteConnection($"Data Source={_communicationsDbPath};Mode=ReadWrite");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE communications
            SET notes = @error
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@error", $"Send failed: {error}");
        cmd.ExecuteNonQuery();
    }

    private void RaiseEvent(EngineEvent e)
    {
        try { OnEvent?.Invoke(e); }
        catch (Exception ex) { FileLog.Write($"[CommunicationDispatcher] Event handler error: {ex.Message}"); }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }

    private class ApprovedEmail
    {
        public string Id { get; set; } = "";
        public int TicketNumber { get; set; }
        public string Body { get; set; } = "";
        public string To { get; set; } = "";
        public string? Cc { get; set; }
        public string? Bcc { get; set; }
        public string Subject { get; set; } = "";
        public List<string> Attachments { get; set; } = new();
        public string Persona { get; set; } = "personal";
        public string? SendFrom { get; set; }
    }

    private class EmailSpecific
    {
        public List<string>? To { get; set; }
        public List<string>? Cc { get; set; }
        public List<string>? Bcc { get; set; }
        public string? Subject { get; set; }
        public List<string>? Attachments { get; set; }
    }
}
