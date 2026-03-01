using CcClick.Helpers;
using Xunit;

namespace CcClick.Tests;

public class ElementFinderTests
{
    [Fact]
    public void FindElement_BothNameAndIdNull_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ElementFinder.FindElement(null!, null!, null, null));

        Assert.Equal("Either --name or --id must be specified", ex.Message);
    }

    [Fact]
    public void FindElement_BothNameAndIdEmpty_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ElementFinder.FindElement(null!, null!, "", ""));

        Assert.Equal("Either --name or --id must be specified", ex.Message);
    }
}
