using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using CcDirector.Avalonia.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Minimal Avalonia headless app so [AvaloniaFact] tests can construct real controls (the onboarding
/// wizard's Skip seam, issue #1809). No real drawing is needed - these tests assert config side effects,
/// not pixels - so headless drawing is left on.
/// </summary>
internal sealed class HeadlessTestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

internal static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
