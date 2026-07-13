namespace CcDirector.Gateway.Contracts;

// Gateway Cleanup mission, Phase 0 (Worker R1): the request and response shapes for the session READ verbs
// that were moved onto the tunnel command surface. These are ADDITIVE - they name the exact wire shapes the
// old REST lambdas already produced (as anonymous objects or from query-string arguments), so the tunnel
// verb and the re-pointed REST route serialize identical JSON. Kept in one new file so no shared Contracts
// file is edited.

/// <summary>
/// GET /sessions/{sid}/buffer request. Carries the three query-string arguments the old REST route took
/// (they have no home on <see cref="DirectorCommand"/>, so they ride in the command payload). All optional,
/// matching the route's <c>int? lines, bool? raw, long? since</c>.
/// </summary>
public sealed class BufferRequest
{
    /// <summary>When set (and positive) and not raw, keep only the last N cleaned lines.</summary>
    public int? Lines { get; set; }

    /// <summary>When true, return the raw bytes as text (no ANSI cleaning).</summary>
    public bool? Raw { get; set; }

    /// <summary>When set (and non-negative), return only the bytes written since this cursor.</summary>
    public long? Since { get; set; }
}

/// <summary>
/// GET /sessions/{sid}/buffer/html response - the styled HTML terminal snapshot, split into scrollback and
/// the live grid. Names the exact anonymous object the old REST route returned (including the concatenated
/// <see cref="Html"/> back-compat field).
/// </summary>
public sealed class BufferHtmlResponse
{
    public string SessionId { get; set; } = "";
    public long TotalBytes { get; set; }
    public string ScrollbackHtml { get; set; } = "";
    public string GridHtml { get; set; } = "";
    public int ScrollbackCount { get; set; }

    /// <summary>Back-compat: the scrollback and grid concatenated, for callers reading the whole stream.</summary>
    public string Html { get; set; } = "";
}

/// <summary>
/// GET /sessions/{sid}/github-urls response - the repo's GitHub "new issue" URL, resolved from its origin
/// remote. Names the anonymous <c>{ newIssueUrl }</c> the old REST route returned. A repo with no GitHub
/// origin is a Conflict (the route's 409), carried as the command result's error rather than in this body.
/// </summary>
public sealed class GithubUrlsResponse
{
    public string NewIssueUrl { get; set; } = "";
}

/// <summary>
/// GET /sessions/{sid}/wingman/explain response - the proactively-cached mobile briefing for a session.
/// Names the exact anonymous object the old REST route returned; every field is served from the session's
/// cached explain state with no LLM call, so a phone renders it instantly.
/// </summary>
public sealed class WingmanExplainResponse
{
    public bool MobileMode { get; set; }
    public string? Text { get; set; }
    public System.DateTime? At { get; set; }
    public string? Model { get; set; }
    public IReadOnlyList<string> QuickReplies { get; set; } = System.Array.Empty<string>();
    public string? Headline { get; set; }
    public string? WhatHappened { get; set; }
    public string? LongDescription { get; set; }
    public string? WhatClaudeWants { get; set; }
    public string? ClaudeVerbatim { get; set; }
    public string? Say { get; set; }
}

/// <summary>
/// GET /sessions/{sid}/handover-context request. Carries the optional <c>extraContext</c> query argument the
/// old REST route took (it has no home on <see cref="DirectorCommand"/>, so it rides in the command payload).
/// Gateway Cleanup mission: the cross-Director handover reads this over the tunnel before spawning the target.
/// </summary>
public sealed class HandoverContextRequest
{
    public string? ExtraContext { get; set; }
}

/// <summary>
/// GET /sessions/{sid}/handover-context response - the plain-text handover prompt the old REST route returned
/// as <c>text/plain</c>. Wrapped in a typed body (like <c>buffer-html</c>) so the tunnel verb serializes valid
/// JSON; the re-pointed REST route unwraps it back to <c>Results.Text</c>, byte-identical.
/// </summary>
public sealed class HandoverContextResponse
{
    public string Text { get; set; } = "";
}
