using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The Repositories home: a left sub-rail hosting the live repository list and the root-folder
/// registration, shown as a pinned, non-modal view inside the main window (issue #507, phase 4).
/// It renders the always-current model from the background <see cref="RepositoryMonitor"/>; it does
/// not scan.
/// </summary>
public partial class RepositoriesView : UserControl
{
    private bool _attached;

    public RepositoriesView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Wire the live model and the registered roots. Idempotent - safe to call each time the view is
    /// opened; the monitor subscription is set up once, the roots list is refreshed each time.
    /// </summary>
    public void Attach(RepositoryMonitor monitor, RootDirectoryStore store, Action onRefreshRequested)
    {
        if (!_attached)
        {
            FileLog.Write("[RepositoriesView] Attach");
            ReposPage.RefreshRequested += onRefreshRequested;
            ReposPage.Attach(monitor);
            _attached = true;
        }
        RootsList.ItemsSource = store.Roots
            .Select(r => $"{(string.IsNullOrWhiteSpace(r.Label) ? r.Path : r.Label)}   [{r.ProviderDisplayName}]   {r.Path}")
            .ToList();
    }

    private void ReposRailButton_Click(object? sender, RoutedEventArgs e) => ShowRoots(false);
    private void RootsRailButton_Click(object? sender, RoutedEventArgs e) => ShowRoots(true);

    private void ShowRoots(bool roots)
    {
        ReposPage.IsVisible = !roots;
        RootsPage.IsVisible = roots;
        SetActive(ReposRailButton, !roots);
        SetActive(RootsRailButton, roots);
    }

    private static void SetActive(Button button, bool active)
    {
        if (active) button.Classes.Add("active");
        else button.Classes.Remove("active");
    }
}
