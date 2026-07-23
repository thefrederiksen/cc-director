using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;

namespace CcDirector.Avalonia;

/// <summary>
/// A thin window hosting the Repository list. Transitional shell while the screen is a pop-up; the
/// list control is the same one that will embed in the main window later.
/// </summary>
public partial class RepositoryScreenWindow : Window
{
    public RepositoryScreenWindow()
    {
        // The generated InitializeComponent wires the x:Name controls (ListView). Do NOT replace it
        // with a bare AvaloniaXamlLoader.Load(this) - that skips the field hookup and leaves ListView null.
        InitializeComponent();
    }

    public RepositoryScreenWindow(RootDirectoryStore store,
        Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>>? liveSessionsProvider = null) : this()
    {
        ListView.LiveSessionsProvider = liveSessionsProvider;
        ListView.Attach(store);
    }
}
