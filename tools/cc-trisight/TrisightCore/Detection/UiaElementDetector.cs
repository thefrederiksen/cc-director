using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Serilog;

namespace Trisight.Core.Detection;

/// <summary>
/// Detects UI elements using Windows UI Automation via FlaUI.
/// This is the primary detection tier — provides pixel-perfect coordinates
/// directly from the application's element tree.
/// </summary>
public class UiaElementDetector : IDisposable
{
    private readonly UIA3Automation _automation;
    private bool _disposed;

    // Win32 imports for window handle resolution
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    public UiaElementDetector()
    {
        _automation = new UIA3Automation();
    }

    /// <summary>
    /// Detect all UI elements in the specified window.
    /// </summary>
    /// <param name="windowTitle">Title (or substring) of the target window.</param>
    /// <param name="maxDepth">Maximum tree depth to walk (default 15).</param>
    /// <returns>List of detected elements with pixel-perfect coordinates.</returns>
    public List<DetectedElement> DetectElements(string windowTitle, int maxDepth = 15)
    {
        var sw = Stopwatch.StartNew();
        var elements = new List<DetectedElement>();

        try
        {
            var window = FindWindow(windowTitle);
            if (window == null)
            {
                Log.Warning("UiaElementDetector: Window not found: {WindowTitle}", windowTitle);
                return elements;
            }

            WalkTree(window, elements, 0, maxDepth);

            sw.Stop();
            Log.Information("UiaElementDetector: Found {Count} elements in {ElapsedMs}ms for '{Window}'",
                elements.Count, sw.ElapsedMilliseconds, windowTitle);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log.Error(ex, "UiaElementDetector: Error detecting elements for '{Window}'", windowTitle);
        }

        return elements;
    }

    /// <summary>
    /// Detect all UI elements using the window handle directly.
    /// </summary>
    public List<DetectedElement> DetectElements(IntPtr windowHandle, int maxDepth = 15)
    {
        var sw = Stopwatch.StartNew();
        var elements = new List<DetectedElement>();

        try
        {
            var window = _automation.FromHandle(windowHandle);
            if (window == null)
            {
                Log.Warning("UiaElementDetector: Could not get automation element for handle {Handle}", windowHandle);
                return elements;
            }

            WalkTree(window, elements, 0, maxDepth);

            sw.Stop();
            Log.Information("UiaElementDetector: Found {Count} elements in {ElapsedMs}ms for handle {Handle}",
                elements.Count, sw.ElapsedMilliseconds, windowHandle);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log.Error(ex, "UiaElementDetector: Error detecting elements for handle {Handle}", windowHandle);
        }

        return elements;
    }

    /// <summary>
    /// Find the foreground window's handle.
    /// </summary>
    public IntPtr GetForegroundWindowHandle() => GetForegroundWindow();

    /// <summary>
    /// Find a window by title (substring match).
    /// Returns the FlaUI AutomationElement for the window.
    /// </summary>
    public AutomationElement? FindWindow(string windowTitle)
    {
        // First try to find the window handle via Win32 enumeration (fast)
        IntPtr targetHandle = IntPtr.Zero;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            int length = GetWindowTextLength(hWnd);
            if (length == 0) return true;

            var sb = new StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();

            if (title.Contains(windowTitle, StringComparison.OrdinalIgnoreCase))
            {
                targetHandle = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        if (targetHandle == IntPtr.Zero)
        {
            Log.Debug("UiaElementDetector: No window found matching '{Title}'", windowTitle);
            return null;
        }

        return _automation.FromHandle(targetHandle);
    }

    /// <summary>
    /// Get the window handle for a window matching the given title.
    /// </summary>
    public IntPtr FindWindowHandle(string windowTitle)
    {
        IntPtr targetHandle = IntPtr.Zero;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            int length = GetWindowTextLength(hWnd);
            if (length == 0) return true;

            var sb = new StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();

            if (title.Contains(windowTitle, StringComparison.OrdinalIgnoreCase))
            {
                targetHandle = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return targetHandle;
    }

    /// <summary>
    /// Recursively walk the UIA tree and collect elements.
    /// </summary>
    private void WalkTree(AutomationElement element, List<DetectedElement> results, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;

        try
        {
            // Get bounding rectangle
            var rect = element.BoundingRectangle;

            // Skip elements with no visible area or offscreen
            if (rect.IsEmpty || rect.Width < 1 || rect.Height < 1)
            {
                // Still walk children — parent may be a container with no bounds
                WalkChildren(element, results, depth, maxDepth);
                return;
            }

            // Skip elements that are entirely offscreen (negative coords or very large)
            if (rect.Right < 0 || rect.Bottom < 0 || rect.Left > 10000 || rect.Top > 10000)
            {
                return;
            }

            var controlType = GetControlTypeName(element);
            var name = element.Name ?? "";
            var automationId = element.AutomationId;
            var className = element.ClassName;
            var isEnabled = true;
            var isOffscreen = false;

            try { isEnabled = element.IsEnabled; } catch { }
            try { isOffscreen = element.IsOffscreen; } catch { }

            // Skip offscreen elements
            if (isOffscreen) return;

            // Only add meaningful elements (skip generic containers with no name)
            if (ShouldInclude(controlType, name, automationId, className))
            {
                var detected = new DetectedElement
                {
                    Type = controlType,
                    Name = name,
                    Bounds = new BoundingRect(
                        (int)rect.X, (int)rect.Y,
                        (int)rect.Width, (int)rect.Height),
                    AutomationId = string.IsNullOrEmpty(automationId) ? null : automationId,
                    ClassName = className,
                    IsEnabled = isEnabled,
                    IsInteractable = IsInteractableType(controlType),
                    State = GetElementState(element, controlType),
                    Sources = DetectionSource.Uia,
                    Confidence = 1.0, // UIA provides pixel-perfect coordinates
                };

                results.Add(detected);
            }

            // Walk children
            WalkChildren(element, results, depth, maxDepth);
        }
        catch (Exception ex)
        {
            Log.Debug("UiaElementDetector: Error reading element at depth {Depth}: {Error}",
                depth, ex.Message);
        }
    }

    private void WalkChildren(AutomationElement element, List<DetectedElement> results, int depth, int maxDepth)
    {
        try
        {
            var children = element.FindAllChildren();
            if (children == null) return;

            foreach (var child in children)
            {
                WalkTree(child, results, depth + 1, maxDepth);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("UiaElementDetector: Error enumerating children at depth {Depth}: {Error}",
                depth, ex.Message);
        }
    }

    /// <summary>
    /// Determine if an element should be included in results.
    /// Filters out generic containers with no useful identification.
    /// </summary>
    private static bool ShouldInclude(string controlType, string name, string? automationId, string? className)
    {
        // Always include elements with a name or automation ID
        if (!string.IsNullOrWhiteSpace(name)) return true;
        if (!string.IsNullOrWhiteSpace(automationId)) return true;

        // Include interactable types even without a name
        return controlType switch
        {
            "Button" or "CheckBox" or "RadioButton" or "ComboBox" or "TextBox" or "Edit"
            or "MenuItem" or "TabItem" or "ListItem" or "TreeItem" or "Slider"
            or "ScrollBar" or "Hyperlink" or "SplitButton" or "ToggleButton"
            or "DataItem" or "SpinButton" => true,
            _ => false,
        };
    }

    /// <summary>
    /// Determine if a control type represents an interactable element.
    /// </summary>
    private static bool IsInteractableType(string controlType)
    {
        return controlType switch
        {
            "Button" or "CheckBox" or "RadioButton" or "ComboBox" or "TextBox" or "Edit"
            or "MenuItem" or "TabItem" or "ListItem" or "TreeItem" or "Slider"
            or "Hyperlink" or "SplitButton" or "ToggleButton" or "MenuBar"
            or "DataItem" or "SpinButton" or "ScrollBar" => true,
            "Text" or "Label" or "StatusBar" or "TitleBar" or "Header"
            or "Separator" or "Group" or "Pane" or "Window" or "ToolBar"
            or "Image" => false,
            _ => false,
        };
    }

    /// <summary>
    /// Get a simplified control type name from the UIA element.
    /// </summary>
    private static string GetControlTypeName(AutomationElement element)
    {
        try
        {
            var ct = element.ControlType;
            return ct switch
            {
                ControlType.Button => "Button",
                ControlType.CheckBox => "CheckBox",
                ControlType.ComboBox => "ComboBox",
                ControlType.Edit => "Edit",
                ControlType.Hyperlink => "Hyperlink",
                ControlType.Image => "Image",
                ControlType.ListItem => "ListItem",
                ControlType.List => "List",
                ControlType.Menu => "Menu",
                ControlType.MenuBar => "MenuBar",
                ControlType.MenuItem => "MenuItem",
                ControlType.Pane => "Pane",
                ControlType.RadioButton => "RadioButton",
                ControlType.ScrollBar => "ScrollBar",
                ControlType.Slider => "Slider",
                ControlType.Spinner => "SpinButton",
                ControlType.SplitButton => "SplitButton",
                ControlType.StatusBar => "StatusBar",
                ControlType.Tab => "Tab",
                ControlType.TabItem => "TabItem",
                ControlType.Text => "Text",
                ControlType.TitleBar => "TitleBar",
                ControlType.ToolBar => "ToolBar",
                ControlType.ToolTip => "ToolTip",
                ControlType.Tree => "Tree",
                ControlType.TreeItem => "TreeItem",
                ControlType.Window => "Window",
                ControlType.DataGrid => "DataGrid",
                ControlType.DataItem => "DataItem",
                ControlType.Document => "Document",
                ControlType.Group => "Group",
                ControlType.Header => "Header",
                ControlType.HeaderItem => "HeaderItem",
                ControlType.ProgressBar => "ProgressBar",
                ControlType.Separator => "Separator",
                ControlType.Table => "Table",
                ControlType.Thumb => "Thumb",
                _ => "Custom",
            };
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <summary>
    /// Extract the current state of an element (checked, expanded, focused, etc.).
    /// </summary>
    private static string? GetElementState(AutomationElement element, string controlType)
    {
        try
        {
            var states = new List<string>();

            // Check toggle state for checkboxes/toggle buttons
            if (controlType is "CheckBox" or "ToggleButton")
            {
                try
                {
                    if (element.Patterns.Toggle.IsSupported)
                    {
                        var toggleState = element.Patterns.Toggle.Pattern.ToggleState.Value;
                        states.Add(toggleState switch
                        {
                            ToggleState.On => "checked",
                            ToggleState.Off => "unchecked",
                            ToggleState.Indeterminate => "indeterminate",
                            _ => "unknown",
                        });
                    }
                }
                catch { }
            }

            // Check selection state for list/tree items
            if (controlType is "ListItem" or "TreeItem" or "TabItem")
            {
                try
                {
                    if (element.Patterns.SelectionItem.IsSupported)
                    {
                        var isSelected = element.Patterns.SelectionItem.Pattern.IsSelected.Value;
                        states.Add(isSelected ? "selected" : "unselected");
                    }
                }
                catch { }
            }

            // Check expand/collapse state
            if (controlType is "TreeItem" or "MenuItem" or "ComboBox")
            {
                try
                {
                    if (element.Patterns.ExpandCollapse.IsSupported)
                    {
                        var expandState = element.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value;
                        states.Add(expandState switch
                        {
                            ExpandCollapseState.Expanded => "expanded",
                            ExpandCollapseState.Collapsed => "collapsed",
                            _ => "",
                        });
                    }
                }
                catch { }
            }

            // Check if element has keyboard focus
            try
            {
                if (element.Properties.HasKeyboardFocus.IsSupported &&
                    element.Properties.HasKeyboardFocus.Value)
                    states.Add("focused");
            }
            catch { }

            // Filter empty and return
            states.RemoveAll(string.IsNullOrEmpty);
            return states.Count > 0 ? string.Join(", ", states) : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _automation.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
