using System.Reflection;
using System.Text;
using CcDirector.Launcher;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Launcher.Tests;

public sealed class LauncherNamedInstanceEndpointTests
{
    [Fact]
    public async Task LifecycleBody_WithNoContentLength_StillCarriesTheNamedInstance()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = null;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"instance\":\"spare\"}"));

        var method = typeof(LauncherHost).GetMethod(
            "ReadInstanceAsync",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var read = Assert.IsType<Task<string?>>(method.Invoke(null, new object[] { context }));

        Assert.Equal("spare", await read);
    }
}
