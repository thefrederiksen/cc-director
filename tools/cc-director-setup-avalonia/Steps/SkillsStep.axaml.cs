using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

public partial class SkillsStep : UserControl
{
    public SkillsStep(bool isUpdate)
    {
        InitializeComponent();
        BuildSkillRows();

        if (isUpdate)
        {
            TitleText.Text = "Update Skills";
            DescriptionText.Text = "The following skills will be updated for Claude Code.";
        }

        SetupLog.Write($"[SkillsStep] Created: isUpdate={isUpdate}, skills={ToolInstaller.SkillNames.Length}");
    }

    private void BuildSkillRows()
    {
        SetupLog.Write("[SkillsStep] BuildSkillRows: creating UI rows");

        var accentBrush = SolidColorBrush.Parse("#007ACC");

        foreach (var skillName in ToolInstaller.SkillNames)
        {
            var card = new Border
            {
                Background = SolidColorBrush.Parse("#2A2D2E"),
                BorderBrush = accentBrush,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 4),
            };

            var topRow = new DockPanel();

            var checkbox = new TextBlock
            {
                Text = "[x]",
                Foreground = accentBrush,
                FontSize = 14,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Width = 24,
            };

            var nameText = new TextBlock
            {
                Text = skillName,
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };

            DockPanel.SetDock(checkbox, Dock.Left);
            topRow.Children.Add(checkbox);
            topRow.Children.Add(nameText);

            card.Child = topRow;
            SkillList.Children.Add(card);
        }

        SkillCountText.Text = $"{ToolInstaller.SkillNames.Length} skills will be installed";
    }
}
