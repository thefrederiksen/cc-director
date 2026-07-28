import { describe, it, expect, vi, afterEach } from "vitest";
import { getSkillBody, getSkills } from "./skillsClient";

// The Gateway serves the Cockpit at "/" and falls UNKNOWN page paths back to index.html, so a Gateway
// running a build from before the skill library answers /gateway/skills with HTTP 200, Content-Type
// text/html, and the app's own shell - never a 404. Every machine whose Gateway has not been upgraded
// is in exactly that state.
//
// The danger is not the failure, it is being BELIEVED: without asserting the content type, the Skills
// preview would render a web page as a skill's instructions and look like it worked. These tests hold
// that line, and the last one holds the other side of it - the guard must not cost the real thing.

const APP_SHELL =
  '<!doctype html><html><head><title>DevThrottle Cockpit</title></head><body><div id="root"></div></body></html>';

function respond(body: string, contentType: string, status = 200): Response {
  return new Response(body, { status, headers: { "Content-Type": contentType } });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the skills client against a Gateway that does not serve the library yet", () => {
  it("refuses the app shell instead of returning it as a skill body", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond(APP_SHELL, "text/html; charset=utf-8")));

    await expect(getSkillBody("move-session", 5)).rejects.toThrow(
      /does not serve the skill library yet/,
    );
  });

  it("names the cause in words a person can act on", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond(APP_SHELL, "text/html")));

    // Not "content type mismatch": the person reading this needs to know their Gateway is behind.
    await expect(getSkills()).rejects.toThrow(/Upgrade or redeploy the Gateway/);
  });

  it("refuses an unlabelled body rather than guessing it is JSON", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response("{}", { status: 200 })));

    await expect(getSkills()).rejects.toThrow(/does not serve the skill library yet/);
  });

  it("still returns a real register and a real body untouched", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        respond(JSON.stringify({ skills: [{ id: "move-session", name: "Move", summary: "s", triggers: [] }] }),
          "application/json"),
      ),
    );
    const skills = await getSkills();
    expect(skills.map((s) => s.id)).toEqual(["move-session"]);

    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond("# Move Session\n\nRelocate.", "text/markdown")));
    expect(await getSkillBody("move-session", 5)).toBe("# Move Session\n\nRelocate.");
  });
});
