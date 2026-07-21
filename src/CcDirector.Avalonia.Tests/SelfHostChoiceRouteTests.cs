using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.GatewayConnection;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The self-host choice route (#1810), guarded from both sides at once:
///
///  - the route EXISTS and goes to the provisioning transaction, and
///  - the card that would fire it is still DISABLED, so no user can reach it.
///
/// Both matter together. Wiring without the disable would expose a path that installs and starts a
/// real Gateway before any real install has proven it. The disable without the wiring would mean the
/// transaction gets bolted on later, in the same change that first exposes it to a stranger's
/// machine - which is exactly when nobody wants to be discovering the wiring for the first time.
/// </summary>
public class SelfHostChoiceRouteTests
{
    private static Border CardFor(GatewayConnectionPanel panel, GatewayChoiceAction action) =>
        (Border)panel.ChoiceCardsForTests.First(c => c is Border { Tag: GatewayChoiceAction a } && a == action);

    private static GatewayConnectionPanel WindowsPanel()
    {
        var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Onboarding);
        // Force a self-host-capable (Windows) context regardless of the test host.
        panel.SetChoiceContextForTests(
            new GatewayChoiceContext(GatewayChoiceConsumer.Onboarding, SelfHostSupported: true));
        panel.ShowChoiceForTests();
        return panel;
    }

    [AvaloniaFact]
    public void SelfHostChoice_IsRoutedToTheProvisioningTransaction()
    {
        var panel = WindowsPanel();
        var raised = 0;
        panel.SelfHostRequested += (_, _) => raised++;

        panel.InvokeChoiceForTests(GatewayChoiceAction.SelfHost);

        Assert.Equal(1, raised);
    }

    [AvaloniaFact]
    public void SelfHostCard_IsStillDisabled_AndCannotFireTheRoute()
    {
        var panel = WindowsPanel();
        var raised = 0;
        panel.SelfHostRequested += (_, _) => raised++;

        Assert.False(CardFor(panel, GatewayChoiceAction.SelfHost).IsEnabled);

        // Going through the CARD, exactly as a click would: a disabled card must do nothing at all.
        panel.ActivateChoiceForTests(GatewayChoiceAction.SelfHost);

        Assert.Equal(0, raised);
    }

    [AvaloniaFact]
    public void SelfHostCard_StatesTheAlwaysOnAndTailscaleTradeoffs()
    {
        // The tradeoffs belong on the card, where the choice is made. A user who discovers after
        // provisioning that the machine must stay awake, or that reaching it from a phone needs
        // Tailscale, has been sold something (#1810).
        var text = TextOf(CardFor(WindowsPanel(), GatewayChoiceAction.SelfHost));

        Assert.Contains("stay on", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tailscale", text, StringComparison.OrdinalIgnoreCase);
        // Capability tradeoffs only - self-hosting is NOT sold as a privacy guarantee.
        Assert.DoesNotContain("private", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", text, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void OtherChoices_AreUnaffectedByTheSelfHostRoute()
    {
        var panel = WindowsPanel();

        // The regression guard that matters: adding a self-host route must not disturb the working paths.
        // Join, Skip, and (now) hosted are the enabled choices; only self-host stays disabled.
        Assert.True(CardFor(panel, GatewayChoiceAction.JoinExisting).IsEnabled);
        Assert.True(CardFor(panel, GatewayChoiceAction.Skip).IsEnabled);
        Assert.True(CardFor(panel, GatewayChoiceAction.UseHosted).IsEnabled);
    }

    private static string TextOf(Control control)
    {
        var parts = new List<string>();
        Collect(control, parts);
        return string.Join(" ", parts);

        static void Collect(Control c, List<string> into)
        {
            if (c is TextBlock { Text: { } t }) into.Add(t);
            foreach (var child in c.GetVisualChildren())
                if (child is Control cc) Collect(cc, into);
        }
    }
}
