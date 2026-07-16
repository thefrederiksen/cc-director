using CcDirector.Core.Account;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// The rendering MECHANICS, tested independently of whatever text we happen to ship. These hold for
/// the user's own template too, which is the point: once a user can write this text, the renderer is
/// the only thing standing between their words and seven agents.
/// </summary>
public class FleetPreambleRendererTests
{
    private const string Id = "a3dfb85e-49dd-442a-9e36-40fc44838783";
    private static readonly SignedInUser User = new("soren@example.com", "Starlord");

    private static string Render(string template, SignedInUser? user = null, string? name = "devthrottle")
        => FleetPreambleRenderer.Render(template, Id, name, "MACHINE_A", @"C:\repos\devthrottle", user);

    [Fact]
    public void Render_SubstitutesEveryPlaceholder()
    {
        var text = Render(
            "[SESSION_ID] [SESSION_SHORT_ID] [SESSION_NAME] [MACHINE] [REPO_PATH]", User);

        Assert.Equal($"{Id} a3dfb85e devthrottle MACHINE_A C:\\repos\\devthrottle", text);
    }

    // The short id is a PREFIX of the full id, so a naive replace of [SESSION_ID] first would corrupt
    // [SESSION_SHORT_ID], and vice versa. Pinned because the failure is silent: the agent gets a
    // plausible-looking id that no fleet command accepts.
    [Fact]
    public void Render_ShortIdAndFullId_DoNotCorruptEachOther()
    {
        var text = Render("short=[SESSION_SHORT_ID] full=[SESSION_ID]", User);

        Assert.Equal($"short=a3dfb85e full={Id}", text);
    }

    // THE TRAP THIS RENDERER EXISTS TO AVOID. Our own default text opens with the literal
    // "[CC Director fleet]", and a user may write any bracketed prose they like. Only exact known
    // tokens are substituted; everything else is ordinary text.
    [Theory]
    [InlineData("[CC Director fleet] hello")]
    [InlineData("see [the docs] for more")]
    [InlineData("[SESSION_IDENTIFIER] is not a token")]
    [InlineData("[session_id] is case-sensitive and is not a token")]
    [InlineData("[]")]
    [InlineData("[[SESSION_UNKNOWN]]")]
    [InlineData("[SESSION_ID")]
    [InlineData("SESSION_ID]")]
    public void Render_LeavesUnknownBracketedTextExactlyAsWritten(string template)
    {
        Assert.Equal(template, Render(template, User));
    }

    // The exact boundary of the rule above, pinned because the honest version is narrower than
    // "unknown brackets survive". A KNOWN token expands wherever it appears - including nested inside
    // other brackets - so there is no way to write a literal, non-expanding [SESSION_ID]. Accepted:
    // an escape syntax would be a language the user must learn to edit a paragraph. Pinned so the
    // limitation is a decision rather than a surprise.
    //
    // Note the sibling test above cannot catch this: it uses UNKNOWN tokens, which survive whatever
    // the scanner does, so it would stay green even if this rule silently changed.
    [Theory]
    [InlineData("[[SESSION_SHORT_ID]]", "[a3dfb85e]")]
    [InlineData("write [SESSION_SHORT_ID] to expand it", "write a3dfb85e to expand it")]
    [InlineData("[MACHINE][MACHINE]", "MACHINE_AMACHINE_A")]
    public void Render_KnownToken_ExpandsWhereverItAppears_IncludingInsideOtherBrackets(
        string template, string expected)
    {
        Assert.Equal(expected, Render(template, User));
    }

    [Fact]
    public void Render_SignedIn_KeepsTheBlockAndDropsOnlyTheMarkers()
    {
        var text = Render("before\n[IF_SIGNED_IN]\nhello [USER_NAME] <[USER_EMAIL]>\n[END_IF]\nafter", User);

        Assert.Equal("before\nhello Starlord <soren@example.com>\nafter", text);
    }

    // The block must vanish WHOLE - no blank line where it was. A stray blank line is the kind of
    // thing nobody notices in review and everybody sees in the agent's context.
    [Fact]
    public void Render_NotSignedIn_DropsTheBlockLeavingNoBlankLine()
    {
        var text = Render("before\n[IF_SIGNED_IN]\nhello [USER_NAME]\n[END_IF]\nafter", user: null);

        Assert.Equal("before\nafter", text);
    }

    // A user with no email is not identified, whatever else the account carries.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Render_SignedInUserWithoutAnEmail_IsTreatedAsNotSignedIn(string email)
    {
        var text = Render("[IF_SIGNED_IN]\nhello\n[END_IF]\nafter", new SignedInUser(email, "Starlord"));

        Assert.Equal("after", text);
    }

    [Fact]
    public void Render_UnnamedSession_UsesThePlaceholderName()
    {
        Assert.Equal("(unnamed)", Render("[SESSION_NAME]", User, name: null));
        Assert.Equal("(unnamed)", Render("[SESSION_NAME]", User, name: ""));
        Assert.Equal("(unnamed)", Render("[SESSION_NAME]", User, name: "   "));
    }

    // A value that happens to look like a token is DATA, not a token: substitution runs once over the
    // template, never over its own output. Otherwise naming a session "[MACHINE]" would rewrite it.
    [Fact]
    public void Render_ValuesThatLookLikeTokens_AreNotSubstitutedAgain()
    {
        var text = FleetPreambleRenderer.Render(
            "[SESSION_NAME]", Id, "[MACHINE]", "MACHINE_A", @"C:\repos\devthrottle", User);

        Assert.Equal("[MACHINE]", text);
    }

    // A user pasting into the Settings tab produces \r\n. A stray carriage return renders as a control
    // character in a terminal, so it is normalized here rather than shipped to seven agents.
    [Fact]
    public void Render_WindowsLineEndings_AreNormalized()
    {
        var text = Render("one\r\ntwo\r\n[IF_SIGNED_IN]\r\nhello\r\n[END_IF]\r\nthree", User);

        Assert.Equal("one\ntwo\nhello\nthree", text);
        Assert.DoesNotContain('\r', text);
    }

    [Fact]
    public void Render_TemplateWithNoPlaceholders_IsReturnedUnchanged()
    {
        Assert.Equal("just some words", Render("just some words", User));
    }

    // The user is allowed to throw our text away entirely - including the fleet commands and the
    // no-attribution rule. That is the whole point of the feature, so an empty template renders empty
    // rather than failing or resurrecting a default.
    [Fact]
    public void Render_EmptyTemplate_RendersEmpty()
    {
        Assert.Equal("", Render("", User));
    }

    [Theory]
    [InlineData("[IF_SIGNED_IN]\nhello", "never closed")]
    [InlineData("hello\n[END_IF]", "no matching")]
    [InlineData("[IF_SIGNED_IN]\n[IF_SIGNED_IN]\nhello\n[END_IF]\n[END_IF]", "cannot be nested")]
    public void Validate_UnbalancedConditionals_AreRejectedInPlainEnglish(string template, string expected)
    {
        var problem = FleetPreambleRenderer.Validate(template);

        Assert.NotNull(problem);
        Assert.Contains(expected, problem);
    }

    [Fact]
    public void Validate_TheShippedDefault_IsWellFormed()
    {
        Assert.Null(FleetPreambleRenderer.Validate(FleetPreambleTemplate.Default));
    }

    // Rendering a malformed template throws rather than guessing which half the author meant. The
    // Settings tab validates at save, so a user never reaches this by typing.
    [Fact]
    public void Render_MalformedTemplate_ThrowsRatherThanGuessing()
    {
        var ex = Assert.Throws<FleetPreambleTemplateException>(() => Render("[IF_SIGNED_IN]\nhello", User));

        Assert.Contains("never closed", ex.Message);
    }
}
