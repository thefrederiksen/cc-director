using System.Text.Json;
using System.Text.Json.Serialization;
using Trisight.Core.Detection;
using Serilog;

// Configure logging to stderr so JSON goes to stdout clean
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Warning()
    .WriteTo.Console(standardErrorFromLevel: Serilog.Events.LogEventLevel.Verbose,
        outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}")
    .CreateLogger();

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0].ToLowerInvariant();
try
{
    return command switch
    {
        "detect" => await RunDetect(args[1..]),
        "uia" => RunUia(args[1..]),
        "ocr" => await RunOcr(args[1..]),
        "annotate" => await RunAnnotate(args[1..]),
        "help" or "--help" or "-h" => PrintUsage(),
        _ => Error($"Unknown command: {command}"),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

// --- Commands ---

async Task<int> RunDetect(string[] cmdArgs)
{
    string? windowTitle = null;
    string? screenshotPath = null;
    var tiers = new HashSet<string> { "uia", "ocr", "pixel" };
    bool annotate = false;
    string? annotatedOutput = null;
    int depth = 15;

    for (int i = 0; i < cmdArgs.Length; i++)
    {
        switch (cmdArgs[i])
        {
            case "--window" or "-w":
                windowTitle = cmdArgs[++i];
                break;
            case "--screenshot" or "-s":
                screenshotPath = cmdArgs[++i];
                break;
            case "--tiers":
                tiers = cmdArgs[++i].Split(',').Select(t => t.Trim().ToLower()).ToHashSet();
                break;
            case "--annotate":
                annotate = true;
                break;
            case "--output" or "-o":
                annotatedOutput = cmdArgs[++i];
                annotate = true;
                break;
            case "--depth" or "-d":
                depth = int.Parse(cmdArgs[++i]);
                break;
        }
    }

    if (windowTitle == null)
        return Error("--window is required for detect");

    using var pipeline = new DetectionPipeline();
    pipeline.EnableOcr = tiers.Contains("ocr");
    pipeline.EnablePixelAnalysis = tiers.Contains("pixel");
    pipeline.MaxUiaDepth = depth;

    DetectionResult result;
    if (annotate && screenshotPath != null)
        result = await pipeline.DetectAndAnnotateAsync(windowTitle, screenshotPath, annotatedOutput);
    else
        result = await pipeline.DetectAsync(windowTitle, screenshotPath ?? "");

    var output = new
    {
        success = true,
        windowTitle = result.WindowTitle,
        elementCount = result.Elements.Count,
        uiaCount = result.UiaElementCount,
        ocrCount = result.OcrRegionCount,
        pixelCount = result.PixelAnalysisCount,
        detectionTimeMs = result.DetectionTimeMs,
        annotatedScreenshot = result.AnnotatedScreenshotPath,
        elements = result.Elements.Select(e => new
        {
            id = e.Id,
            type = e.Type,
            name = e.Name,
            bounds = new { e.Bounds.X, e.Bounds.Y, e.Bounds.Width, e.Bounds.Height },
            center = e.Center,
            automationId = e.AutomationId,
            isEnabled = e.IsEnabled,
            isInteractable = e.IsInteractable,
            state = e.State,
            sources = e.Sources.ToString(),
            confidence = e.Confidence,
        }),
    };

    Console.WriteLine(JsonSerializer.Serialize(output, jsonOptions));
    return 0;
}

int RunUia(string[] cmdArgs)
{
    string? windowTitle = null;
    int depth = 15;

    for (int i = 0; i < cmdArgs.Length; i++)
    {
        switch (cmdArgs[i])
        {
            case "--window" or "-w":
                windowTitle = cmdArgs[++i];
                break;
            case "--depth" or "-d":
                depth = int.Parse(cmdArgs[++i]);
                break;
        }
    }

    if (windowTitle == null)
        return Error("--window is required for uia");

    using var detector = new UiaElementDetector();
    var elements = detector.DetectElements(windowTitle, depth);

    var output = new
    {
        success = true,
        windowTitle,
        count = elements.Count,
        elements = elements.Select(e => new
        {
            id = e.Id,
            type = e.Type,
            name = e.Name,
            bounds = new { e.Bounds.X, e.Bounds.Y, e.Bounds.Width, e.Bounds.Height },
            center = e.Center,
            automationId = e.AutomationId,
            isEnabled = e.IsEnabled,
            isInteractable = e.IsInteractable,
            state = e.State,
            className = e.ClassName,
        }),
    };

    Console.WriteLine(JsonSerializer.Serialize(output, jsonOptions));
    return 0;
}

async Task<int> RunOcr(string[] cmdArgs)
{
    string? screenshotPath = null;

    for (int i = 0; i < cmdArgs.Length; i++)
    {
        switch (cmdArgs[i])
        {
            case "--screenshot" or "-s":
                screenshotPath = cmdArgs[++i];
                break;
        }
    }

    if (screenshotPath == null || !File.Exists(screenshotPath))
        return Error("--screenshot pointing to an existing file is required for ocr");

    var detector = new OcrTextDetector();
    var regions = await detector.DetectTextAsync(screenshotPath);

    var output = new
    {
        success = true,
        screenshotPath,
        count = regions.Count,
        regions = regions.Select(r => new
        {
            text = r.Text,
            bounds = new { r.Bounds.X, r.Bounds.Y, r.Bounds.Width, r.Bounds.Height },
            confidence = r.Confidence,
        }),
    };

    Console.WriteLine(JsonSerializer.Serialize(output, jsonOptions));
    return 0;
}

async Task<int> RunAnnotate(string[] cmdArgs)
{
    string? screenshotPath = null;
    string? elementsPath = null;
    string? outputPath = null;
    string? windowTitle = null;

    for (int i = 0; i < cmdArgs.Length; i++)
    {
        switch (cmdArgs[i])
        {
            case "--screenshot" or "-s":
                screenshotPath = cmdArgs[++i];
                break;
            case "--elements" or "-e":
                elementsPath = cmdArgs[++i];
                break;
            case "--output" or "-o":
                outputPath = cmdArgs[++i];
                break;
            case "--window" or "-w":
                windowTitle = cmdArgs[++i];
                break;
        }
    }

    if (screenshotPath == null || !File.Exists(screenshotPath))
        return Error("--screenshot pointing to an existing file is required for annotate");

    List<DetectedElement> elements;

    if (elementsPath != null && File.Exists(elementsPath))
    {
        // Load pre-computed elements from JSON
        var json = await File.ReadAllTextAsync(elementsPath);
        elements = JsonSerializer.Deserialize<List<DetectedElement>>(json, jsonOptions) ?? [];
    }
    else if (windowTitle != null)
    {
        // Run full detection to get elements
        using var pipeline = new DetectionPipeline();
        var result = await pipeline.DetectAsync(windowTitle, screenshotPath);
        elements = result.Elements;
    }
    else
    {
        return Error("Either --elements (JSON file) or --window (to auto-detect) is required");
    }

    var (path, _) = AnnotatedScreenshotRenderer.Render(screenshotPath, elements, outputPath);

    var output = new
    {
        success = true,
        annotatedPath = path,
        elementCount = elements.Count,
    };

    Console.WriteLine(JsonSerializer.Serialize(output, jsonOptions));
    return 0;
}

// --- Helpers ---

int Error(string message)
{
    var output = new { success = false, error = message };
    Console.WriteLine(JsonSerializer.Serialize(output, jsonOptions));
    return 1;
}

int PrintUsage()
{
    Console.Error.WriteLine(@"TrisightCli — TriSight detection pipeline CLI

Usage:
  trisight_cli detect --window ""Notepad"" --screenshot path.png [--tiers uia,ocr,pixel] [--annotate] [--output out.png] [--depth 15]
  trisight_cli uia --window ""Notepad"" [--depth 15]
  trisight_cli ocr --screenshot path.png
  trisight_cli annotate --screenshot path.png --window ""Notepad"" --output annotated.png
  trisight_cli annotate --screenshot path.png --elements elements.json --output annotated.png

Commands:
  detect     Full 3-tier detection (UIA + OCR + Pixel Analysis + Fusion)
  uia        Tier 1 only: UI Automation element tree
  ocr        Tier 2 only: Windows OCR text detection
  annotate   Render numbered bounding boxes on screenshot");
    return 0;
}
