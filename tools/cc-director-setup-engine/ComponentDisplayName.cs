namespace CcDirector.Setup.Engine;

/// <summary>
/// The name a PERSON reads for an installed component, from the internal component id.
///
/// The ids are file-shaped - "cc-director", "cc-launcher" - and every screen that rendered an id
/// undid the naming rule the moment something went wrong: the cards said "Director" and "Launcher"
/// while a failure said "cc-launcher did not install". The product calls the application the Director
/// and the background app the Launcher; ids belong in logs and paths.
/// </summary>
public static class ComponentDisplayName
{
    /// <summary>The display name for a component id, or the id itself when it is not one we name.</summary>
    public static string For(string componentId) => componentId?.ToLowerInvariant() switch
    {
        "cc-director" or "director" => "Director",
        "cc-launcher" or "launcher" => "Launcher",
        "cc-gateway" or "gateway" or "devthrottle-gateway" => "Gateway",
        "cockpit" => "Cockpit",
        "cc-tools" or "tools" or "python-tools" => "Tools",
        null => "",
        _ => componentId,
    };

    /// <summary>Display names for a set of component ids, in order.</summary>
    public static IReadOnlyList<string> For(IEnumerable<string> componentIds) =>
        componentIds is null ? [] : componentIds.Select(For).ToList();
}
