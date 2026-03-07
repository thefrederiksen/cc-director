using System.IO;
using System.Text.Json;
using CcDirector.ContentWriter.Models;
using CcDirector.Core.Utilities;

namespace CcDirector.ContentWriter.Services;

public class ContentStorageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string StorageDirectory { get; }

    public ContentStorageService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        StorageDirectory = Path.Combine(appData, "cc-director", "writer");
        Directory.CreateDirectory(StorageDirectory);
        FileLog.Write($"[ContentStorageService] StorageDirectory: {StorageDirectory}");
    }

    public List<ContentDocumentInfo> ListDocuments()
    {
        FileLog.Write("[ContentStorageService] ListDocuments");
        var results = new List<ContentDocumentInfo>();

        var files = Directory.GetFiles(StorageDirectory, "*.json");
        foreach (var file in files)
        {
            var doc = LoadDocument(file);
            if (doc != null)
            {
                results.Add(new ContentDocumentInfo
                {
                    FilePath = file,
                    Name = doc.Name,
                    Status = doc.Status,
                    Modified = doc.Modified
                });
            }
        }

        FileLog.Write($"[ContentStorageService] ListDocuments: found {results.Count} documents");
        return results.OrderByDescending(d => d.Modified).ToList();
    }

    public ContentDocument? LoadDocument(string filePath)
    {
        FileLog.Write($"[ContentStorageService] LoadDocument: {filePath}");
        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<ContentDocument>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ContentStorageService] LoadDocument FAILED: {ex.Message}");
            return null;
        }
    }

    public string SaveDocument(ContentDocument doc, string? existingPath = null)
    {
        FileLog.Write($"[ContentStorageService] SaveDocument: name={doc.Name}, path={existingPath}");
        doc.Modified = DateTime.UtcNow;

        var filePath = existingPath ?? GenerateFilePath(doc.Name);
        var json = JsonSerializer.Serialize(doc, JsonOptions);
        File.WriteAllText(filePath, json);

        FileLog.Write($"[ContentStorageService] SaveDocument: written to {filePath}");
        return filePath;
    }

    public ContentDocument CreateNewDocument(string name)
    {
        FileLog.Write($"[ContentStorageService] CreateNewDocument: {name}");
        var doc = new ContentDocument
        {
            Name = name,
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow,
            Sections = new List<ContentSection>
            {
                new() { Id = 1, Heading = "Section 1", Body = "" }
            }
        };

        SaveDocument(doc);
        return doc;
    }

    public void UpdateSelection(string filePath, List<int> selectedIds)
    {
        FileLog.Write($"[ContentStorageService] UpdateSelection: path={filePath}, ids=[{string.Join(",", selectedIds)}]");
        var doc = LoadDocument(filePath);
        if (doc == null) return;

        doc.Selected = selectedIds;
        SaveDocument(doc, filePath);
    }

    private string GenerateFilePath(string name)
    {
        var safeName = string.Join("-", name.Split(Path.GetInvalidFileNameChars()))
            .Replace(" ", "-")
            .ToLowerInvariant();
        var path = Path.Combine(StorageDirectory, $"{safeName}.json");

        // Avoid collisions
        var counter = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(StorageDirectory, $"{safeName}-{counter}.json");
            counter++;
        }

        return path;
    }
}

public class ContentDocumentInfo
{
    public string FilePath { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime Modified { get; set; }
}
