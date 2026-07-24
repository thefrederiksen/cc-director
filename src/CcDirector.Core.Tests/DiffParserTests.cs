using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

public class DiffParserTests
{
    private const string Simple = """
diff --git a/src/App.cs b/src/App.cs
index 1111111..2222222 100644
--- a/src/App.cs
+++ b/src/App.cs
@@ -10,4 +10,5 @@ class App
 line ten
-old line eleven
+new line eleven
+inserted twelve
 line thirteen
""";

    [Fact]
    public void Parse_SingleFile_HunkLinesAndNumbers()
    {
        var files = DiffParser.Parse(Simple);

        var f = Assert.Single(files);
        Assert.Equal("src/App.cs", f.OldPath);
        Assert.Equal("src/App.cs", f.NewPath);
        Assert.False(f.IsBinary);
        Assert.False(f.IsRename);
        Assert.Equal(2, f.Added);
        Assert.Equal(1, f.Removed);

        var h = Assert.Single(f.Hunks);
        Assert.StartsWith("@@ -10,4 +10,5 @@", h.Header);
        Assert.Equal(5, h.Lines.Count);

        Assert.Equal(DiffLineKind.Context, h.Lines[0].Kind);
        Assert.Equal(10, h.Lines[0].OldNumber);
        Assert.Equal(10, h.Lines[0].NewNumber);

        Assert.Equal(DiffLineKind.Removed, h.Lines[1].Kind);
        Assert.Equal(11, h.Lines[1].OldNumber);
        Assert.Null(h.Lines[1].NewNumber);

        Assert.Equal(DiffLineKind.Added, h.Lines[2].Kind);
        Assert.Null(h.Lines[2].OldNumber);
        Assert.Equal(11, h.Lines[2].NewNumber);

        Assert.Equal(DiffLineKind.Added, h.Lines[3].Kind);
        Assert.Equal(12, h.Lines[3].NewNumber);

        Assert.Equal(DiffLineKind.Context, h.Lines[4].Kind);
        Assert.Equal(12, h.Lines[4].OldNumber);
        Assert.Equal(13, h.Lines[4].NewNumber);
    }

    [Fact]
    public void Parse_TwoFiles_SplitsCorrectly()
    {
        var two = Simple + "\n" + """
diff --git a/b.txt b/b.txt
--- a/b.txt
+++ b/b.txt
@@ -1 +1 @@
-x
+y
""";
        var files = DiffParser.Parse(two);
        Assert.Equal(2, files.Count);
        Assert.Equal("b.txt", files[1].NewPath);
        Assert.Equal(1, files[1].Added);
        Assert.Equal(1, files[1].Removed);
    }

    [Fact]
    public void Parse_Rename_IsFlagged()
    {
        var rename = """
diff --git a/old-name.cs b/new-name.cs
similarity index 97%
rename from old-name.cs
rename to new-name.cs
""";
        var f = Assert.Single(DiffParser.Parse(rename));
        Assert.True(f.IsRename);
        Assert.Equal("old-name.cs", f.OldPath);
        Assert.Equal("new-name.cs", f.NewPath);
    }

    [Fact]
    public void Parse_Binary_IsFlagged()
    {
        var binary = """
diff --git a/logo.png b/logo.png
index 1111111..2222222 100644
Binary files a/logo.png and b/logo.png differ
""";
        var f = Assert.Single(DiffParser.Parse(binary));
        Assert.True(f.IsBinary);
        Assert.Empty(f.Hunks);
    }

    [Fact]
    public void Parse_NewFile_DevNullOldPath()
    {
        var added = """
diff --git a/fresh.txt b/fresh.txt
new file mode 100644
--- /dev/null
+++ b/fresh.txt
@@ -0,0 +1,2 @@
+hello
+world
""";
        var f = Assert.Single(DiffParser.Parse(added));
        Assert.Equal("", f.OldPath);
        Assert.Equal("fresh.txt", f.NewPath);
        Assert.Equal(2, f.Added);
    }

    [Fact]
    public void Parse_NoNewlineMarker_IsSkippedNotRendered()
    {
        var diff = """
diff --git a/a.txt b/a.txt
--- a/a.txt
+++ b/a.txt
@@ -1 +1 @@
-x
+y
\ No newline at end of file
""";
        var f = Assert.Single(DiffParser.Parse(diff));
        var h = Assert.Single(f.Hunks);
        Assert.Equal(2, h.Lines.Count); // the backslash metadata line is not content
    }

    [Fact]
    public void Parse_EmptyOrNull_ReturnsNoFiles()
    {
        Assert.Empty(DiffParser.Parse(""));
        Assert.Empty(DiffParser.Parse(null));
    }

    [Theory]
    [InlineData("@@ -10,4 +12,5 @@", 10, 12)]
    [InlineData("@@ -1 +1 @@", 1, 1)]
    [InlineData("@@ garbage @@", 1, 1)] // malformed header renders from line 1 rather than crashing
    public void ParseHunkHeader_ExtractsStarts(string header, int oldStart, int newStart)
    {
        var (o, n) = DiffParser.ParseHunkHeader(header);
        Assert.Equal(oldStart, o);
        Assert.Equal(newStart, n);
    }
}
