// Who is speaking. The page computes a voice embedding (a few hundred numbers that describe how a
// voice sounds, not what it said) and this decides whose it is, by comparing it with the samples
// each person recorded when they enrolled.
//
//   POST { action: "enrol", name, embedding, label }   store one sample for a person
//   POST { action: "identify", embedding }              -> { name, confidence } or { name: null }
//   POST { action: "clear", name }                      forget a person's voice
//
// Matching is cosine similarity against every stored sample, best sample wins; a person needs the
// best match to clear THRESHOLD and to beat the runner-up by MARGIN, or the answer is "unknown".
// Unknown is a fine answer: it just means no personalisation on this turn. A wrong name is worse.
//
// The embedding model lives in the page (src/assistant/speakerId.ts) because raw audio never
// leaves the device; only the embedding does, and only to this local service.

export const THRESHOLD = 0.62;
export const MARGIN = 0.05;

export function cosine(a, b) {
  let dot = 0;
  let na = 0;
  let nb = 0;
  for (let i = 0; i < a.length && i < b.length; i += 1) {
    dot += a[i] * b[i];
    na += a[i] * a[i];
    nb += b[i] * b[i];
  }
  if (na === 0 || nb === 0) {
    return 0;
  }
  return dot / (Math.sqrt(na) * Math.sqrt(nb));
}

/** Pure: given people with voice samples and one embedding, who is it? */
export function identify(people, embedding, threshold = THRESHOLD, margin = MARGIN) {
  const scores = people
    .map((person) => {
      const samples = person.voice && Array.isArray(person.voice.samples) ? person.voice.samples : [];
      const best = samples.reduce((top, s) => Math.max(top, cosine(s.embedding, embedding)), -1);
      return { name: person.name, key: person.key, best, samples: samples.length };
    })
    .filter((s) => s.samples > 0)
    .sort((x, y) => y.best - x.best);
  if (scores.length === 0) {
    return { name: null, confidence: 0, reason: "nobody has enrolled", scores };
  }
  const [top, second] = scores;
  if (top.best < threshold) {
    return { name: null, confidence: top.best, reason: `best match ${top.name} at ${top.best.toFixed(2)}, below ${threshold}`, scores };
  }
  if (second && top.best - second.best < margin) {
    return { name: null, confidence: top.best, reason: `${top.name} and ${second.name} too close (${top.best.toFixed(2)} vs ${second.best.toFixed(2)})`, scores };
  }
  return { name: top.name, key: top.key, confidence: top.best, reason: null, scores };
}

function validEmbedding(e) {
  return Array.isArray(e) && e.length >= 32 && e.length <= 4096 && e.every((n) => typeof n === "number" && Number.isFinite(n));
}

export default async function handler(request, response, wilson) {
  if (!wilson) {
    response.status(200).json({ name: null, reason: "This deployment keeps no voices." });
    return;
  }
  const store = wilson.store;
  if (request.method !== "POST") {
    response.status(405).json({ error: "POST an action." });
    return;
  }
  let payload = request.body;
  if (typeof payload === "string") {
    try {
      payload = JSON.parse(payload);
    } catch {
      response.status(400).json({ error: "The body was not JSON." });
      return;
    }
  }
  payload = payload || {};
  const name = typeof payload.name === "string" ? payload.name.trim() : "";

  if (payload.action === "clear") {
    if (name.length === 0) {
      response.status(400).json({ error: "Whose voice?" });
      return;
    }
    store.clearVoice(name);
    store.log({ kind: "voice-cleared", person: name });
    response.status(200).json({ cleared: true });
    return;
  }
  if (!validEmbedding(payload.embedding)) {
    response.status(400).json({ error: "The embedding was missing or malformed." });
    return;
  }
  if (payload.action === "enrol") {
    if (name.length === 0) {
      response.status(400).json({ error: "Whose voice?" });
      return;
    }
    store.person(name);
    const count = store.addVoiceSample(name, payload.embedding, typeof payload.label === "string" ? payload.label : "");
    store.log({ kind: "voice-enrolled", person: name, samples: count });
    response.status(200).json({ samples: count });
    return;
  }
  if (payload.action === "identify") {
    const result = identify(store.people(), payload.embedding);
    response.status(200).json({ name: result.name, confidence: result.confidence, reason: result.reason, scores: result.scores.map((s) => ({ name: s.name, score: Number(s.best.toFixed(3)) })) });
    return;
  }
  response.status(400).json({ error: "Unknown action." });
}
