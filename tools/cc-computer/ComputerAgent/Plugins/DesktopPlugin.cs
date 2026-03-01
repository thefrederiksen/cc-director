using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Serilog;

namespace CCComputer.Agent.Plugins;

/// <summary>
/// Plugin for desktop UI automation using cc_click.
/// Allows the agent to interact with Windows applications.
/// </summary>
public class DesktopPlugin
{
    private readonly string _ccClickPath;

    // Win32 API declarations for window management
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const int SW_MAXIMIZE = 3;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint GW_OWNER = 4;
    private const uint SPI_GETWORKAREA = 0x0030;

    public DesktopPlugin(string ccClickPath)
    {
        _ccClickPath = ccClickPath;

        if (!File.Exists(_ccClickPath))
        {
            throw new FileNotFoundException($"cc_click not found at: {_ccClickPath}");
        }
    }

    [KernelFunction("minimize_all_windows")]
    [Description("Minimize all windows to show a clean desktop. Use this before starting a task to get a clean slate.")]
    public async Task<string> MinimizeAllWindowsAsync()
    {
        Log.Debug("MinimizeAllWindowsAsync called");

        try
        {
            // Use Shell.Application COM to minimize all windows
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-Command \"$shell = New-Object -ComObject Shell.Application; $shell.MinimizeAll()\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();
            await process.WaitForExitAsync();

            await Task.Delay(300); // Give windows time to minimize

            Log.Debug("MinimizeAllWindowsAsync completed successfully");
            return "Minimized all windows. Desktop is now clean.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MinimizeAllWindowsAsync failed");
            return $"Error minimizing windows: {ex.Message}";
        }
    }

    [KernelFunction("position_window")]
    [Description("Position a window on the screen. Use 'left' for left half, 'right' for right half, 'maximize' for full screen.")]
    public async Task<string> PositionWindowAsync(
        [Description("Window title or substring to match")] string windowTitle,
        [Description("Position: 'left' (left half of screen), 'right' (right half of screen), or 'maximize' (full screen)")] string position)
    {
        Log.Debug("PositionWindowAsync called: windowTitle={WindowTitle}, position={Position}", windowTitle, position);

        try
        {
            // Find the window handle
            IntPtr targetWindow = IntPtr.Zero;
            string foundTitle = "";

            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                int length = GetWindowTextLength(hWnd);
                if (length == 0) return true;

                var sb = new StringBuilder(length + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                var title = sb.ToString();

                if (title.Contains(windowTitle, StringComparison.OrdinalIgnoreCase))
                {
                    targetWindow = hWnd;
                    foundTitle = title;
                    return false; // Stop enumeration
                }

                return true;
            }, IntPtr.Zero);

            if (targetWindow == IntPtr.Zero)
            {
                Log.Warning("PositionWindowAsync: Window not found: {WindowTitle}", windowTitle);
                return $"Window not found: {windowTitle}";
            }

            // Get primary monitor working area (excludes taskbar)
            var workArea = new RECT();
            SystemParametersInfo(SPI_GETWORKAREA, 0, ref workArea, 0);

            var screenWidth = workArea.Right - workArea.Left;
            var screenHeight = workArea.Bottom - workArea.Top;
            var screenX = workArea.Left;
            var screenY = workArea.Top;

            // Restore window first if minimized
            ShowWindow(targetWindow, SW_RESTORE);
            await Task.Delay(100);

            int x, y, width, height;
            switch (position.ToLowerInvariant())
            {
                case "left":
                    // Left 80% of screen (CC Computer takes right 20%)
                    x = screenX;
                    y = screenY;
                    width = (int)(screenWidth * 0.80);
                    height = screenHeight;
                    break;
                case "right":
                    // Right 20% of screen (where CC Computer sits)
                    x = screenX + (int)(screenWidth * 0.80);
                    y = screenY;
                    width = (int)(screenWidth * 0.20);
                    height = screenHeight;
                    break;
                case "maximize":
                    ShowWindow(targetWindow, SW_MAXIMIZE);
                    return $"Maximized window: {foundTitle}";
                default:
                    return $"Invalid position: {position}. Use 'left', 'right', or 'maximize'.";
            }

            SetWindowPos(targetWindow, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_SHOWWINDOW);
            SetForegroundWindow(targetWindow);

            Log.Debug("PositionWindowAsync completed: positioned '{WindowTitle}' on {Position}", foundTitle, position);
            return $"Positioned window '{foundTitle}' on the {position} side of the screen.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PositionWindowAsync failed: windowTitle={WindowTitle}, position={Position}", windowTitle, position);
            return $"Error positioning window: {ex.Message}";
        }
    }

    [KernelFunction("get_foreground_window")]
    [Description("Get the title of the window that currently has focus. Use this to verify the correct window is active before performing actions.")]
    public string GetForegroundWindowTitle()
    {
        try
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
            {
                return "No window has focus.";
            }

            int length = GetWindowTextLength(hWnd);
            if (length == 0)
            {
                return "Foreground window has no title.";
            }

            var sb = new StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);

            return $"Current foreground window: {sb}";
        }
        catch (Exception ex)
        {
            return $"Error getting foreground window: {ex.Message}";
        }
    }

    [KernelFunction("list_windows")]
    [Description("List all visible top-level windows on the desktop. Returns JSON array with window titles and handles.")]
    public async Task<string> ListWindowsAsync()
    {
        return await RunCcClickAsync("list-windows");
    }

    [KernelFunction("list_elements")]
    [Description("List UI elements in a specific window. Use this to find buttons, text fields, menus, etc.")]
    public async Task<string> ListElementsAsync(
        [Description("Window title or substring to match (e.g., 'Outlook', 'Notepad')")] string windowTitle,
        [Description("Optional: Filter by control type (e.g., 'Button', 'TextBox', 'MenuItem')")] string? controlType = null,
        [Description("Optional: Max depth to search (default 25)")] int depth = 25)
    {
        var args = $"list-elements -w \"{windowTitle}\" -d {depth}";
        if (!string.IsNullOrEmpty(controlType))
        {
            args += $" -t {controlType}";
        }
        return await RunCcClickAsync(args);
    }

    [KernelFunction("click")]
    [Description("Click a UI element in a window. Can target by element name, automation ID, or screen coordinates.")]
    public async Task<string> ClickAsync(
        [Description("Window title or substring to match")] string? windowTitle = null,
        [Description("Element name/text to click (e.g., 'Send', 'File', 'New Email')")] string? elementName = null,
        [Description("Element AutomationId to click")] string? automationId = null,
        [Description("Screen coordinates to click (e.g., '500,300')")] string? coordinates = null)
    {
        Log.Debug("ClickAsync called: windowTitle={WindowTitle}, elementName={ElementName}, automationId={AutomationId}, coordinates={Coordinates}",
            windowTitle, elementName, automationId, coordinates);

        var args = "click";

        if (!string.IsNullOrEmpty(windowTitle))
            args += $" -w \"{windowTitle}\"";

        if (!string.IsNullOrEmpty(elementName))
            args += $" --name \"{elementName}\"";

        if (!string.IsNullOrEmpty(automationId))
            args += $" --id \"{automationId}\"";

        if (!string.IsNullOrEmpty(coordinates))
            args += $" --xy \"{coordinates}\"";

        return await RunCcClickAsync(args);
    }

    [KernelFunction("type_text")]
    [Description("Type text into a specific UI element by name or automation ID. REQUIRES either elementName or automationId - if you just want to type into the focused window without targeting a specific element, use send_keys instead.")]
    public async Task<string> TypeTextAsync(
        [Description("The text to type")] string text,
        [Description("Window title or substring to match")] string? windowTitle = null,
        [Description("REQUIRED: Element name to type into (e.g., 'Text Editor', 'Search box')")] string? elementName = null,
        [Description("REQUIRED: Element AutomationId to type into")] string? automationId = null)
    {
        // Validate that at least one targeting parameter is provided
        if (string.IsNullOrEmpty(elementName) && string.IsNullOrEmpty(automationId))
        {
            return "Error: type_text requires either elementName or automationId to target a specific UI element. " +
                   "To type into the focused window without targeting a specific element, use send_keys instead.";
        }

        var args = $"type --text \"{EscapeForCommandLine(text)}\"";

        if (!string.IsNullOrEmpty(windowTitle))
            args += $" -w \"{windowTitle}\"";

        if (!string.IsNullOrEmpty(elementName))
            args += $" --name \"{elementName}\"";

        if (!string.IsNullOrEmpty(automationId))
            args += $" --id \"{automationId}\"";

        return await RunCcClickAsync(args);
    }

    [KernelFunction("read_text")]
    [Description("Read text content from a UI element.")]
    public async Task<string> ReadTextAsync(
        [Description("Window title or substring to match")] string windowTitle,
        [Description("Element name to read from")] string? elementName = null,
        [Description("Element AutomationId to read from")] string? automationId = null)
    {
        var args = $"read-text -w \"{windowTitle}\"";

        if (!string.IsNullOrEmpty(elementName))
            args += $" --name \"{elementName}\"";

        if (!string.IsNullOrEmpty(automationId))
            args += $" --id \"{automationId}\"";

        return await RunCcClickAsync(args);
    }

    [KernelFunction("send_keys")]
    [Description("Send keystrokes to the currently focused window. Use this when you cannot find a specific element to type into. First click on the target field, then use send_keys to type. NOTE: Special characters like + ^ % ~ ( ) { } are automatically escaped to prevent triggering shortcuts.")]
    public async Task<string> SendKeysAsync(
        [Description("The text to type. Special characters are escaped automatically. For actual key combinations, use press_key or keyboard_shortcut instead.")] string keys,
        [Description("Set to true to send raw keys without escaping (for advanced use with special key sequences)")] bool raw = false)
    {
        try
        {
            // Escape special SendKeys characters unless raw mode is requested
            // SendKeys special chars: + (Shift), ^ (Ctrl), % (Alt), ~ (Enter), { } ( )
            string escapedKeys;
            if (raw)
            {
                escapedKeys = keys.Replace("'", "''");
            }
            else
            {
                escapedKeys = EscapeSendKeysSpecialChars(keys);
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-Command \"Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.SendKeys]::SendWait('{escapedKeys}')\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();
            await process.WaitForExitAsync();

            return $"Sent keys: {keys}";
        }
        catch (Exception ex)
        {
            return $"Error sending keys: {ex.Message}";
        }
    }

    /// <summary>
    /// Escape special characters that have meaning in SendKeys format.
    /// </summary>
    private static string EscapeSendKeysSpecialChars(string text)
    {
        // SendKeys special characters that need to be wrapped in braces:
        // + (Shift), ^ (Ctrl), % (Alt), ~ (Enter), { }, ( )
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            switch (c)
            {
                case '+':
                    sb.Append("{+}");
                    break;
                case '^':
                    sb.Append("{^}");
                    break;
                case '%':
                    sb.Append("{%}");
                    break;
                case '~':
                    sb.Append("{~}");
                    break;
                case '(':
                    sb.Append("{(}");
                    break;
                case ')':
                    sb.Append("{)}");
                    break;
                case '{':
                    sb.Append("{{}");
                    break;
                case '}':
                    sb.Append("{}}");
                    break;
                case '\'':
                    sb.Append("''");  // Escape for PowerShell string
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    [KernelFunction("press_key")]
    [Description("Press a special key or key combination. Common uses: Enter to confirm, Tab to move to next field, Escape to cancel, Ctrl+A to select all, Ctrl+C to copy, Ctrl+V to paste.")]
    public async Task<string> PressKeyAsync(
        [Description("The key to press: Enter, Tab, Escape, Backspace, Delete, Up, Down, Left, Right, Home, End, F1-F12, or combinations like Ctrl+A, Ctrl+C, Ctrl+V, Alt+F4")] string key)
    {
        // Convert friendly key names to SendKeys format
        var sendKeysFormat = key.ToLowerInvariant().Trim() switch
        {
            "enter" => "{ENTER}",
            "tab" => "{TAB}",
            "escape" or "esc" => "{ESC}",
            "backspace" => "{BACKSPACE}",
            "delete" or "del" => "{DELETE}",
            "up" => "{UP}",
            "down" => "{DOWN}",
            "left" => "{LEFT}",
            "right" => "{RIGHT}",
            "home" => "{HOME}",
            "end" => "{END}",
            "f1" => "{F1}",
            "f2" => "{F2}",
            "f3" => "{F3}",
            "f4" => "{F4}",
            "f5" => "{F5}",
            "f6" => "{F6}",
            "f7" => "{F7}",
            "f8" => "{F8}",
            "f9" => "{F9}",
            "f10" => "{F10}",
            "f11" => "{F11}",
            "f12" => "{F12}",
            "ctrl+a" => "^a",
            "ctrl+c" => "^c",
            "ctrl+v" => "^v",
            "ctrl+x" => "^x",
            "ctrl+z" => "^z",
            "ctrl+y" => "^y",
            "ctrl+s" => "^s",
            "ctrl+shift+s" => "^+s",  // Save As
            "ctrl+enter" => "^{ENTER}",  // Send email in Outlook
            "ctrl+n" => "^n",
            "ctrl+o" => "^o",
            "ctrl+p" => "^p",
            "ctrl+f" => "^f",
            "ctrl+h" => "^h",
            "ctrl+w" => "^w",  // Close tab/window
            "alt+f4" => "%{F4}",
            "alt+tab" => "%{TAB}",
            "shift+tab" => "+{TAB}",
            _ => key // Pass through as-is if not recognized
        };

        return await SendKeysAsync(sendKeysFormat, raw: true);
    }

    [KernelFunction("keyboard_shortcut")]
    [Description(@"Execute a keyboard shortcut. More reliable than clicking when UI automation fails on modern apps.
RECOMMENDED SHORTCUTS:
- Save As: Ctrl+Shift+S (works in Notepad, Word, most apps)
- New: Ctrl+N (new document, new email in Outlook)
- Send Email: Ctrl+Enter (sends email in Outlook - USE THIS instead of clicking Send button!)
- Save: Ctrl+S
- Open: Ctrl+O
- Close: Ctrl+W or Alt+F4
- Copy/Paste: Ctrl+C, Ctrl+V
Use this BEFORE trying to click menu items - it's faster and more reliable.")]
    public async Task<string> KeyboardShortcutAsync(
        [Description("Shortcut like 'Ctrl+Shift+S' for Save As, 'Ctrl+N' for New, 'Ctrl+S' for Save")] string shortcut,
        [Description("Optional: Window title to send shortcut to. Will focus the window first.")] string? windowTitle = null)
    {
        Log.Debug("KeyboardShortcutAsync: shortcut={Shortcut}, windowTitle={WindowTitle}", shortcut, windowTitle);

        try
        {
            // Focus window first if specified
            if (!string.IsNullOrEmpty(windowTitle))
            {
                var focusResult = await FocusWindowAsync(windowTitle);
                Log.Debug("KeyboardShortcutAsync: focus result: {Result}", focusResult);
                await Task.Delay(200); // Give window time to come to foreground
            }

            // Use press_key which handles the conversion
            var result = await PressKeyAsync(shortcut);

            return $"Sent keyboard shortcut: {shortcut}" + (windowTitle != null ? $" to '{windowTitle}'" : "");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "KeyboardShortcutAsync failed: shortcut={Shortcut}", shortcut);
            return $"Error sending shortcut {shortcut}: {ex.Message}";
        }
    }

    [KernelFunction("focus_window")]
    [Description("Bring a window to the foreground and give it focus.")]
    public async Task<string> FocusWindowAsync(
        [Description("Window title or substring to match")] string windowTitle)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-Command \"$wshell = New-Object -ComObject wscript.shell; $wshell.AppActivate('{windowTitle.Replace("'", "''")}')\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            // Give the window time to come to foreground
            await Task.Delay(200);

            return $"Focused window: {windowTitle}";
        }
        catch (Exception ex)
        {
            return $"Error focusing window: {ex.Message}";
        }
    }

    private async Task<string> RunCcClickAsync(string arguments)
    {
        Log.Debug("RunCcClickAsync: executing cc_click with args: {Arguments}", arguments);

        var psi = new ProcessStartInfo
        {
            FileName = _ccClickPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();
        sw.Stop();

        if (process.ExitCode != 0)
        {
            Log.Warning("RunCcClickAsync: cc_click failed with exit code {ExitCode} in {ElapsedMs}ms: {Error}",
                process.ExitCode, sw.ElapsedMilliseconds, error);
            return $"Error (exit code {process.ExitCode}): {error}".Trim();
        }

        Log.Debug("RunCcClickAsync: completed in {ElapsedMs}ms, output length: {OutputLength}",
            sw.ElapsedMilliseconds, output.Length);
        return output.Trim();
    }

    private static string EscapeForCommandLine(string text)
    {
        // Escape double quotes for command line
        return text.Replace("\"", "\\\"");
    }
}
