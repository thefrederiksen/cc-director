using System.Text.Json.Serialization;

namespace CcDirector.Core.Instances;

/// <summary>
/// One named Director instance = a settings profile. Persisted in the cross-instance
/// registry (<see cref="NamedInstanceRegistry"/>) at the shared machine root.
///
/// Three independent name fields, deliberately NOT the same thing:
///   - <see cref="Id"/>          immutable identity (uuid); the per-director "install id".
///   - <see cref="Name"/>        the slug: the folder <c>instances\{slug}\</c> and the
///                               <c>--instance {slug}</c> CLI handle. Fixed at creation.
///                               The default instance's slug is ALWAYS <c>default</c>.
///   - <see cref="DisplayName"/> the editable label shown in the menu/picker/Fleet Map.
///                               Seeded from the hostname for the default instance.
/// </summary>
public sealed class NamedInstance
{
    /// <summary>Immutable identity (uuid). Never shown to the user.</summary>
    public string Id { get; init; } = "";

    /// <summary>The slug: folder name + CLI handle. Fixed at creation.</summary>
    public string Name { get; init; } = "";

    /// <summary>Editable label. This is the only name field the user renames.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>The gateway URL this instance binds to (stored for display in the picker).</summary>
    public string GatewayUrl { get; init; } = "";

    /// <summary>ISO-8601 creation timestamp.</summary>
    public string CreatedAt { get; init; } = "";

    /// <summary>True when this is the reserved default instance.</summary>
    [JsonIgnore]
    public bool IsDefault => string.Equals(Name, InstanceContext.DefaultSlug, StringComparison.OrdinalIgnoreCase);
}
