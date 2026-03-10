using System.Windows;
using System.Windows.Controls;

namespace CcDirector.Wpf.Controls;

/// <summary>
/// Routes CleanWidgetViewModel items to the appropriate DataTemplate
/// based on their WidgetKind.
/// </summary>
public class WidgetTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }
    public DataTemplate? ThinkingTemplate { get; set; }
    public DataTemplate? BashTemplate { get; set; }
    public DataTemplate? FileTemplate { get; set; }
    public DataTemplate? SearchTemplate { get; set; }
    public DataTemplate? TodoTemplate { get; set; }
    public DataTemplate? UserTemplate { get; set; }
    public DataTemplate? SkillTemplate { get; set; }
    public DataTemplate? GenericToolTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not CleanWidgetViewModel vm)
            return base.SelectTemplate(item, container);

        return vm.Kind switch
        {
            WidgetKind.Text => TextTemplate,
            WidgetKind.Thinking => ThinkingTemplate,
            WidgetKind.Bash => BashTemplate,
            WidgetKind.Read or WidgetKind.Write or WidgetKind.Edit => FileTemplate,
            WidgetKind.Grep or WidgetKind.Glob => SearchTemplate,
            WidgetKind.TodoWrite => TodoTemplate,
            WidgetKind.UserMessage => UserTemplate,
            WidgetKind.Agent => GenericToolTemplate,
            WidgetKind.Skill => SkillTemplate,
            WidgetKind.GenericTool => GenericToolTemplate,
            _ => GenericToolTemplate
        };
    }
}
