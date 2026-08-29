// Wilson's own hearing. A short clip of speech in, its words out.
//
// The browser's recogniser only exists where the platform ships one (Google's on Android,
// Microsoft's on Windows); a Raspberry Pi has none. So the device records what was said after the
// wake word and this sends the clip to Groq's Whisper on the same key as the brain and the voice.
// whisper-large-v3-turbo runs at about 216 times real time and costs four cents an hour of audio.
//
// The clip comes as a WAV body (16 kHz, 16-bit mono from the page). Whisper takes a prompt of
// known spellings, which is how "Perry Sound" becomes "Parry Sound" at the source: the household's
// names and places, plus whatever the page passes (the wake word), go in.
//
// Web Request/Response signature: the body is raw audio, and this signature reads it as bytes
// without anything trying to parse it as JSON.

const GROQ_STT_URL = "https://api.groq.com/openai/v1/audio/transcriptions";
const MODEL = "whisper-large-v3-turbo";
/** Whisper's prompt is capped at 224 tokens; a comma list of names stays well inside at this length. */
const MAX_PROMPT_CHARS = 600;
const MIN_BYTES = 44 + 16000 * 2 * 0.3; // less than 0.3 s of audio is a click, not speech

/** The spelling hints for this household, as one Whisper prompt. */
export function hintsFor(words) {
  const seen = new Set();
  const list = [];
  for (const w of words) {
    const clean = String(w || "").trim();
    if (clean.length === 0 || seen.has(clean.toLowerCase())) {
      continue;
    }
    seen.add(clean.toLowerCase());
    list.push(clean);
  }
  let prompt = list.join(", ");
  if (prompt.length > MAX_PROMPT_CHARS) {
    prompt = prompt.slice(0, MAX_PROMPT_CHARS).replace(/,[^,]*$/, "");
  }
  return prompt;
}

export default async function handler(request, wilson) {
  if (request.method !== "POST") {
    return Response.json({ error: "Send the audio with POST." }, { status: 405 });
  }
  const key = process.env.GROQ_API_KEY;
  if (!key) {
    return Response.json({ error: "The assistant has no model key configured on the server." }, { status: 500 });
  }

  const audio = new Uint8Array(await request.arrayBuffer());
  if (audio.length < MIN_BYTES) {
    return Response.json({ error: "The clip was too short to hold speech." }, { status: 400 });
  }

  const url = new URL(request.url, "http://localhost");
  const fromPage = (url.searchParams.get("hints") || "").split(",");
  const fromStore = wilson
    ? [...wilson.store.people().map((p) => p.name), ...wilson.store.knownPlaceNames(), ...wilson.store.people().flatMap((p) => [p.profile.home, p.profile.currentLocation])]
    : [];
  const prompt = hintsFor([...fromPage, ...fromStore]);

  const form = new FormData();
  form.append("file", new Blob([audio], { type: "audio/wav" }), "clip.wav");
  form.append("model", MODEL);
  form.append("language", "en");
  form.append("response_format", "json");
  form.append("temperature", "0");
  if (prompt.length > 0) {
    form.append("prompt", prompt);
  }

  const startedAt = Date.now();
  let upstream;
  try {
    upstream = await fetch(GROQ_STT_URL, { method: "POST", headers: { Authorization: `Bearer ${key}` }, body: form });
  } catch (error) {
    console.log("HEAR UNREACHABLE " + String(error));
    return Response.json({ error: "The hearing service could not be reached." }, { status: 502 });
  }
  if (!upstream.ok) {
    const detail = (await upstream.text()).slice(0, 300);
    console.log("HEAR UPSTREAM FAILED " + upstream.status + " " + detail);
    if (wilson) {
      wilson.store.log({ kind: "hear", error: `whisper ${upstream.status}`, detail, bytes: audio.length });
    }
    return Response.json({ error: `The hearing service refused the clip (${upstream.status}).` }, { status: 502 });
  }
  const body = await upstream.json();
  const text = typeof body.text === "string" ? body.text.trim() : "";
  const elapsedMs = Date.now() - startedAt;
  const seconds = Number(((audio.length - 44) / (16000 * 2)).toFixed(2));
  console.log("HEAR " + JSON.stringify({ at: new Date().toISOString(), elapsedMs, seconds, text }));
  if (wilson) {
    wilson.store.log({ kind: "hear", text, seconds, elapsedMs, hints: prompt.length > 0 ? prompt : null });
  }
  return Response.json({ text, elapsedMs, seconds, model: MODEL });
}
