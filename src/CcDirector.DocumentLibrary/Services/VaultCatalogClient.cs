using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.DocumentLibrary.Models;

namespace CcDirector.DocumentLibrary.Services;

/// <summary>
/// Spawns cc-vault processes and reads streaming stdout.
/// Follows the same pattern as ClaudeClient.ReadStreamEvents.
/// </summary>
public class VaultCatalogClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static string FindCcVault()
    {
        FileLog.Write("[VaultCatalogClient] FindCcVault: resolving path");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var binPath = Path.Combine(localAppData, "cc-director", "bin", "cc-vault.exe");
        if (File.Exists(binPath))
            return binPath;

        // Fall back to PATH
        return "cc-vault";
    }

    private static Process StartProcess(string arguments)
    {
        FileLog.Write($"[VaultCatalogClient] StartProcess: cc-vault {arguments}");
        var psi = new ProcessStartInfo
        {
            FileName = FindCcVault(),
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException("Failed to start cc-vault process");
        return process;
    }

    private static async Task<string> RunAsync(string arguments)
    {
        FileLog.Write($"[VaultCatalogClient] RunAsync: cc-vault {arguments}");
        using var process = StartProcess(arguments);
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var output = await outputTask;
        var stderr = await errorTask;
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            FileLog.Write($"[VaultCatalogClient] RunAsync FAILED: exit={process.ExitCode}, stderr={stderr}");
            throw new InvalidOperationException($"cc-vault exited with code {process.ExitCode}: {stderr}");
        }

        FileLog.Write($"[VaultCatalogClient] RunAsync: complete, {output.Length} chars");
        return output;
    }

    private static async IAsyncEnumerable<StreamEvent> ReadStreamEvents(
        Process process,
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await process.StandardOutput.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
                break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            StreamEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<StreamEvent>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue; // Skip non-JSON lines (Rich output, etc.)
            }

            if (evt is not null)
                yield return evt;
        }
    }

    /// <summary>
    /// Scan a library with streaming progress.
    /// </summary>
    public async IAsyncEnumerable<StreamEvent> ScanLibraryAsync(
        string label, [EnumeratorCancellation] CancellationToken ct)
    {
        FileLog.Write($"[VaultCatalogClient] ScanLibraryAsync: {label}");
        using var process = StartProcess($"catalog scan --library \"{label}\" --stream");

        await foreach (var evt in ReadStreamEvents(process, ct))
        {
            yield return evt;
        }

        if (ct.IsCancellationRequested && !process.HasExited)
        {
            FileLog.Write("[VaultCatalogClient] ScanLibraryAsync: cancellation requested, killing process");
            process.Kill(entireProcessTree: true);
        }
    }

    /// <summary>
    /// Summarize pending entries with streaming progress.
    /// </summary>
    public async IAsyncEnumerable<StreamEvent> SummarizeAsync(
        string label, int batchSize,
        [EnumeratorCancellation] CancellationToken ct)
    {
        FileLog.Write($"[VaultCatalogClient] SummarizeAsync: {label}, batch={batchSize}");
        using var process = StartProcess(
            $"catalog summarize --library \"{label}\" --batch {batchSize} --stream");

        await foreach (var evt in ReadStreamEvents(process, ct))
        {
            yield return evt;
        }

        if (ct.IsCancellationRequested && !process.HasExited)
        {
            FileLog.Write("[VaultCatalogClient] SummarizeAsync: cancellation requested, killing process");
            process.Kill(entireProcessTree: true);
        }
    }

    /// <summary>List all registered libraries.</summary>
    public async Task<List<Library>> ListLibrariesAsync()
    {
        FileLog.Write("[VaultCatalogClient] ListLibrariesAsync");
        var json = await RunAsync("library list --json");
        return JsonSerializer.Deserialize<List<Library>>(json, JsonOptions) ?? [];
    }

    /// <summary>Get library details with stats.</summary>
    public async Task<Library?> GetLibraryAsync(string label)
    {
        FileLog.Write($"[VaultCatalogClient] GetLibraryAsync: {label}");
        var json = await RunAsync($"library show \"{label}\" --json");
        return JsonSerializer.Deserialize<Library>(json, JsonOptions);
    }

    /// <summary>Add a new library.</summary>
    public async Task<Library?> AddLibraryAsync(string path, string label,
                                                 string category, string? owner)
    {
        FileLog.Write($"[VaultCatalogClient] AddLibraryAsync: {label} -> {path}");
        var args = $"library add \"{path}\" --label \"{label}\" --category {category} --json";
        if (!string.IsNullOrEmpty(owner))
            args += $" --owner \"{owner}\"";
        var json = await RunAsync(args);
        return JsonSerializer.Deserialize<Library>(json, JsonOptions);
    }

    /// <summary>Delete a library.</summary>
    public async Task DeleteLibraryAsync(string label)
    {
        FileLog.Write($"[VaultCatalogClient] DeleteLibraryAsync: {label}");
        await RunAsync($"library delete \"{label}\" --yes");
    }

    /// <summary>List catalog entries.</summary>
    public async Task<List<CatalogEntry>> ListEntriesAsync(
        string? library = null, string? ext = null, int count = 100)
    {
        FileLog.Write($"[VaultCatalogClient] ListEntriesAsync: library={library}, ext={ext}");
        var args = "catalog list --json";
        if (!string.IsNullOrEmpty(library))
            args += $" --library \"{library}\"";
        if (!string.IsNullOrEmpty(ext))
            args += $" --ext {ext}";
        args += $" -n {count}";
        var json = await RunAsync(args);
        return JsonSerializer.Deserialize<List<CatalogEntry>>(json, JsonOptions) ?? [];
    }

    /// <summary>Full-text search catalog entries.</summary>
    public async Task<List<CatalogEntry>> SearchAsync(string query, int count = 50)
    {
        FileLog.Write($"[VaultCatalogClient] SearchAsync: {query}");
        var json = await RunAsync($"catalog search \"{query}\" --json -n {count}");
        return JsonSerializer.Deserialize<List<CatalogEntry>>(json, JsonOptions) ?? [];
    }

    /// <summary>Get catalog stats.</summary>
    public async Task<CatalogStats?> GetStatsAsync(string? library = null)
    {
        FileLog.Write($"[VaultCatalogClient] GetStatsAsync: library={library}");
        var args = "catalog stats --json";
        if (!string.IsNullOrEmpty(library))
            args += $" --library \"{library}\"";
        var json = await RunAsync(args);
        return JsonSerializer.Deserialize<CatalogStats>(json, JsonOptions);
    }
}
