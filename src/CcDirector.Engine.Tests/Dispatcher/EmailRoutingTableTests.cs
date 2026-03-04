using CcDirector.Engine.Dispatcher;
using Xunit;

namespace CcDirector.Engine.Tests.Dispatcher;

public sealed class EmailRoutingTableTests
{
    private static EmailRoutingTable CreateTable(params EmailRoute[] routes)
    {
        return new EmailRoutingTable(routes);
    }

    [Fact]
    public void FindRoute_ExistingEmail_ReturnsRoute()
    {
        var route = new EmailRoute("test@example.com", @"C:\bin\cc-gmail.exe", "cc-gmail", "personal");
        var table = CreateTable(route);

        var result = table.FindRoute("test@example.com");

        Assert.NotNull(result);
        Assert.Equal("test@example.com", result.EmailAddress);
        Assert.Equal("cc-gmail", result.ToolName);
        Assert.Equal("personal", result.AccountName);
    }

    [Fact]
    public void FindRoute_CaseInsensitive_ReturnsRoute()
    {
        var route = new EmailRoute("Test@Example.com", @"C:\bin\cc-gmail.exe", "cc-gmail", "personal");
        var table = CreateTable(route);

        var result = table.FindRoute("test@example.com");

        Assert.NotNull(result);
        Assert.Equal("Test@Example.com", result.EmailAddress);
    }

    [Fact]
    public void FindRoute_UnknownEmail_ReturnsNull()
    {
        var route = new EmailRoute("test@example.com", @"C:\bin\cc-gmail.exe", "cc-gmail", "personal");
        var table = CreateTable(route);

        var result = table.FindRoute("unknown@example.com");

        Assert.Null(result);
    }

    [Fact]
    public void Count_ReturnsCorrectNumber()
    {
        var table = CreateTable(
            new EmailRoute("a@test.com", @"C:\bin\cc-gmail.exe", "cc-gmail", "a"),
            new EmailRoute("b@test.com", @"C:\bin\cc-outlook.exe", "cc-outlook", "b"));

        Assert.Equal(2, table.Count);
    }

    [Fact]
    public void Constructor_DuplicateEmails_FirstWins()
    {
        var first = new EmailRoute("a@test.com", @"C:\bin\cc-gmail.exe", "cc-gmail", "personal");
        var second = new EmailRoute("a@test.com", @"C:\bin\cc-outlook.exe", "cc-outlook", "work");
        var table = CreateTable(first, second);

        Assert.Equal(1, table.Count);
        var result = table.FindRoute("a@test.com");
        Assert.NotNull(result);
        Assert.Equal("cc-gmail", result.ToolName);
    }

    [Fact]
    public void AllRoutes_ReturnsAllRoutes()
    {
        var table = CreateTable(
            new EmailRoute("a@test.com", @"C:\bin\cc-gmail.exe", "cc-gmail", "a"),
            new EmailRoute("b@test.com", @"C:\bin\cc-outlook.exe", "cc-outlook", "b"));

        Assert.Equal(2, table.AllRoutes.Count);
    }

    [Fact]
    public void EmptyTable_FindRoute_ReturnsNull()
    {
        var table = CreateTable();

        Assert.Null(table.FindRoute("any@test.com"));
        Assert.Equal(0, table.Count);
    }
}
