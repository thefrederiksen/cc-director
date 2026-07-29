namespace CcDirector.Gateway.Contracts;

/// <summary>
/// A skill on the wire - the REGISTER LISTING shape, and deliberately small. Every session's launch
/// briefing is rendered from this listing, so what belongs here is exactly what an agent needs to
/// decide whether to reach for the skill: its id, its name, ONE line of what it does, and the phrases
/// that should make an agent think of it. The body and the supporting files are NOT here; they are
/// fetched, per skill, at the moment the skill is used (devthrottle_internal issue 995).
///
/// That split is the whole feature. Discovery costs the listing; only a skill actually used costs its
/// body. Anything added to this shape is paid for by every session on every machine, so additions are
/// weighed against that and nothing bulky is ever allowed in.
/// </summary>
public sealed class SkillDto
{
    /// <summary>The skill's slug id, e.g. "move-session".</summary>
    public string Id { get; set; } = "";

    /// <summary>The display name shown in the register.</summary>
    public string Name { get; set; } = "";

    /// <summary>ONE line: what this skill does. This is the line an agent sees in its briefing.</summary>
    public string Summary { get; set; } = "";

    /// <summary>The phrases that should bring this skill to mind, e.g. "move session", "migrate
    /// session". Short and few - they ride the register, not the body.</summary>
    public List<string> Triggers { get; set; } = new();

    /// <summary>The published version number this projection reflects.</summary>
    public int Version { get; set; }

    /// <summary>True for the skills DevThrottle ships. Built-ins are read-only and can never be
    /// deleted; the way to customize one is to clone it.</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>When the skill head last changed (UTC).</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>True when an unpublished draft version exists beside the published one.</summary>
    public bool HasDraft { get; set; }

    /// <summary>The canonical content hash of the published version. A client that already holds a
    /// skill compares this to know whether what it holds is current, without fetching the body.</summary>
    public string ContentHash { get; set; } = "";

    /// <summary>How many supporting files the published version carries. A count, not the files - the
    /// listing never carries content.</summary>
    public int FileCount { get; set; }

    /// <summary>The owner's switch: false = OFF - left out of every agent's briefing and the fetch
    /// refused; nothing deleted. The register still LISTS off skills so they can be switched back on.
    /// Defaults true so a reader of an older Gateway treats everything as available. On a built-in
    /// this is the CALLING TENANT's effective state.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The Gateway's verdict on whether the caller may change this skill's content: false for
    /// the built-in DevThrottle skills (read-only - customize by cloning), true for the tenant's own.
    /// Clients render this verbatim and never derive editability themselves. Defaults false: when in
    /// doubt, offer no edit affordance.</summary>
    public bool Editable { get; set; }
}

/// <summary>
/// A supporting file carried by a skill version, with full content (authoring payloads).
///
/// A skill is a DIRECTORY in the Agent Skills standard, so <see cref="FileName"/> is a RELATIVE PATH
/// inside that directory - "references/tracing.md", "scripts/build.sh" - not a bare name. That is what
/// lets the library hold a real skill rather than a flattened approximation of one.
/// </summary>
public sealed class SkillFileDto
{
    /// <summary>The file's path relative to the skill's own directory, always with forward slashes.</summary>
    public string FileName { get; set; } = "";

    /// <summary>The file's content: the text itself when <see cref="Encoding"/> is "utf8", or the
    /// base64 of the file's bytes when it is "base64".</summary>
    public string Content { get; set; } = "";

    /// <summary>How <see cref="Content"/> carries the file: "utf8" (the default, and what an older
    /// client that omits this field always meant) or "base64" for a binary file - an image, an
    /// archive, a compiled program. Size limits are applied to the DECODED bytes either way.</summary>
    public string Encoding { get; set; } = "utf8";

    /// <summary>Whether this file gets the executable bit when written to disk. Honored on Linux and
    /// macOS and ignored on Windows. A bundled script that a skill tells an agent to run is useless
    /// without it, and the bit is part of the file's identity, so it is hashed with the content.</summary>
    public bool Executable { get; set; }
}

/// <summary>A supporting file in a version-detail response, with its content and hash. The detail
/// route is the authoring read; the raw <c>files/{fileName}</c> route serves the agent read.</summary>
public sealed class SkillFileInfoDto
{
    public string FileName { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string Content { get; set; } = "";

    /// <summary>"utf8" or "base64" - see <see cref="SkillFileDto.Encoding"/>.</summary>
    public string Encoding { get; set; } = "utf8";

    /// <summary>Whether the file gets the executable bit - see <see cref="SkillFileDto.Executable"/>.</summary>
    public bool Executable { get; set; }
}

/// <summary>One row of a skill's version history (no content bodies).</summary>
public sealed class SkillVersionInfoDto
{
    public int Version { get; set; }
    public string Status { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string AuthoredBy { get; set; } = "";
    public string? ChangeNote { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? PublishedUtc { get; set; }
}

/// <summary>The complete content snapshot of one skill version - the authoring read, and the shape
/// the command line pulls a whole skill directory from.</summary>
public sealed class SkillVersionDetailDto
{
    public string SkillId { get; set; } = "";
    public int Version { get; set; }
    public string Status { get; set; } = "";
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Triggers { get; set; } = new();
    public string BodyMarkdown { get; set; } = "";
    public List<SkillFileInfoDto> Files { get; set; } = new();

    // ---- the Agent Skills standard's optional frontmatter ----------------------------------------
    // Held verbatim so a skill authored in any other tool survives a round trip through this library
    // unchanged, and so SKILL.md can be written back out exactly as its author wrote it.

    /// <summary>The standard's <c>license</c>: a licence name, or the name of a bundled licence file.</summary>
    public string? License { get; set; }

    /// <summary>The standard's <c>compatibility</c>: environment requirements, if the skill has any.</summary>
    public string? Compatibility { get; set; }

    /// <summary>The standard's <c>allowed-tools</c>: a space-separated list of pre-approved tools.
    /// Marked experimental by the specification, and support varies between agents.</summary>
    public string? AllowedTools { get; set; }

    /// <summary>The standard's <c>metadata</c>: an arbitrary string map for properties the standard
    /// does not define (author, version, and whatever else a tool chose to record).</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    public string ContentHash { get; set; } = "";
    public string AuthoredBy { get; set; } = "";
    public string? ChangeNote { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? PublishedUtc { get; set; }
}

/// <summary>
/// Body of <c>POST /gateway/skills</c> (create a skill as a draft) and
/// <c>PUT /gateway/skills/{id}/draft</c> (replace the draft's content wholesale). The draft is a FULL
/// REPLACEMENT on every write - the command line round-trips a complete directory, so there is no
/// partial patch to reason about. A draft may be skeletal; publishing enforces the strict rules (a
/// summary an agent can choose from, and a body that actually says how to do the thing).
/// </summary>
public sealed class SkillContentRequest
{
    /// <summary>Required on create (the new skill's slug id); ignored on draft update (the route wins).</summary>
    public string? Id { get; set; }

    public string? Name { get; set; }
    public string? Summary { get; set; }
    public List<string>? Triggers { get; set; }
    public string? BodyMarkdown { get; set; }
    public List<SkillFileDto>? Files { get; set; }

    /// <summary>The standard's <c>license</c> frontmatter field.</summary>
    public string? License { get; set; }

    /// <summary>The standard's <c>compatibility</c> frontmatter field.</summary>
    public string? Compatibility { get; set; }

    /// <summary>The standard's <c>allowed-tools</c> frontmatter field.</summary>
    public string? AllowedTools { get; set; }

    /// <summary>The standard's <c>metadata</c> frontmatter map.</summary>
    public Dictionary<string, string>? Metadata { get; set; }

    /// <summary>Who is authoring: a session id, an agent name, or "human:&lt;user&gt;".</summary>
    public string? AuthoredBy { get; set; }

    public string? ChangeNote { get; set; }
}
