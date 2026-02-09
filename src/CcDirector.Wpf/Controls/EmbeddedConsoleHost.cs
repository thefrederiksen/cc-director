using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace CcDirector.Wpf.Controls;

/// <summary>
/// Manages a console process as a borderless overlay window positioned over
/// the WPF terminal area. Does NOT reparent (SetParent) — the console stays
/// top-level so keyboard input works naturally.
/// </summary>
public class EmbeddedConsoleHost : IDisposable
{
    private IntPtr _consoleHwnd;
    private Process? _process;
    private bool _disposed;
    private bool _visible;

    public int ProcessId => _process?.Id ?? 0;
    public bool HasExited => _process == null || _process.HasExited;
    public IntPtr ConsoleHwnd => _consoleHwnd;

    public event Action<int>? OnProcessExited;

    /// <summary>
    /// Spawn the console process, find its window handle, and strip borders.
    /// </summary>
    public void StartProcess(string exe, string args, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = false,
        };

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += (_, _) =>
        {
            int code = 0;
            try { code = _process.ExitCode; } catch { }
            Application.Current?.Dispatcher.BeginInvoke(() => OnProcessExited?.Invoke(code));
        };
        _process.Start();

        Debug.WriteLine($"[EmbeddedConsoleHost] Process started, PID={_process.Id}");

        _consoleHwnd = WaitForConsoleWindow(_process, TimeSpan.FromSeconds(5));

        if (_consoleHwnd == IntPtr.Zero)
        {
            Debug.WriteLine("[EmbeddedConsoleHost] Could not find console window handle");
            return;
        }

        Debug.WriteLine($"[EmbeddedConsoleHost] Found console hwnd=0x{_consoleHwnd:X}");
        StripBorders(_consoleHwnd);
        _visible = true;
    }

    /// <summary>
    /// Remove title bar, borders, and taskbar presence from the console window.
    /// </summary>
    private static void StripBorders(IntPtr hwnd)
    {
        // Remove title bar and resize borders
        int style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME);
        SetWindowLong(hwnd, GWL_STYLE, style);

        // Set WS_EX_TOOLWINDOW to hide from taskbar and alt-tab
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle &= ~(WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME | WS_EX_APPWINDOW);
        exStyle |= WS_EX_TOOLWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

        // Force the window to redraw with new styles
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Position the console window to cover the given screen-space rectangle.
    /// </summary>
    public void UpdatePosition(Rect screenRect)
    {
        if (_consoleHwnd == IntPtr.Zero) return;

        MoveWindow(_consoleHwnd,
            (int)screenRect.X, (int)screenRect.Y,
            (int)screenRect.Width, (int)screenRect.Height,
            true);
    }

    /// <summary>
    /// Send text to the console input buffer using WriteConsoleInput.
    /// Each character is sent as a key-down + key-up pair, followed by Enter.
    /// </summary>
    public void SendText(string text)
    {
        if (_process == null || _process.HasExited)
        {
            Debug.WriteLine("[EmbeddedConsoleHost] SendText: process not running");
            return;
        }

        // Attach to the target process's console to get its input handle
        FreeConsole();
        if (!AttachConsole((uint)_process.Id))
        {
            Debug.WriteLine($"[EmbeddedConsoleHost] SendText: AttachConsole failed, error={Marshal.GetLastWin32Error()}");
            return;
        }

        try
        {
            IntPtr hInput = GetStdHandle(STD_INPUT_HANDLE);
            if (hInput == IntPtr.Zero || hInput == INVALID_HANDLE_VALUE)
            {
                Debug.WriteLine("[EmbeddedConsoleHost] SendText: GetStdHandle failed");
                return;
            }

            // Build key event records: key down + key up for each char, then Enter
            var records = new List<INPUT_RECORD>();

            foreach (char c in text)
            {
                short vk = VkKeyScan(c);
                byte virtualKey = (byte)(vk & 0xFF);

                records.Add(MakeKeyEvent(c, virtualKey, true));
                records.Add(MakeKeyEvent(c, virtualKey, false));
            }

            // Enter key
            records.Add(MakeKeyEvent('\r', VK_RETURN, true));
            records.Add(MakeKeyEvent('\r', VK_RETURN, false));

            var arr = records.ToArray();
            WriteConsoleInput(hInput, arr, (uint)arr.Length, out uint written);

            Debug.WriteLine($"[EmbeddedConsoleHost] SendText: wrote {written}/{arr.Length} input records");
        }
        finally
        {
            FreeConsole();
        }
    }

    /// <summary>Show the console window.</summary>
    public void Show()
    {
        if (_consoleHwnd != IntPtr.Zero)
        {
            ShowWindow(_consoleHwnd, SW_SHOWNOACTIVATE);
            _visible = true;
        }
    }

    /// <summary>Hide the console window.</summary>
    public void Hide()
    {
        if (_consoleHwnd != IntPtr.Zero)
        {
            ShowWindow(_consoleHwnd, SW_HIDE);
            _visible = false;
        }
    }

    public bool IsVisible => _visible;

    public void KillProcess()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EmbeddedConsoleHost] Kill error: {ex.Message}");
        }

        _process?.Dispose();
        _process = null;
        _consoleHwnd = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        KillProcess();
    }

    private static IntPtr WaitForConsoleWindow(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                return IntPtr.Zero;

            FreeConsole();

            if (AttachConsole((uint)process.Id))
            {
                IntPtr hwnd = GetConsoleWindow();
                FreeConsole();
                if (hwnd != IntPtr.Zero)
                {
                    Debug.WriteLine($"[EmbeddedConsoleHost] AttachConsole succeeded, hwnd=0x{hwnd:X}");
                    return hwnd;
                }
            }

            Thread.Sleep(100);
        }

        Debug.WriteLine("[EmbeddedConsoleHost] Timed out waiting for console window");
        return IntPtr.Zero;
    }

    private static INPUT_RECORD MakeKeyEvent(char c, byte virtualKey, bool keyDown)
    {
        var rec = new INPUT_RECORD
        {
            EventType = KEY_EVENT,
            KeyEvent = new KEY_EVENT_RECORD
            {
                bKeyDown = keyDown,
                wRepeatCount = 1,
                wVirtualKeyCode = virtualKey,
                wVirtualScanCode = (ushort)MapVirtualKey(virtualKey, MAPVK_VK_TO_VSC),
                UnicodeChar = c,
                dwControlKeyState = 0,
            }
        };
        return rec;
    }

    // --- Win32 constants ---

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_BORDER = 0x00800000;
    private const int WS_DLGFRAME = 0x00400000;
    private const int WS_EX_WINDOWEDGE = 0x00000100;
    private const int WS_EX_CLIENTEDGE = 0x00000200;
    private const int WS_EX_DLGMODALFRAME = 0x00000001;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SWP_NOMOVE = 0x0002;
    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_FRAMECHANGED = 0x0020;
    private const int STD_INPUT_HANDLE = -10;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const ushort KEY_EVENT = 0x0001;
    private const byte VK_RETURN = 0x0D;
    private const uint MAPVK_VK_TO_VSC = 0;

    // --- kernel32 P/Invoke ---

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WriteConsoleInput(
        IntPtr hConsoleInput,
        INPUT_RECORD[] lpBuffer,
        uint nLength,
        out uint lpNumberOfEventsWritten);

    // --- user32 P/Invoke ---

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int cx, int cy, bool bRepaint);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern short VkKeyScan(char ch);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    // --- Structs ---

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
    }

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    private struct KEY_EVENT_RECORD
    {
        [FieldOffset(0)] public bool bKeyDown;
        [FieldOffset(4)] public ushort wRepeatCount;
        [FieldOffset(6)] public ushort wVirtualKeyCode;
        [FieldOffset(8)] public ushort wVirtualScanCode;
        [FieldOffset(10)] public char UnicodeChar;
        [FieldOffset(12)] public uint dwControlKeyState;
    }
}
