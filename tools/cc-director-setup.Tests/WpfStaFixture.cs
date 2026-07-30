using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// A single, long-lived STA thread that owns the one <see cref="Application"/> this process may have,
/// with the wizard's REAL App.xaml resources loaded. WPF resource resolution is thread-affine, so
/// every control under test must be constructed on the thread that created the Application.
///
/// It loads App.xaml rather than re-declaring the brushes it needs. A hand-written copy of those
/// values cannot receive a correction: change a brush or add a style in App.xaml and the copy silently
/// keeps the old one, so a step that constructs here can still fail to construct in the product.
/// </summary>
public sealed class WpfStaFixture : IDisposable
{
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;
    private readonly ManualResetEventSlim _ready = new(false);

    public WpfStaFixture()
    {
        _thread = new Thread(() =>
        {
            // One Application per process, and .NET throws on a second one. The generated
            // InitializeComponent is what loads App.xaml's resources; only the wizard's own entry point
            // calls it normally, so it is called here.
            if (Application.Current is null)
            {
                var app = new CcDirectorSetup.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
            }

            _dispatcher = Dispatcher.CurrentDispatcher;
            _ready.Set();
            Dispatcher.Run();
        });
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>Run <paramref name="body"/> synchronously on the STA thread, surfacing any exception.</summary>
    public void Run(Action body)
    {
        Exception? captured = null;
        _dispatcher!.Invoke(() =>
        {
            try { body(); }
            catch (Exception ex) { captured = ex; }
        });
        if (captured != null)
            throw new Xunit.Sdk.XunitException($"WPF STA body failed: {captured}");
    }

    public void Dispose()
    {
        _dispatcher?.InvokeShutdown();
        _ready.Dispose();
    }
}

/// <summary>
/// Every test class that builds WPF controls joins this collection, so they share ONE fixture and run
/// one at a time. A per-class fixture would build a second <see cref="Application"/> the moment a
/// second class needed one, and .NET refuses that - which crashed the whole test host rather than
/// failing a test.
/// </summary>
[CollectionDefinition(Name)]
public sealed class WpfCollection : ICollectionFixture<WpfStaFixture>
{
    public const string Name = "wpf";
}
