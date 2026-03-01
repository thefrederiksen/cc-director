using System.ComponentModel;
using System.Text.Json;
using Microsoft.SemanticKernel;

namespace CCComputer.Agent.Plugins;

/// <summary>
/// Plugin for discovering and validating file system paths.
/// The agent should use these tools instead of guessing paths.
/// </summary>
public class FileSystemPlugin
{
    [KernelFunction("get_user_directories")]
    [Description("Get the current user's standard directories (Desktop, Documents, Downloads, Pictures, etc.). Use this to discover where to save files instead of guessing paths.")]
    public string GetUserDirectories()
    {
        var directories = new Dictionary<string, string?>
        {
            ["UserProfile"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ["Desktop"] = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            ["Documents"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ["Downloads"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            ["Pictures"] = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            ["Music"] = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            ["Videos"] = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            ["AppData"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ["LocalAppData"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ["Temp"] = Path.GetTempPath()
        };

        return JsonSerializer.Serialize(directories, new JsonSerializerOptions { WriteIndented = true });
    }

    [KernelFunction("get_environment_info")]
    [Description("Get environment information: current username, computer name, OS version, current directory, etc.")]
    public string GetEnvironmentInfo()
    {
        var info = new Dictionary<string, string?>
        {
            ["UserName"] = Environment.UserName,
            ["MachineName"] = Environment.MachineName,
            ["OSVersion"] = Environment.OSVersion.ToString(),
            ["CurrentDirectory"] = Environment.CurrentDirectory,
            ["SystemDirectory"] = Environment.SystemDirectory,
            ["ProcessorCount"] = Environment.ProcessorCount.ToString(),
            ["Is64BitOS"] = Environment.Is64BitOperatingSystem.ToString()
        };

        return JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
    }

    [KernelFunction("path_exists")]
    [Description("Check if a file or directory path exists. Always use this to verify paths before using them.")]
    public string PathExists(
        [Description("The path to check")] string path)
    {
        var result = new Dictionary<string, object>
        {
            ["path"] = path,
            ["exists"] = false,
            ["isFile"] = false,
            ["isDirectory"] = false
        };

        if (File.Exists(path))
        {
            result["exists"] = true;
            result["isFile"] = true;
            var fileInfo = new FileInfo(path);
            result["size"] = fileInfo.Length;
            result["lastModified"] = fileInfo.LastWriteTime.ToString("o");
        }
        else if (Directory.Exists(path))
        {
            result["exists"] = true;
            result["isDirectory"] = true;
        }

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    [KernelFunction("list_directory")]
    [Description("List files and folders in a directory. Returns names, sizes, and types.")]
    public string ListDirectory(
        [Description("The directory path to list")] string path,
        [Description("Optional file pattern filter (e.g., '*.txt', '*.jpg')")] string? pattern = null)
    {
        if (!Directory.Exists(path))
        {
            return JsonSerializer.Serialize(new { error = $"Directory does not exist: {path}" });
        }

        var items = new List<object>();

        // Get directories
        foreach (var dir in Directory.GetDirectories(path))
        {
            var dirInfo = new DirectoryInfo(dir);
            items.Add(new
            {
                name = dirInfo.Name,
                type = "directory",
                lastModified = dirInfo.LastWriteTime.ToString("o")
            });
        }

        // Get files
        var searchPattern = pattern ?? "*";
        foreach (var file in Directory.GetFiles(path, searchPattern))
        {
            var fileInfo = new FileInfo(file);
            items.Add(new
            {
                name = fileInfo.Name,
                type = "file",
                size = fileInfo.Length,
                lastModified = fileInfo.LastWriteTime.ToString("o")
            });
        }

        return JsonSerializer.Serialize(new
        {
            path = path,
            itemCount = items.Count,
            items = items
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [KernelFunction("read_text_file")]
    [Description("Read the contents of a text file. Use this to verify file contents after saving.")]
    public string ReadTextFile(
        [Description("The path to the text file")] string path,
        [Description("Maximum characters to read (default 10000)")] int maxChars = 10000)
    {
        if (!File.Exists(path))
        {
            return JsonSerializer.Serialize(new { error = $"File does not exist: {path}" });
        }

        try
        {
            var content = File.ReadAllText(path);
            if (content.Length > maxChars)
            {
                content = content.Substring(0, maxChars) + "\n... (truncated)";
            }

            return JsonSerializer.Serialize(new
            {
                path = path,
                length = new FileInfo(path).Length,
                content = content
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Error reading file: {ex.Message}" });
        }
    }

    [KernelFunction("write_text_file")]
    [Description("Write text content to a file. The directory must exist. If the file already exists and allowOverwrite is false (the default), returns an error — call get_unique_filename first to get a safe path.")]
    public string WriteTextFile(
        [Description("The path where to save the file")] string path,
        [Description("The text content to write")] string content,
        [Description("Set to true to explicitly allow overwriting an existing file. Defaults to false.")] bool allowOverwrite = false)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"Directory does not exist: {directory}",
                    suggestion = "Use get_user_directories to find valid paths, or create the directory first"
                });
            }

            if (!allowOverwrite && File.Exists(path))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"File already exists: {path}",
                    suggestion = "Call get_unique_filename to generate a safe filename, or set allowOverwrite=true to replace the existing file"
                });
            }

            File.WriteAllText(path, content);

            // Verify the write succeeded
            if (File.Exists(path))
            {
                var fileInfo = new FileInfo(path);
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    path = path,
                    size = fileInfo.Length,
                    message = "File saved successfully"
                });
            }
            else
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "File write appeared to succeed but file not found"
                });
            }
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Error writing file: {ex.Message}"
            });
        }
    }

    [KernelFunction("get_unique_filename")]
    [Description("Generate a unique filename by adding a number suffix if the file already exists. Example: joke.txt -> joke_2.txt. Use this before saving to avoid overwrite dialogs.")]
    public string GetUniqueFilename(
        [Description("The desired file path")] string path)
    {
        if (!File.Exists(path))
        {
            // File doesn't exist, the path is already unique
            return JsonSerializer.Serialize(new
            {
                originalPath = path,
                uniquePath = path,
                alreadyUnique = true,
                message = "File does not exist, path is already unique"
            });
        }

        var directory = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        int counter = 2;
        string newPath;
        do
        {
            newPath = Path.Combine(directory, $"{name}_{counter}{extension}");
            counter++;
        } while (File.Exists(newPath) && counter < 1000);

        if (counter >= 1000)
        {
            return JsonSerializer.Serialize(new
            {
                originalPath = path,
                error = "Could not find unique filename after 1000 attempts"
            });
        }

        return JsonSerializer.Serialize(new
        {
            originalPath = path,
            uniquePath = newPath,
            alreadyUnique = false,
            message = $"Generated unique filename: {Path.GetFileName(newPath)}"
        });
    }
}
