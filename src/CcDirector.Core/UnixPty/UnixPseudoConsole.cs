using System.Runtime.InteropServices;
using static CcDirector.Core.UnixPty.UnixNativeMethods;

namespace CcDirector.Core.UnixPty;

/// <summary>
/// Managed wrapper around a Unix pseudo-terminal (PTY).
/// Creates master/slave file descriptor pair for terminal emulation.
/// </summary>
public sealed class UnixPseudoConsole : IDisposable
{
    private int _masterFd;
    private int _slaveFd;
    private bool _disposed;

    /// <summary>
    /// Master file descriptor - read/write from parent process.
    /// Writing here sends to the child's stdin.
    /// Reading here receives from the child's stdout/stderr.
    /// </summary>
    public int MasterFd => _masterFd;

    /// <summary>
    /// Slave file descriptor - attached to child process.
    /// The child uses this as its stdin/stdout/stderr.
    /// </summary>
    public int SlaveFd => _slaveFd;

    private UnixPseudoConsole(int masterFd, int slaveFd)
    {
        _masterFd = masterFd;
        _slaveFd = slaveFd;
    }

    /// <summary>
    /// Create a new pseudo-terminal with the given dimensions.
    /// </summary>
    /// <param name="cols">Terminal width in columns.</param>
    /// <param name="rows">Terminal height in rows.</param>
    /// <returns>A new UnixPseudoConsole instance.</returns>
    public static UnixPseudoConsole Create(short cols = 120, short rows = 30)
    {
        int master, slave;

        int result = openpty(out master, out slave, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (result == -1)
        {
            int errno = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"openpty failed with errno {errno}");
        }

        var console = new UnixPseudoConsole(master, slave);

        // Set initial terminal size
        console.Resize(cols, rows);

        return console;
    }

    /// <summary>
    /// Resize the pseudo-terminal.
    /// </summary>
    /// <param name="cols">New width in columns.</param>
    /// <param name="rows">New height in rows.</param>
    public void Resize(short cols, short rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var ws = new Winsize
        {
            ws_col = (ushort)cols,
            ws_row = (ushort)rows,
            ws_xpixel = 0,
            ws_ypixel = 0
        };

        int result = ioctl(_masterFd, TIOCSWINSZ, ref ws);
        if (result == -1)
        {
            int errno = Marshal.GetLastWin32Error();
            // Don't throw - resize failures are not fatal
            System.Diagnostics.Debug.WriteLine($"[UnixPseudoConsole] ioctl TIOCSWINSZ failed: errno={errno}");
        }
    }

    /// <summary>
    /// Write data to the master (sends to child's stdin).
    /// </summary>
    public int Write(byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        IntPtr written = write(_masterFd, data, (IntPtr)data.Length);
        if ((long)written == -1)
        {
            int errno = Marshal.GetLastWin32Error();
            throw new IOException($"write to PTY master failed: errno={errno}");
        }
        return (int)written;
    }

    /// <summary>
    /// Read data from the master (receives from child's stdout/stderr).
    /// Returns 0 on EOF (child closed).
    /// </summary>
    public int Read(byte[] buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        IntPtr bytesRead = read(_masterFd, buffer, (IntPtr)buffer.Length);
        if ((long)bytesRead == -1)
        {
            int errno = Marshal.GetLastWin32Error();
            // EAGAIN/EWOULDBLOCK means no data available (non-blocking)
            if (errno == 11 || errno == 35) // EAGAIN on Linux/macOS
                return 0;
            throw new IOException($"read from PTY master failed: errno={errno}");
        }
        return (int)bytesRead;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_masterFd != -1)
        {
            close(_masterFd);
            _masterFd = -1;
        }

        if (_slaveFd != -1)
        {
            close(_slaveFd);
            _slaveFd = -1;
        }
    }
}
