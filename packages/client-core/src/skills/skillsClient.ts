// The central skill library surface of the Gateway (devthrottle_internal issue 995): the typed,
// same-origin client the Cockpit's Skills page reads.
//
// A skill is a capability an agent reaches for mid-task. They are held on the Gateway and fetched by
// agents at the moment of use, instead of being copied onto every machine by the installer - so this
// is a read against the Gateway front door like every other client here; the page never reaches a
// Director.
//
// NOTE what this client deliberately does NOT expose to the register: a skill's BODY. The register
// listing is the same small shape every session's launch briefing is rendered from, and the body is
// fetched one skill at a time. The page reads a body only when someone opens the preview.
import { authHeaders, GatewayError } from "../api/client";

/** One skill in the register: what an agent chooses from, never the instructions themselves. */
export interface SkillDefinition {
  id: string;
  name: string;
  /** ONE line: what this skill does. This is the line that rides every session's briefing. */
  summary: string;
  /** The phrases that should bring this skill to mind. */
  triggers: string[];
  /** The published version number this projection reflects. */
  version?: number;
  /** True for the skills DevThrottle ships: read-only, never deletable, customize by cloning. */
  isBuiltIn?: boolean;
  /** True when an unpublished draft exists beside the published version. */
  hasDraft?: boolean;
  /** The canonical content hash of the published version. */
  contentHash?: string;
  /** How many supporting files the published version carries - a count, never the files. */
  fileCount?: number;
  /** When the skill head last changed (UTC, ISO). */
  updatedUtc?: string;
  /** The owner's switch: false = OFF (left out of every briefing, fetch refused, nothing deleted).
   *  Absent on an older Gateway, which means available. */
  enabled?: boolean;
  /** The Gateway's verdict on whether this skill's content can be changed by the caller (rendered
   *  verbatim, never derived here): false for built-ins, true for the tenant's own. Absent means
   *  not editable - when in doubt, offer no edit affordance. */
  editable?: boolean;
}

/** The response of creating a skill: the new draft's snapshot (subset this client reads). */
export interface SkillDraft {
  skillId: string;
  version: number;
  status: string;
}

/** One row of a skill's version history. */
export interface SkillVersionInfo {
  version: number;
  status: string;
  contentHash: string;
  authoredBy: string;
  changeNote: string | null;
  createdUtc: string;
  publishedUtc: string | null;
}

async function gatewayErrorFrom(res: Response, label: string): Promise<GatewayError> {
  let detail = `${res.status}`;
  try {
    const text = await res.text();
    if (text.length > 0) {
      try {
        const body = JSON.parse(text) as { error?: string; detail?: string };
        detail = body.error ?? body.detail ?? text;
      } catch {
        detail = text;
      }
    }
  } catch {
    /* body unreadable - keep the status code */
  }
  return new GatewayError(res.status, `${label} failed: ${detail}`);
}

// GET /gateway/skills - the register. Throws on a non-2xx or a transport failure so the page shows
// an error banner rather than an empty list that reads as "you have no skills".
export async function getSkills(signal?: AbortSignal): Promise<SkillDefinition[]> {
  const res = await fetch("/gateway/skills", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "GET /gateway/skills");
  const body = (await res.json()) as { skills?: SkillDefinition[] } | null;
  const skills = body?.skills;
  if (skills === undefined) throw new GatewayError(res.status, "GET /gateway/skills returned no skills field");
  return skills;
}

// GET /gateway/skills/{id} - one skill's published projection.
export async function getSkill(id: string, signal?: AbortSignal): Promise<SkillDefinition> {
  const res = await fetch(`/gateway/skills/${encodeURIComponent(id)}`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, `GET /gateway/skills/${id}`);
  return (await res.json()) as SkillDefinition;
}

// GET /gateway/skills/{id}/body - the instructions an agent fetches, raw markdown. The preview
// renders exactly this, so what the owner reads is what their agents read. Pass the version from the
// projection you already hold so the metadata and the body are guaranteed to be the SAME version -
// two unpinned fetches can straddle a publish and show a torn read.
export async function getSkillBody(id: string, version?: number, signal?: AbortSignal): Promise<string> {
  const versionQuery = typeof version === "number" ? `?version=${version}` : "";
  const res = await fetch(`/gateway/skills/${encodeURIComponent(id)}/body${versionQuery}`, {
    method: "GET",
    headers: { Accept: "text/markdown", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, `GET /gateway/skills/${id}/body`);
  return await res.text();
}

// GET /gateway/skills/{id}/versions - the version history, newest first, no bodies.
export async function getSkillVersions(id: string, signal?: AbortSignal): Promise<SkillVersionInfo[]> {
  const res = await fetch(`/gateway/skills/${encodeURIComponent(id)}/versions`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, `GET /gateway/skills/${id}/versions`);
  const body = (await res.json()) as { versions?: SkillVersionInfo[] } | null;
  return body?.versions ?? [];
}

// POST /gateway/skills - create a skill as a DRAFT (invisible to the register and to every briefing
// until it is published). The add dialog sends only an id, a name and the one-line summary;
// authoring the body is agent-driven by design, so the write surface here is deliberately thin.
export async function createSkill(
  input: { id: string; name: string; summary: string },
  signal?: AbortSignal,
): Promise<SkillDraft> {
  const res = await fetch("/gateway/skills", {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ ...input, authoredBy: "cockpit:add-dialog" }),
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "POST /gateway/skills");
  return (await res.json()) as SkillDraft;
}

// POST /gateway/skills/{id}/enable | /disable - the owner's switch. The Gateway REQUIRES the actor:
// a governance change is never anonymous.
export async function setSkillEnabled(
  id: string,
  enabled: boolean,
  by: string,
  signal?: AbortSignal,
): Promise<void> {
  const verb = enabled ? "enable" : "disable";
  const res = await fetch(
    `/gateway/skills/${encodeURIComponent(id)}/${verb}?by=${encodeURIComponent(by)}`,
    { method: "POST", headers: { Accept: "application/json", ...authHeaders() }, signal },
  );
  if (!res.ok) throw await gatewayErrorFrom(res, `POST /gateway/skills/${id}/${verb}`);
}

// POST /gateway/skills/{id}/clone - copy a skill's published content into a new tenant-owned, fully
// editable skill. This is the sanctioned way to customize a read-only built-in.
export async function cloneSkill(
  id: string,
  newId: string,
  by: string,
  signal?: AbortSignal,
): Promise<SkillDefinition> {
  const query = new URLSearchParams({ newId, by });
  const res = await fetch(`/gateway/skills/${encodeURIComponent(id)}/clone?${query.toString()}`, {
    method: "POST",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, `POST /gateway/skills/${id}/clone`);
  return (await res.json()) as SkillDefinition;
}

/** Suggest a slug id from a display name, for the add dialog. Mirrors the Gateway's id rules. */
export function suggestSkillId(name: string): string {
  return name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 64);
}
