// The voice. Text in, a stream of spoken audio out, starting within about a quarter of a second.
//
// Groq's Orpheus model, on the same key as the brain. Measured on 29 August: first audio bytes in
// 195-247 ms, a full sentence in about 700 ms, delivered as chunked WAV at 24 kHz, 16-bit mono.
// The page starts playing the first chunk while the rest is still being made, which is the whole
// point: what a reply feels like is set by the time to the first sound, not the time to the last.
//
// It runs here rather than in the page so the key never reaches a browser, same as talk.js.
//
// Orpheus takes at most 200 characters per request. Longer replies are cut at sentence ends and
// requested in parallel; the pieces are joined into ONE continuous stream in the right order, so the
// page sees a single WAV whatever the length. The header's data length is left as "unknown"
// (0xFFFFFFFF), which is what Groq itself sends and what a streaming reader expects.
//
// Written against the Web Request/Response signature so the audio streams through Vercel instead of
// being buffered to the end, which would throw away the latency the model is paid for.

const GROQ_TTS_URL = "https://api.groq.com/openai/v1/audio/speech";
const MODEL = "canopylabs/orpheus-v1-english";
const VOICES = ["autumn", "diana", "hannah", "austin", "daniel", "troy"];
const DEFAULT_VOICE = "austin";
const MAX_PIECE = 200;

const SAMPLE_RATE = 24000;
const CHANNELS = 1;
const BITS = 16;

/** A 44-byte WAV header for a stream of unknown length. */
function wavHeader() {
  const header = new ArrayBuffer(44);
  const view = new DataView(header);
  const ascii = (offset, text) => {
    for (let i = 0; i < text.length; i += 1) {
      view.setUint8(offset + i, text.charCodeAt(i));
    }
  };
  ascii(0, "RIFF");
  view.setUint32(4, 0xffffffff, true);
  ascii(8, "WAVE");
  ascii(12, "fmt ");
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, CHANNELS, true);
  view.setUint32(24, SAMPLE_RATE, true);
  view.setUint32(28, (SAMPLE_RATE * CHANNELS * BITS) / 8, true);
  view.setUint16(32, (CHANNELS * BITS) / 8, true);
  view.setUint16(34, BITS, true);
  ascii(36, "data");
  view.setUint32(40, 0xffffffff, true);
  return new Uint8Array(header);
}

/**
 * Cut text into pieces Orpheus will accept, at sentence ends where possible, then at commas, then at
 * spaces. Never inside a word.
 */
export function splitForSpeech(text, max = MAX_PIECE) {
  const pieces = [];
  let rest = text.replace(/\s+/g, " ").trim();
  while (rest.length > max) {
    let cut = -1;
    for (const pattern of [/[.!?]["')]?\s/g, /[,;:]\s/g, /\s/g]) {
      let match;
      while ((match = pattern.exec(rest)) !== null && match.index < max) {
        cut = match.index + match[0].length;
      }
      if (cut > 0) {
        break;
      }
    }
    if (cut <= 0) {
      cut = max;
    }
    pieces.push(rest.slice(0, cut).trim());
    rest = rest.slice(cut).trim();
  }
  if (rest.length > 0) {
    pieces.push(rest);
  }
  return pieces;
}

/** Strips the WAV header from Groq's stream, yielding only PCM bytes. */
function pcmOnly(stream) {
  let headerDone = false;
  let held = new Uint8Array(0);
  return new TransformStream({
    transform(chunk, controller) {
      if (headerDone) {
        controller.enqueue(chunk);
        return;
      }
      const joined = new Uint8Array(held.length + chunk.length);
      joined.set(held);
      joined.set(chunk, held.length);
      held = joined;
      const dataAt = findAscii(held, "data");
      if (dataAt < 0) {
        return;
      }
      const start = dataAt + 8;
      if (held.length < start) {
        return;
      }
      headerDone = true;
      controller.enqueue(held.slice(start));
      held = new Uint8Array(0);
    },
  });
}

function findAscii(bytes, text) {
  outer: for (let i = 0; i + text.length <= bytes.length; i += 1) {
    for (let j = 0; j < text.length; j += 1) {
      if (bytes[i + j] !== text.charCodeAt(j)) {
        continue outer;
      }
    }
    return i;
  }
  return -1;
}

async function requestPiece(key, text, voice) {
  const upstream = await fetch(GROQ_TTS_URL, {
    method: "POST",
    headers: { Authorization: `Bearer ${key}`, "Content-Type": "application/json" },
    body: JSON.stringify({ model: MODEL, input: text, voice, response_format: "wav" }),
  });
  if (!upstream.ok || upstream.body === null) {
    const detail = (await upstream.text()).slice(0, 300);
    console.log("SPEAK UPSTREAM FAILED " + upstream.status + " " + detail);
    throw new Error(`The voice refused the request (${upstream.status}).`);
  }
  return upstream.body.pipeThrough(pcmOnly());
}

export default async function handler(request) {
  if (request.method !== "POST") {
    return Response.json({ error: "Send the text to speak with POST." }, { status: 405 });
  }
  const key = process.env.GROQ_API_KEY;
  if (!key) {
    return Response.json({ error: "The assistant has no model key configured on the server." }, { status: 500 });
  }

  let payload;
  try {
    payload = await request.json();
  } catch {
    return Response.json({ error: "The body was not JSON." }, { status: 400 });
  }
  const text = payload && typeof payload.text === "string" ? payload.text.trim() : "";
  if (text.length === 0) {
    return Response.json({ error: "There was nothing to say." }, { status: 400 });
  }
  const voice = VOICES.includes(payload.voice) ? payload.voice : DEFAULT_VOICE;
  const pieces = splitForSpeech(text);
  const startedAt = Date.now();

  // All pieces are requested at once so the second is ready by the time the first has played.
  // They are drained in order, so the listener hears one uninterrupted sentence.
  let pieceStreams;
  try {
    pieceStreams = await Promise.all(pieces.map((piece) => requestPiece(key, piece, voice)));
  } catch (error) {
    return Response.json({ error: error instanceof Error ? error.message : "The voice could not be reached." }, { status: 502 });
  }

  const out = new ReadableStream({
    async start(controller) {
      controller.enqueue(wavHeader());
      let bytes = 0;
      try {
        for (const stream of pieceStreams) {
          const reader = stream.getReader();
          for (;;) {
            const { done, value } = await reader.read();
            if (done) {
              break;
            }
            bytes += value.length;
            controller.enqueue(value);
          }
        }
        console.log("SPEAK " + JSON.stringify({ at: new Date().toISOString(), voice, chars: text.length, pieces: pieces.length, bytes, elapsedMs: Date.now() - startedAt }));
        controller.close();
      } catch (error) {
        console.log("SPEAK STREAM ERROR " + String(error));
        controller.error(error);
      }
    },
  });

  return new Response(out, {
    status: 200,
    headers: { "Content-Type": "audio/wav", "Cache-Control": "no-store", "X-Voice": voice, "X-Pieces": String(pieces.length) },
  });
}
