using System.Windows;
using System.Windows.Controls;
using CommunicationManager.Models;

namespace CommunicationManager.Views;

public class PlatformTemplateSelector : DataTemplateSelector
{
    public DataTemplate? LinkedInTemplate { get; set; }
    public DataTemplate? TwitterTemplate { get; set; }
    public DataTemplate? RedditTemplate { get; set; }
    public DataTemplate? EmailTemplate { get; set; }
    public DataTemplate? ArticleTemplate { get; set; }
    public DataTemplate? DefaultTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is not ContentItem contentItem)
            return DefaultTemplate;

        var platform = contentItem.Platform?.ToLower() ?? "";
        var type = contentItem.Type?.ToLower() ?? "";

        // Articles get special treatment regardless of platform
        if (type == "article")
            return ArticleTemplate ?? DefaultTemplate;

        return platform switch
        {
            "linkedin" => LinkedInTemplate ?? DefaultTemplate,
            "twitter" => TwitterTemplate ?? DefaultTemplate,
            "reddit" => RedditTemplate ?? DefaultTemplate,
            "email" => EmailTemplate ?? DefaultTemplate,
            "blog" => ArticleTemplate ?? DefaultTemplate,
            _ => DefaultTemplate
        };
    }
}
