using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Git;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// The hand-off dialog: the owner chooses an outcome in plain language, sees EXACTLY the brief the
/// agent will receive, and confirms. Returns the brief text (dialog result), or null on cancel. The
/// host then spawns a session in the repository with the brief staged in the prompt box - the owner
/// reads and sends it; the agent never starts unseen.
/// </summary>
public partial class HandToAgentDialog : Window
{
    private readonly RepositoryStatus? _repo;

    public HandToAgentDialog()
    {
        InitializeComponent();
    }

    public HandToAgentDialog(RepositoryStatus repo) : this()
    {
        _repo = repo;
        HeaderText.Text = $"Hand {repo.Name} to an agent";
        SubText.Text = repo.IsClean
            ? "The tree is clean - an agent can still explain or tidy branches and worktrees."
            : $"{repo.UncommittedCount} uncommitted file(s). Choose what the agent should do:";
        UpdatePreview();
    }

    private AgentHandOffKind SelectedKind =>
        OptCleanup.IsChecked == true ? AgentHandOffKind.CleanUp
        : OptExplain.IsChecked == true ? AgentHandOffKind.ExplainOnly
        : AgentHandOffKind.ProtectChanges;

    private void Option_Changed(object? sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (_repo is null || BriefPreview is null)
            return;
        BriefPreview.Text = AgentBriefTemplates.Build(SelectedKind, _repo);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void StartButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_repo is null)
        {
            Close(null);
            return;
        }
        FileLog.Write($"[HandToAgentDialog] hand-off chosen: {SelectedKind} for {_repo.Path}");
        Close(AgentBriefTemplates.Build(SelectedKind, _repo));
    }
}
