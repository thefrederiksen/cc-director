using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Manages workspace definition files as individual JSON files in the workspaces directory.
/// Each workspace is stored as {slug}.workspace.json.
/// </summary>
public class WorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string FolderPath { get; }

    public WorkspaceStore(string? folderPath = null)
    {
        FolderPath = folderPath ?? CcStorage.Workspaces();
    }

    /// <summary>
    /// Save a workspace definition. Overwrites if a file with the same slug exists.
    /// </summary>
    public bool Save(WorkspaceDefinition workspace)
    {
        var slug = ToSlug(workspace.Name);
        FileLog.Write($"[WorkspaceStore] Save: name={workspace.Name}, slug={slug}, sessions={workspace.Sessions.Count}");

        try
        {
            EnsureDirectory();

            var filePath = GetFilePath(slug);
            var json = JsonSerializer.Serialize(workspace, JsonOptions);
            File.WriteAllText(filePath, json);

            FileLog.Write($"[WorkspaceStore] Save: written to {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorkspaceStore] Save FAILED: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Load all workspace definitions, sorted by name.
    /// Skips corrupt files with a warning log.
    /// </summary>
    public List<WorkspaceDefinition> LoadAll()
    {
        FileLog.Write($"[WorkspaceStore] LoadAll: scanning {FolderPath}");

        if (!Directory.Exists(FolderPath))
        {
            FileLog.Write("[WorkspaceStore] LoadAll: folder does not exist, returning empty list");
            return new List<WorkspaceDefinition>();
        }

        var workspaces = new List<WorkspaceDefinition>();
        var files = Directory.GetFiles(FolderPath, "*.workspace.json");

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var workspace = JsonSerializer.Deserialize<WorkspaceDefinition>(json, JsonOptions);
                if (workspace != null)
                    workspaces.Add(workspace);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[WorkspaceStore] LoadAll: skipping corrupt file {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        workspaces.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        FileLog.Write($"[WorkspaceStore] LoadAll: loaded {workspaces.Count} workspaces");
        return workspaces;
    }

    /// <summary>
    /// Load a single workspace by slug. Returns null if not found or corrupt.
    /// </summary>
    public WorkspaceDefinition? Load(string slug)
    {
        FileLog.Write($"[WorkspaceStore] Load: slug={slug}");

        var filePath = GetFilePath(slug);
        if (!File.Exists(filePath))
        {
            FileLog.Write($"[WorkspaceStore] Load: file not found for slug={slug}");
            return null;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<WorkspaceDefinition>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorkspaceStore] Load FAILED for {slug}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Delete a workspace by slug. Returns true if the file was deleted.
    /// </summary>
    public bool Delete(string slug)
    {
        FileLog.Write($"[WorkspaceStore] Delete: slug={slug}");

        var filePath = GetFilePath(slug);
        if (!File.Exists(filePath))
        {
            FileLog.Write($"[WorkspaceStore] Delete: file not found for slug={slug}");
            return false;
        }

        try
        {
            File.Delete(filePath);
            FileLog.Write($"[WorkspaceStore] Delete: deleted {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorkspaceStore] Delete FAILED: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Check if a workspace with the given slug exists.
    /// </summary>
    public bool Exists(string slug)
    {
        return File.Exists(GetFilePath(slug));
    }

    /// <summary>
    /// Convert a workspace name to a filesystem-safe slug (lowercase, hyphens).
    /// </summary>
    public static string ToSlug(string name)
    {
        var slug = name.Trim().ToLowerInvariant();
        slug = Regex.Replace(slug, @"[^a-z0-9\s-]", "");
        slug = Regex.Replace(slug, @"[\s]+", "-");
        slug = Regex.Replace(slug, @"-+", "-");
        slug = slug.Trim('-');

        if (string.IsNullOrEmpty(slug))
            slug = "workspace";

        return slug;
    }

    private string GetFilePath(string slug) => Path.Combine(FolderPath, $"{slug}.workspace.json");

    private void EnsureDirectory()
    {
        if (!Directory.Exists(FolderPath))
        {
            Directory.CreateDirectory(FolderPath);
            FileLog.Write($"[WorkspaceStore] Created directory {FolderPath}");
        }
    }
}
