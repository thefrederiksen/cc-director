using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CcDirector.Core.Communications.Models;

namespace CcDirector.Avalonia.Controls.CommManager;

/// <summary>
/// Routes ContentItem instances to the appropriate platform preview DataTemplate.
/// Follows the same IDataTemplate pattern as WidgetTemplateSelector.
/// </summary>
public class PlatformPreviewSelector : IDataTemplate
{
    public IDataTemplate? LinkedInTemplate { get; set; }
    public IDataTemplate? TwitterTemplate { get; set; }
    public IDataTemplate? RedditTemplate { get; set; }
    public IDataTemplate? EmailTemplate { get; set; }
    public IDataTemplate? ArticleTemplate { get; set; }
    public IDataTemplate? FacebookTemplate { get; set; }
    public IDataTemplate? WhatsAppTemplate { get; set; }
    public IDataTemplate? YouTubeTemplate { get; set; }
    public IDataTemplate? DefaultTemplate { get; set; }

    public Control? Build(object? param)
    {
        if (param is not ContentItem item)
            return new TextBlock { Text = "No item selected" };

        var type = item.Type?.ToLower() ?? "";

        // Articles get special treatment regardless of platform
        if (type == "article")
            return (ArticleTemplate ?? DefaultTemplate)?.Build(param) ?? BuildFallback(item);

        var platform = item.Platform?.ToLower() ?? "";
        var template = platform switch
        {
            "linkedin" => LinkedInTemplate ?? DefaultTemplate,
            "twitter" => TwitterTemplate ?? DefaultTemplate,
            "reddit" => RedditTemplate ?? DefaultTemplate,
            "email" => EmailTemplate ?? DefaultTemplate,
            "blog" => ArticleTemplate ?? DefaultTemplate,
            "facebook" => FacebookTemplate ?? DefaultTemplate,
            "whatsapp" => WhatsAppTemplate ?? DefaultTemplate,
            "youtube" => YouTubeTemplate ?? DefaultTemplate,
            _ => DefaultTemplate
        };

        return template?.Build(param) ?? BuildFallback(item);
    }

    private static TextBlock BuildFallback(ContentItem item)
    {
        return new TextBlock { Text = $"[{item.Platform}] {item.DisplayTitle}" };
    }

    public bool Match(object? data) => data is ContentItem;
}
