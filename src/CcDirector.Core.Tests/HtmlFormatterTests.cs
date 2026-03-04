using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests;

public class HtmlFormatterTests
{
    [Fact]
    public void ConvertPlainTextToHtml_EmptyString_ReturnsEmpty()
    {
        var result = HtmlFormatter.ConvertPlainTextToHtml("");
        Assert.Equal("", result);
    }

    [Fact]
    public void ConvertPlainTextToHtml_WhitespaceOnly_ReturnsWhitespace()
    {
        var result = HtmlFormatter.ConvertPlainTextToHtml("   ");
        Assert.Equal("   ", result);
    }

    [Fact]
    public void ConvertPlainTextToHtml_SingleParagraph_WrapsInPTags()
    {
        var result = HtmlFormatter.ConvertPlainTextToHtml("Hello world");
        Assert.Equal("<p>Hello world</p>", result);
    }

    [Fact]
    public void ConvertPlainTextToHtml_TwoParagraphs_CreatesTwoPTags()
    {
        var result = HtmlFormatter.ConvertPlainTextToHtml("First paragraph\n\nSecond paragraph");
        Assert.Contains("<p>First paragraph</p>", result);
        Assert.Contains("<p>Second paragraph</p>", result);
    }

    [Fact]
    public void ConvertPlainTextToHtml_SingleNewline_ConvertsToBr()
    {
        var result = HtmlFormatter.ConvertPlainTextToHtml("Line one\nLine two");
        Assert.Contains("<br>", result);
        Assert.Contains("Line one", result);
        Assert.Contains("Line two", result);
    }

    [Fact]
    public void ConvertPlainTextToHtml_MixedBreaks_CorrectConversion()
    {
        var input = "Dear John,\n\nThank you for your message.\nI appreciate it.\n\nBest regards,\nSoren";
        var result = HtmlFormatter.ConvertPlainTextToHtml(input);

        // Should have 3 paragraphs
        Assert.Contains("<p>Dear John,</p>", result);
        Assert.Contains("<p>Thank you for your message.<br>", result);
        Assert.Contains("I appreciate it.</p>", result);
        Assert.Contains("<p>Best regards,<br>", result);
        Assert.Contains("Soren</p>", result);
    }

    [Fact]
    public void ConvertPlainTextToHtml_WindowsLineEndings_NormalizedCorrectly()
    {
        var result = HtmlFormatter.ConvertPlainTextToHtml("First\r\n\r\nSecond");
        Assert.Contains("<p>First</p>", result);
        Assert.Contains("<p>Second</p>", result);
    }

    [Fact]
    public void ConvertPlainTextToHtml_AlreadyHtmlWithPTags_PassesThrough()
    {
        var html = "<p>Already formatted</p><p>Second paragraph</p>";
        var result = HtmlFormatter.ConvertPlainTextToHtml(html);
        Assert.Equal(html, result);
    }

    [Fact]
    public void ConvertPlainTextToHtml_AlreadyHtmlWithBr_PassesThrough()
    {
        var html = "Line one<br>Line two";
        var result = HtmlFormatter.ConvertPlainTextToHtml(html);
        Assert.Equal(html, result);
    }

    [Fact]
    public void ConvertPlainTextToHtml_AlreadyHtmlWithDiv_PassesThrough()
    {
        var html = "<div>Content</div>";
        var result = HtmlFormatter.ConvertPlainTextToHtml(html);
        Assert.Equal(html, result);
    }

    [Fact]
    public void ConvertPlainTextToHtml_AlreadyHtmlWithTable_PassesThrough()
    {
        var html = "<table><tr><td>Cell</td></tr></table>";
        var result = HtmlFormatter.ConvertPlainTextToHtml(html);
        Assert.Equal(html, result);
    }
}
