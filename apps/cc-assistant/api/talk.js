// The brain. One turn: text in, a spoken sentence out.
//
// It runs here rather than in the page so the model key never reaches a browser. The page has no
// credential of any kind, which is also why this app needs no sign-in.
//
// Groq because first-token latency is what a conversation feels, and it is the fastest thing
// available on this account. Reasoning effort is held low on purpose: this is a kitchen, not a
// research assistant, and a model that thinks for four seconds about a timer has failed at the only
// thing that matters here.

const GROQ_URL = "https://api.groq.com/openai/v1/chat/completions";

// The tools. The model decides WHAT was asked for; the device decides what happened and says so.
// Nothing here returns a result to the model, because the model never composes the spoken sentence -
// it was told a timer was set once when none was, and that is a mistake worth designing out.
const TOOLS = [
  {
    type: "function",
    function: {
      name: "start_timer",
      description: "Start a countdown timer. ONLY call this when the person actually said how long. If no length was given, do not call it - ask how long instead.",
      parameters: {
        type: "object",
        properties: {
          seconds: { type: "integer", description: "Total length in seconds." },
          name: { type: "string", description: "What the timer is for, taken from what they said, one or two words, e.g. pasta or barbecue. Drop the word timer from it. Omit only if they gave no name at all." },
        },
        required: ["seconds"],
      },
    },
  },
  {
    type: "function",
    function: {
      name: "stop_timer",
      description: "Stop one timer. Give the name the person used, or omit it if they just said the timer.",
      parameters: { type: "object", properties: { name: { type: "string" } } },
    },
  },
  {
    type: "function",
    function: {
      name: "stop_all_timers",
      description: "Stop every running timer.",
      parameters: { type: "object", properties: {} },
    },
  },
  {
    type: "function",
    function: {
      name: "get_weather",
      description: "The weather now. Call this for any question about weather, temperature, rain, or what to wear.",
      parameters: {
        type: "object",
        properties: {
          place: {
            type: "string",
            description: "A town or city, only if they named one. Omit it for where they are.",
          },
        },
      },
    },
  },
  {
    type: "function",
    function: {
      name: "list_timers",
      description: "Say what timers are running and how long is left on each.",
      parameters: { type: "object", properties: {} },
    },
  },
];

/** The timers currently running, described for the model so it can resolve "the pasta one". */
function describeTimers(timers) {
  if (!Array.isArray(timers) || timers.length === 0) {
    return "No timers are running.";
  }
  const lines = timers.map((t) => {
    const name = typeof t.name === "string" && t.name.length > 0 ? t.name : "unnamed";
    return t.ringing ? `${name} (going off now)` : `${name} (${t.remainingSeconds} seconds left)`;
  });
  return "Timers running right now: " + lines.join("; ") + ".";
}

// The prompt is short and blunt on purpose. Two failures matter here and everything else is taste.
//
// LENGTH: this is spoken aloud in a kitchen. A paragraph that reads fine takes twenty seconds to say
// and nobody waits for it.
//
// LYING: asked to set a timer on 28 August it answered "Timer set for ten minutes." It cannot set a
// timer. A confident false confirmation is worse than any refusal, because it is trusted and the food
// burns. So the things it cannot do are listed explicitly rather than left to its judgement.
const CANNOT_DO = [
  "play music or control speakers",
  "control lights, heating or any other device",
  "check the news or anything else live",
  "read or change a calendar, list, message or email",
  "remember anything after this conversation ends",
];

const SYSTEM_PROMPT = [
  "You are a voice assistant in someone's kitchen. Everything you say is spoken out loud.",
  "ANSWER IN ONE SENTENCE, under twenty-five words. Use more only if asked to explain something.",
  "Give the answer and stop. Never restate the question, never explain how you worked it out,",
  "never say what you are about to do, and never offer further help or ask if there is anything else.",
  "Never use lists, headings, markdown or emoji. Say numbers the way a person says them aloud.",
  "YOU CANNOT DO ANY OF THE FOLLOWING: " + CANNOT_DO.join("; ") + ".",
  "For anything about timers, or about the weather, CALL A TOOL. Do not answer in words and never invent a temperature.",
  "If you are asked for one of the things you cannot do, say plainly in one short sentence that you cannot do it yet.",
  "NEVER claim to have done something you cannot do. Saying a timer is set when it is not is the",
  "worst mistake you can make.",
].join(" ");

export default async function handler(request, response) {
  if (request.method !== "POST") {
    response.status(405).json({ error: "Send the turn with POST." });
    return;
  }

  const key = process.env.GROQ_API_KEY;
  if (!key) {
    // Loud and specific. A missing key is a setup problem with an exact fix, not something to paper
    // over with a canned reply that would look like the assistant being stupid.
    response.status(500).json({ error: "The assistant has no model key configured on the server." });
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

  const text = payload && typeof payload.text === "string" ? payload.text.trim() : "";
  if (text.length === 0) {
    response.status(400).json({ error: "There was nothing to answer." });
    return;
  }

  // A short rolling history so "what about tomorrow" means something. Capped because a kitchen
  // conversation does not need a transcript of the whole week, and every turn pays for the tokens.
  const history = Array.isArray(payload.history) ? payload.history.slice(-8) : [];
  const messages = [
    { role: "system", content: SYSTEM_PROMPT },
    ...history
      .filter((m) => m && (m.role === "user" || m.role === "assistant") && typeof m.content === "string")
      .map((m) => ({ role: m.role, content: m.content })),
    { role: "user", content: text },
  ];

  const timerState = describeTimers(payload.timers);
  messages[0] = { role: "system", content: SYSTEM_PROMPT + " " + timerState };

  const model = process.env.ASSISTANT_MODEL || "openai/gpt-oss-120b";
  const startedAt = Date.now();

  try {
    const upstream = await fetch(GROQ_URL, {
      method: "POST",
      headers: { Authorization: `Bearer ${key}`, "Content-Type": "application/json" },
      body: JSON.stringify({ model, messages, tools: TOOLS, tool_choice: "auto", reasoning_effort: "low", max_tokens: 200, temperature: 0.3 }),
    });

    if (!upstream.ok) {
      const detail = await upstream.text();
      console.log("TALK UPSTREAM FAILED " + upstream.status + " " + detail.slice(0, 400));
      response.status(502).json({ error: `The model refused the request (${upstream.status}).` });
      return;
    }

    const body = await upstream.json();
    const message = body?.choices?.[0]?.message ?? {};
    const elapsedMs = Date.now() - startedAt;

    // A tool call means the device has work to do and will say what happened itself.
    const rawCalls = Array.isArray(message.tool_calls) ? message.tool_calls : [];
    const actions = [];
    for (const call of rawCalls) {
      const name = call?.function?.name;
      if (typeof name !== "string") {
        continue;
      }
      let args = {};
      try {
        args = call.function.arguments ? JSON.parse(call.function.arguments) : {};
      } catch {
        args = {};
      }
      actions.push({ name, args });
    }

    if (actions.length > 0) {
      console.log("TALK ACTIONS " + JSON.stringify({ at: new Date().toISOString(), elapsedMs, text, actions }));
      response.status(200).json({ actions, elapsedMs, model });
      return;
    }

    // gpt-oss models return their working in `reasoning` and the answer in `content`. Only the
    // answer is ever spoken.
    const reply = typeof message.content === "string" ? message.content.trim() : "";
    if (reply.length === 0) {
      console.log("TALK EMPTY REPLY " + JSON.stringify(body).slice(0, 400));
      response.status(502).json({ error: "The model returned nothing to say." });
      return;
    }

    console.log("TALK " + JSON.stringify({ at: new Date().toISOString(), elapsedMs, model, text, reply }));
    response.status(200).json({ reply, elapsedMs, model });
  } catch (error) {
    console.log("TALK ERROR " + String(error));
    response.status(502).json({ error: "The model could not be reached." });
  }
}
