// The page's half of the turn log.
//
// The server knows what was heard and what the model decided (talk.js logs that). Only the page
// knows what was actually said aloud, how long the voice took to make a sound, what the wake word
// matched, and what was thrown away as echo. It reports those here, tagged with the turn id talk.js
// handed out, and a reader joins the two by id.
//
// GET returns the recent log for the debug screen. DELETE clears it: the household owns its log.

export default async function handler(request, response, wilson) {
  if (!wilson) {
    response.status(200).json({ logged: false, reason: "This deployment keeps no log." });
    return;
  }
  const store = wilson.store;

  if (request.method === "GET") {
    const limit = Math.min(500, Math.max(1, Number(request.query?.limit) || 100));
    response.status(200).json({ turns: store.recentTurns(limit), directory: store.directory });
    return;
  }
  if (request.method === "DELETE") {
    store.clearTurns();
    response.status(200).json({ cleared: true });
    return;
  }
  if (request.method !== "POST") {
    response.status(405).json({ error: "Report a turn with POST." });
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
  const kind = typeof payload.kind === "string" ? payload.kind : "spoken";
  const record = { kind, ...payload };
  delete record.at;
  store.log(record);
  response.status(200).json({ logged: true });
}
