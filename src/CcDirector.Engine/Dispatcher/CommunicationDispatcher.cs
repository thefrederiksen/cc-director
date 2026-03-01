using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Engine.Events;
using Microsoft.Data.Sqlite;

namespace CcDirector.Engine.Dispatcher;

public sealed class CommunicationDispatcher : IDisposable
{
    private readonly string _communicationsDbPath;
    private readonly string _ccOutlookPath;
    private readonly int _pollIntervalSeconds;
    private Timer? _timer;
    private int _polling; // 0 = idle, 1 = polling (used with Interlocked for thread safety)

    public event Action<EngineEvent>? OnEvent;

    public CommunicationDispatcher(string communicationsDbPath, string ccOutlookPath, int pollIntervalSeconds = 5)
    {
        _communicationsDbPath = communicationsDbPath;
        _ccOutlookPath = ccOutlookPath;
        _pollIntervalSeconds = pollIntervalSeconds;
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
            SELECT id, ticket_number, content, email_specific, persona
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

            try
            {
                var spec = JsonSerializer.Deserialize<EmailSpecific>(emailSpecific,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

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
                        Persona = persona
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

    private async Task DispatchEmailAsync(ApprovedEmail email)
    {
        FileLog.Write($"[CommunicationDispatcher] Sending ticket #{email.TicketNumber} to {email.To}");

        try
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

            foreach (var attachment in email.Attachments)
            {
                if (File.Exists(attachment))
                {
                    args.Add("-a");
                    args.Add(attachment);
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = _ccOutlookPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null)
            {
                MarkFailed(email.Id, "Failed to start cc-outlook process");
                return;
            }

            // Read both streams concurrently to avoid deadlock when both buffers fill
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await stderrTask;
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                MarkPosted(email.Id);
                FileLog.Write($"[CommunicationDispatcher] Ticket #{email.TicketNumber} sent OK");
                RaiseEvent(new EngineEvent(EngineEventType.CommunicationDispatched,
                    Message: $"Email ticket #{email.TicketNumber} sent to {email.To}"));
            }
            else
            {
                var error = string.IsNullOrEmpty(stderr) ? stdout : stderr;
                MarkFailed(email.Id, error);
                FileLog.Write($"[CommunicationDispatcher] Ticket #{email.TicketNumber} FAILED: {error}");
                RaiseEvent(new EngineEvent(EngineEventType.Error,
                    Message: $"Email ticket #{email.TicketNumber} failed: {error}"));
            }
        }
        catch (Exception ex)
        {
            MarkFailed(email.Id, ex.Message);
            FileLog.Write($"[CommunicationDispatcher] Ticket #{email.TicketNumber} exception: {ex.Message}");
        }
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
