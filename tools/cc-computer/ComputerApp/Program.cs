using System.IO;
using System.Runtime.InteropServices;

namespace CCComputer.App;

public static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateFileW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpFileName,
        uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--cli")
        {
            // Attach to parent console (the terminal that launched us)
            // If no parent console, allocate a new one
            if (!AttachConsole(ATTACH_PARENT_PROCESS))
            {
                AllocConsole();
            }

            // WinExe apps have null console handles at startup.
            // After AttachConsole/AllocConsole, reopen the standard streams
            // so Console.Write* actually produces output.
            ReopenConsoleStreams();

            // Remaining args after --cli are the optional command
            var command = args.Length > 1 ? string.Join(" ", args[1..]) : null;

            var runner = new ConsoleRunner();
            runner.RunAsync(command).GetAwaiter().GetResult();
        }
        else
        {
            // Normal WPF GUI mode
            var app = new App();
            app.StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
            app.Run();
        }
    }

    /// <summary>
    /// After AttachConsole/AllocConsole, .NET's Console still has the cached null handles
    /// from WinExe startup. Reopen CONOUT$ so Console.Write* works.
    /// </summary>
    private static void ReopenConsoleStreams()
    {
        var conOut = CreateFileW("CONOUT$", GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

        if (conOut != IntPtr.Zero && conOut != new IntPtr(-1))
        {
            SetStdHandle(STD_OUTPUT_HANDLE, conOut);
            SetStdHandle(STD_ERROR_HANDLE, conOut);

            var stream = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(conOut, false),
                FileAccess.Write);
            var writer = new StreamWriter(stream) { AutoFlush = true };
            Console.SetOut(writer);
            Console.SetError(writer);
        }

        var conIn = CreateFileW("CONIN$", GENERIC_READ, 0x00000001 /* FILE_SHARE_READ */,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

        if (conIn != IntPtr.Zero && conIn != new IntPtr(-1))
        {
            var inStream = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(conIn, false),
                FileAccess.Read);
            var reader = new StreamReader(inStream);
            Console.SetIn(reader);
        }
    }
}
