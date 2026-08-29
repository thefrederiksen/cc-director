// What Wilson brings to a turn, and what it takes away from one.
//
// BEFORE a turn: the person's profile and their remembered facts become a short block in the system
// prompt, so "what's the weather" knows they are in Parry Sound this week without being told again.
//
// AFTER a turn: the fast model reads what was said and answers one question - is there anything here
// worth keeping about this person? - and returns it as data. It runs after the reply has gone out,
// so it costs the person nothing to wait for. It is given what is already known, so it does not
// return the same fact every day.
//
// The model only ever proposes; the store decides (dedupes, caps). Nothing here composes speech.

const GROQ_URL = "https://api.groq.com/openai/v1/chat/completions";
const EXTRACT_MODEL = "qwen/qwen3.6-27b";

/** Profile fields Wilson understands. Anything else the model returns is kept too, but these have meaning. */
export const PROFILE_FIELDS = {
  callMe: "what to call them",
  home: "home town or city",
  currentLocation: "where they are right now, if away from home",
  units: "celsius or fahrenheit",
  household: "who else lives in the house",
  language: "language they prefer",
};

/** The block that goes into the system prompt. Empty when nothing is known. */
export function contextFor(person, knownPlaces = []) {
  if (!person) {
    return "";
  }
  const lines = [];
  lines.push(`You are talking to ${person.profile.callMe || person.name}.`);
  const profileLines = Object.entries(person.profile)
    .filter(([field, value]) => field !== "callMe" && value !== "" && value !== null && value !== undefined)
    .map(([field, value]) => `${PROFILE_FIELDS[field] || field}: ${value}`);
  if (profileLines.length > 0) {
    lines.push("About them: " + profileLines.join("; ") + ".");
  }
  const facts = (person.facts || []).slice(-40);
  if (facts.length > 0) {
    lines.push("Things they have told you before, oldest first: " + facts.map((f) => f.text).join(" | "));
  }
  if (knownPlaces.length > 0) {
    lines.push("Place names this household uses, spelled correctly: " + knownPlaces.join(", ") + ".");
  }
  lines.push("Use what you know without announcing that you remember it. If they tell you something new about themselves, just take it in; do not say you will remember it.");
  return lines.join(" ");
}

/** The interview, for when a person asks Wilson to get to know them. One question at a time. */
export const INTERVIEW_PROMPT =
  "If they ask you to get to know them, interview them, or set up, ask these ONE AT A TIME, waiting for each answer, skipping any you already know: " +
  "what to call them; where home is; whether they are somewhere else right now; Celsius or Fahrenheit; who else lives in the house; anything they want you to know. " +
  "Then say in one sentence what you have got. Never ask two questions in one breath.";

/**
 * After the reply is out: anything worth keeping?
 * Returns { facts: string[], profile: object } or null when there was nothing.
 */
export async function extractAfterTurn(key, person, heard, reply) {
  const known = {
    profile: person.profile,
    recentFacts: (person.facts || []).slice(-20).map((f) => f.text),
  };
  const body = {
    model: EXTRACT_MODEL,
    messages: [
      {
        role: "system",
        content:
          "You maintain a house assistant's memory about one person. You are given what is already known, then one exchange. " +
          "Return ONLY JSON: {\"facts\": [..], \"profile\": {..}}. " +
          "facts: new, durable, specific things about the person worth knowing next month, as short third-person sentences (\"Her mother's birthday is March 3rd\"). Not questions, not weather, not timers, not things already known, not the assistant's own words. Usually empty. " +
          `profile: only fields that changed, from: ${Object.keys(PROFILE_FIELDS).join(", ")}. currentLocation is where they say they ARE (a trip, a cottage); home is where they LIVE. Both hold a PLACE NAME ONLY, as on a map: from \"I am up at the cottage in Parry Sound until Friday\" the currentLocation is \"Parry Sound\" and the fact is \"At the cottage until Friday\". Use an empty string to clear currentLocation when they say they are home. ` +
          "If nothing is worth keeping, return {\"facts\": [], \"profile\": {}}.",
      },
      { role: "user", content: `Known: ${JSON.stringify(known)}\n\nPerson said: ${heard}\nAssistant replied: ${reply}` },
    ],
    reasoning_effort: "none",
    response_format: { type: "json_object" },
    max_tokens: 300,
    temperature: 0,
  };
  const upstream = await fetch(GROQ_URL, {
    method: "POST",
    headers: { Authorization: `Bearer ${key}`, "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!upstream.ok) {
    throw new Error(`Memory extraction refused (${upstream.status}).`);
  }
  const result = await upstream.json();
  const content = result?.choices?.[0]?.message?.content;
  if (typeof content !== "string") {
    return null;
  }
  let parsed;
  try {
    parsed = JSON.parse(content);
  } catch {
    throw new Error("Memory extraction returned something that was not JSON: " + content.slice(0, 120));
  }
  const facts = Array.isArray(parsed.facts) ? parsed.facts.filter((f) => typeof f === "string") : [];
  const profile = parsed.profile && typeof parsed.profile === "object" ? parsed.profile : {};
  if (facts.length === 0 && Object.keys(profile).length === 0) {
    return null;
  }
  return { facts, profile };
}
