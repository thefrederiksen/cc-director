using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests;

public class FileExtensionsTests
{
    [Theory]
    [InlineData("readme.md", true)]
    [InlineData("CHANGELOG.MD", true)]
    [InlineData("notes.markdown", true)]
    [InlineData("file.txt", false)]
    [InlineData("image.png", false)]
    [InlineData("", false)]
    public void IsMarkdown_ReturnsExpected(string path, bool expected)
    {
        Assert.Equal(expected, FileExtensions.IsMarkdown(path));
    }

    [Theory]
    [InlineData("photo.png", true)]
    [InlineData("photo.PNG", true)]
    [InlineData("image.jpg", true)]
    [InlineData("image.jpeg", true)]
    [InlineData("anim.gif", true)]
    [InlineData("icon.ico", true)]
    [InlineData("pic.bmp", true)]
    [InlineData("hero.webp", true)]
    [InlineData("file.txt", false)]
    [InlineData("readme.md", false)]
    [InlineData("", false)]
    public void IsImage_ReturnsExpected(string path, bool expected)
    {
        Assert.Equal(expected, FileExtensions.IsImage(path));
    }

    [Theory]
    [InlineData("file.txt", true)]
    [InlineData("app.log", true)]
    [InlineData("data.csv", true)]
    [InlineData("config.json", true)]
    [InlineData("layout.xml", true)]
    [InlineData("config.yaml", true)]
    [InlineData("config.yml", true)]
    [InlineData("settings.ini", true)]
    [InlineData("settings.toml", true)]
    [InlineData("Program.cs", true)]
    [InlineData("script.py", true)]
    [InlineData("index.js", true)]
    [InlineData("app.ts", true)]
    [InlineData("run.ps1", true)]
    [InlineData("run.bat", true)]
    [InlineData("run.sh", true)]
    [InlineData("query.sql", true)]
    [InlineData("page.html", true)]
    [InlineData("style.css", true)]
    [InlineData("drawing.svg", true)]
    [InlineData("component.tsx", true)]
    [InlineData("component.jsx", true)]
    [InlineData("main.rs", true)]
    [InlineData("main.go", true)]
    [InlineData("App.java", true)]
    [InlineData("main.cpp", true)]
    [InlineData("main.c", true)]
    [InlineData("header.h", true)]
    [InlineData("header.hpp", true)]
    [InlineData("app.rb", true)]
    [InlineData("index.php", true)]
    [InlineData(".gitignore", true)]
    [InlineData(".editorconfig", true)]
    [InlineData("readme.md", false)]
    [InlineData("image.png", false)]
    [InlineData("app.exe", false)]
    [InlineData("lib.dll", false)]
    [InlineData("", false)]
    public void IsTextFile_ReturnsExpected(string path, bool expected)
    {
        Assert.Equal(expected, FileExtensions.IsTextFile(path));
    }

    [Theory]
    [InlineData("readme.md", true)]
    [InlineData("photo.png", true)]
    [InlineData("file.txt", true)]
    [InlineData("Program.cs", true)]
    [InlineData("app.exe", false)]
    [InlineData("lib.dll", false)]
    [InlineData("archive.zip", false)]
    [InlineData("", false)]
    public void IsViewable_ReturnsExpected(string path, bool expected)
    {
        Assert.Equal(expected, FileExtensions.IsViewable(path));
    }

    [Theory]
    [InlineData("readme.md", FileViewerCategory.Markdown)]
    [InlineData("notes.markdown", FileViewerCategory.Markdown)]
    [InlineData("photo.png", FileViewerCategory.Image)]
    [InlineData("icon.ico", FileViewerCategory.Image)]
    [InlineData("file.txt", FileViewerCategory.Text)]
    [InlineData("Program.cs", FileViewerCategory.Text)]
    [InlineData("config.json", FileViewerCategory.Text)]
    [InlineData("app.exe", FileViewerCategory.None)]
    [InlineData("", FileViewerCategory.None)]
    public void GetViewerCategory_ReturnsExpected(string path, FileViewerCategory expected)
    {
        Assert.Equal(expected, FileExtensions.GetViewerCategory(path));
    }
}
