using System.Runtime.InteropServices;

namespace CcDirector.Core.UnixPty;

/// <summary>
/// P/Invoke bindings for Unix/macOS PTY operations.
/// These are libc functions for pseudo-terminal management.
/// </summary>
internal static class UnixNativeMethods
{
    private const string LibC = "libc";
    private const string LibUtil = "libutil"; // macOS uses libutil for openpty

    // ioctl request codes differ by platform
    // Linux: 0x5414, macOS: 0x80087467
    public static ulong TIOCSWINSZ => RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
        ? 0x80087467UL
        : 0x5414UL;

    // Standard file descriptors
    public const int STDIN_FILENO = 0;
    public const int STDOUT_FILENO = 1;
    public const int STDERR_FILENO = 2;

    // Signal constants
    public const int SIGTERM = 15;
    public const int SIGKILL = 9;

    /// <summary>
    /// Create a pseudo-terminal pair.
    /// On success, master and slave contain file descriptors for the PTY.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int openpty(
        out int master,
        out int slave,
        IntPtr name,      // char* name - can be null
        IntPtr termios,   // struct termios* - can be null
        IntPtr winsize);  // struct winsize* - can be null

    /// <summary>
    /// Perform I/O control operations on a file descriptor.
    /// Used for resizing the terminal (TIOCSWINSZ).
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int ioctl(int fd, ulong request, ref Winsize ws);

    /// <summary>
    /// Close a file descriptor.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int close(int fd);

    /// <summary>
    /// Read from a file descriptor.
    /// Returns number of bytes read, 0 on EOF, -1 on error.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern IntPtr read(int fd, byte[] buf, IntPtr count);

    /// <summary>
    /// Write to a file descriptor.
    /// Returns number of bytes written, -1 on error.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern IntPtr write(int fd, byte[] buf, IntPtr count);

    /// <summary>
    /// Create a child process.
    /// Returns 0 in child, child PID in parent, -1 on error.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int fork();

    /// <summary>
    /// Create a new session and set the process as session leader.
    /// Required before setting a controlling terminal.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int setsid();

    /// <summary>
    /// Duplicate a file descriptor.
    /// Makes newfd refer to the same file as oldfd.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int dup2(int oldfd, int newfd);

    /// <summary>
    /// Execute a program, searching PATH.
    /// argv must be null-terminated array.
    /// Does not return on success.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int execvp(
        [MarshalAs(UnmanagedType.LPStr)] string file,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[] argv);

    /// <summary>
    /// Wait for child process to change state.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int waitpid(int pid, out int status, int options);

    /// <summary>
    /// Send a signal to a process.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int kill(int pid, int sig);

    /// <summary>
    /// Set environment variable.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int setenv(
        [MarshalAs(UnmanagedType.LPStr)] string name,
        [MarshalAs(UnmanagedType.LPStr)] string value,
        int overwrite);

    /// <summary>
    /// Change current working directory.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int chdir([MarshalAs(UnmanagedType.LPStr)] string path);

    // waitpid options
    public const int WNOHANG = 1;

    /// <summary>
    /// Window size structure for terminal resize.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Winsize
    {
        public ushort ws_row;     // rows (characters)
        public ushort ws_col;     // columns (characters)
        public ushort ws_xpixel;  // horizontal size in pixels (unused)
        public ushort ws_ypixel;  // vertical size in pixels (unused)
    }

    /// <summary>
    /// Extract exit status from waitpid status.
    /// </summary>
    public static int WEXITSTATUS(int status) => (status >> 8) & 0xFF;

    /// <summary>
    /// Check if process exited normally.
    /// </summary>
    public static bool WIFEXITED(int status) => (status & 0x7F) == 0;
}
