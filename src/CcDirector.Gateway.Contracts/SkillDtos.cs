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

/// <summary>A supporting file carried by a skill version, with full content (authoring payloads).</summary>
public sealed class SkillFileDto
{
    public string FileName { get; set; } = "";
    public string Content { get; set; } = "";
}

/// <summary>A supporting file in a version-detail response, with its content and hash. The detail
/// route is the authoring read; the raw <c>files/{fileName}</c> route serves the agent read.</summary>
public sealed class SkillFileInfoDto
{
    public string FileName { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string Content { get; set; } = "";
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

    /// <summary>Who is authoring: a session id, an agent name, or "human:&lt;user&gt;".</summary>
    public string? AuthoredBy { get; set; }

    public string? ChangeNote { get; set; }
}
