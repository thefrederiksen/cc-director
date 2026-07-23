using System;
using Avalonia.Controls;
using CcDirector.Core.Git;

namespace CcDirector.Avalonia;

/// <summary>
/// A thin window hosting the Repository list. Transitional shell while the screen is a pop-up; the
/// list control is the same one that will embed in the main window later. The window only displays -
/// the <see cref="RepositoryMonitor"/> owns the state and the scanning.
/// </summary>
public partial class RepositoryScreenWindow : Window
{
    public RepositoryScreenWindow()
    {
        // The generated InitializeComponent wires the x:Name controls (ListView). Do NOT replace it
        // with a bare AvaloniaXamlLoader.Load(this) - that skips the field hookup and leaves ListView null.
        InitializeComponent();
    }

    public RepositoryScreenWindow(RepositoryMonitor monitor, Action? onRefreshRequested = null) : this()
    {
        if (onRefreshRequested != null)
            ListView.RefreshRequested += onRefreshRequested;
        ListView.Attach(monitor);
    }
}
