// The soul document: who Wilson is, in the household's own words. Read on every turn by talk.js,
// edited from the settings screen. Every previous version is kept on disk by the store.

export default async function handler(request, response, wilson) {
  if (!wilson) {
    response.status(200).json({ soul: null, editable: false, reason: "This deployment has no soul document; the built-in prompt is used." });
    return;
  }
  const store = wilson.store;
  if (request.method === "GET") {
    response.status(200).json({ soul: store.soul(), editable: true });
    return;
  }
  if (request.method !== "PUT" && request.method !== "POST") {
    response.status(405).json({ error: "Read the soul with GET, write it with PUT." });
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
  const soul = payload && typeof payload.soul === "string" ? payload.soul : null;
  if (soul === null || soul.trim().length === 0) {
    response.status(400).json({ error: "A soul document cannot be empty." });
    return;
  }
  store.writeSoul(soul);
  store.log({ kind: "soul-edited", chars: soul.length });
  response.status(200).json({ soul: store.soul(), editable: true });
}
