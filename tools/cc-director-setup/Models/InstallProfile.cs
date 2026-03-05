namespace CcDirectorSetup.Models;

public enum InstallProfile
{
    Developer,
    Standard
}

public static class ProfileToolLists
{
    public static readonly string[] StandardTools =
    [
        "cc-pdf",
        "cc-html",
        "cc-word",
        "cc-excel",
        "cc-powerpoint",
        "cc-outlook",
        "cc-gmail",
        "cc-vault",
        "cc-settings",
        "cc-image",
        "cc-photos",
        "cc-transcribe",
        "cc-whisper",
        "cc-voice",
        "cc-video",
    ];

    public static readonly string[] DeveloperOnlyTools =
    [
        "cc-browser",
        "cc-fox-browser",
        "cc-linkedin",
        "cc-reddit",
        "cc-twitter",
        "cc-facebook",
        "cc-crawl4ai",
        "cc-docgen",
        "cc-hardware",
        "cc-personresearch",
        "cc-posthog",
        "cc-spotify",
        "cc-youtube",
        "cc-youtube-info",
        "cc-brandingrecommendations",
        "cc-websiteaudit",
        "cc-click",
        "cc-computer",
        "cc-trisight",
    ];

    public static readonly string[] NodeTools =
    [
        "cc-browser",
        "cc-fox-browser",
        "cc-brandingrecommendations",
        "cc-websiteaudit",
    ];

    public static readonly string[] DotNetTools =
    [
        "cc-click",
        "cc-computer",
        "cc-trisight",
    ];

    public static string[] GetToolsForProfile(InstallProfile profile)
    {
        if (profile == InstallProfile.Standard)
            return StandardTools;

        var all = new List<string>(StandardTools.Length + DeveloperOnlyTools.Length);
        all.AddRange(StandardTools);
        all.AddRange(DeveloperOnlyTools);
        return all.ToArray();
    }
}
