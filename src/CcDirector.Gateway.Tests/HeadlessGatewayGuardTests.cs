using System.Linq;
using System.Reflection;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Gateway is being reshaped to behave like a Windows service, and the shape has one rule:
/// <b>the Gateway library must run with no user interface at all.</b>
///
/// CcDirector.GatewayApp (the tray exe) is deliberately a thin SHIM around this library - it puts a
/// Start/Stop control in the notification area and nothing else. Everything that makes the Gateway work
/// lives HERE, in the headless library, so that when the Gateway becomes a real service the shim is simply
/// deleted and nothing else changes.
///
/// That rule is invisible at a code review and easy to break by accident: one convenient
/// <c>using Avalonia;</c> in a library file, added to show a dialog or read a Dispatcher, silently makes
/// the "headless" Gateway depend on a windowing toolkit and quietly ends the service plan. These tests are
/// the tripwire. If one fails, the fix is not to relax it - it is to move the user interface into the shim
/// (or delete it), which is the whole point of the exercise.
///
/// Known limit, verified rather than assumed: these read the compiled assembly's REFERENCES, and the
/// compiler only emits a reference the code actually USES. Adding a bare PackageReference to the library
/// without touching a type does NOT trip these - confirmed by trying it. That is the intended semantic
/// (an unused reference does not make the Gateway need a desktop), but it does mean a green result here
/// says "no windowing code is reachable", not "no windowing package is listed in the csproj".
/// </summary>
public class HeadlessGatewayGuardTests
{
    private static Assembly GatewayLibrary => typeof(GatewayHost).Assembly;

    [Fact]
    public void GatewayLibrary_takesNoDependencyOnAWindowingToolkit()
    {
        var offenders = GatewayLibrary
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .Where(n => n.StartsWith("Avalonia", System.StringComparison.OrdinalIgnoreCase)
                        || n.StartsWith("System.Windows", System.StringComparison.OrdinalIgnoreCase)
                        || n.StartsWith("PresentationFramework", System.StringComparison.OrdinalIgnoreCase)
                        || n.StartsWith("System.Drawing", System.StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The Gateway library must stay headless so the tray shim can be deleted without breaking it, "
            + "but it now references a windowing toolkit: " + string.Join(", ", offenders)
            + ". Move whatever needed this into CcDirector.GatewayApp, or remove it.");
    }

    [Fact]
    public void GatewayLibrary_doesNotReferenceTheTrayShimOrItsUi()
    {
        var offenders = GatewayLibrary
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .Where(n => n.Contains("GatewayApp", System.StringComparison.OrdinalIgnoreCase)
                        || n.Contains("TrayUi", System.StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The Gateway library must not depend on the tray shim (that would invert the whole design), "
            + "but it references: " + string.Join(", ", offenders));
    }
}
