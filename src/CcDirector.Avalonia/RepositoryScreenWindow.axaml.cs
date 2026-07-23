using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
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
        InitializeComponent();
    }

    public RepositoryScreenWindow(RootDirectoryStore store,
        Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>>? liveSessionsProvider = null) : this()
    {
        ListView.LiveSessionsProvider = liveSessionsProvider;
        ListView.Attach(store);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
