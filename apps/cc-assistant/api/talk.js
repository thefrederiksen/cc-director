// The brain. One turn: text in, a spoken sentence out.
//
// It runs here rather than in the page so the model key never reaches a browser. The page has no
// credential of any kind, which is also why this app needs no sign-in.
//
// Groq because first-token latency is what a conversation feels, and it is the fastest thing
// available on this account.
//
// TWO MODELS, ONE ROUTE. The fast model answers everything it can on its own and decides which tool
// was meant. Measured on 29 August: qwen3.6-27b answered a kitchen question in about 200 ms with
// reasoning off, got the arithmetic right, and made the timer tool call eleven times out of eleven
// once the prompt spelled out that words are not a timer. qwen3.8-27b, newer and just as fast, said
// "I'm setting a timer called barbecue" in words on every try and set nothing. gpt-oss-120b on low reasoning
// was slower and, on a question that needed a moment's thought, spent the whole token budget thinking
// and returned an EMPTY answer - which the kitchen heard as "the model returned nothing to say".
//
// Anything live, recent, or that the fast model is unsure of goes through the look_up tool, which is
// answered by a second, slower call with Groq's built-in web search. Two to three seconds instead of
// two hundred milliseconds, but right instead of invented: without search, gpt-oss-20b stated a gold
// price two thousand dollars off as plain fact. A wrong answer said confidently is the failure this
// whole design exists to avoid, so the slow path is worth its wait and only paid when needed.
//
// There is deliberately no separate "router" call in front. A classifier hop would add a round trip
// to EVERY turn to save it on the few; the fast model routing by tool call costs nothing extra.

const GROQ_URL = "https://api.groq.com/openai/v1/chat/completions";
const FAST_MODEL = "qwen/qwen3.6-27b";
const SEARCH_MODEL = "openai/gpt-oss-120b";

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
  {
    type: "function",
    function: {
      name: "look_up",
      description:
        "Look something up on the web. Call this for anything happening now or recently (news, prices, sport results, who currently holds a job, what is open), anything that may have changed since your training, and any fact you are not sure of. Do not guess at these; look them up. Never call it for timers, the weather, or general knowledge you are sure of.",
      parameters: {
        type: "object",
        properties: {
          query: { type: "string", description: "What to search for, as a short search query." },
        },
        required: ["query"],
      },
    },
  },
];

// Search results come back with reference markers like [1+L12-L18] in the model's own bracket
// notation, which uses the CJK lenticular brackets U+3010 and U+3011. Spoken aloud they are noise,
// so they are removed before anything reaches a voice. Built from code points so this file stays ASCII.
const OPEN_BRACKET = String.fromCharCode(0x3010);
const CLOSE_BRACKET = String.fromCharCode(0x3011);
const CITATION_MARKER = new RegExp(OPEN_BRACKET + "[^" + CLOSE_BRACKET + "]*" + CLOSE_BRACKET, "g");

function cleanSpoken(text) {
  return text.replace(CITATION_MARKER, "").replace(/\s+([.,;:!?])/g, "$1").replace(/\s+/g, " ").trim();
}

// "none" is a Qwen setting; the gpt-oss family rejects it with a 400 and wants "low" at the least.
// So an ASSISTANT_MODEL override to gpt-oss keeps working, at the cost of a little thinking.
function reasoningEffortFor(model) {
  return model.startsWith("openai/gpt-oss") ? "low" : "none";
}

/** One call to Groq. Returns the parsed body, or throws with the upstream status and detail. */
async function complete(key, body) {
  const upstream = await fetch(GROQ_URL, {
    method: "POST",
    headers: { Authorization: `Bearer ${key}`, "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!upstream.ok) {
    const detail = await upstream.text();
    const error = new Error(`The model refused the request (${upstream.status}).`);
    error.status = upstream.status;
    error.detail = detail.slice(0, 400);
    throw error;
  }
  return upstream.json();
}

/**
 * The slow path: answer one question with live web search.
 *
 * The search model is told the original question AND the fast model's query, and is held to the same
 * one-sentence spoken rule. Reasoning is low because the search itself is where the time goes.
 */
async function lookUp(key, question, query, history) {
  const body = await complete(key, {
    model: process.env.ASSISTANT_SEARCH_MODEL || SEARCH_MODEL,
    messages: [
      { role: "system", content: SYSTEM_PROMPT + " You have web search. Use it, then answer from what you found, in one spoken sentence. Say when the information is from, if it matters." },
      ...history,
      { role: "user", content: `${question}\n\n(Search for: ${query})` },
    ],
    tools: [{ type: "browser_search" }],
    reasoning_effort: "low",
    max_tokens: 1200,
    temperature: 0.2,
  });
  const message = body?.choices?.[0]?.message ?? {};
  return cleanSpoken(typeof message.content === "string" ? message.content : "");
}

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
  "You have no way to start, stop or read a timer except by calling the tool. Words like 'I'll set a timer' or 'timer set' do nothing and are a lie. Call the tool instead, every time, even for a bare 'ten minute timer'.",
  "For anything live, recent, or that you are not certain of, call look_up rather than guessing. A confident wrong answer is worse than a short wait.",
  "Your knowledge has a cutoff and the world moved on. Who currently holds any office or job, prices, scores, news, what is open, the latest anything: ALWAYS look_up, never answer from memory.",
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

  const model = process.env.ASSISTANT_MODEL || FAST_MODEL;
  const startedAt = Date.now();

  try {
    // Reasoning is OFF for the fast model. This is a kitchen: a model that thinks for four seconds
    // about a timer has failed at the only thing that matters, and one that thinks its whole token
    // budget away answers with silence.
    const body = await complete(key, {
      model,
      messages,
      tools: TOOLS,
      tool_choice: "auto",
      reasoning_effort: reasoningEffortFor(model),
      max_tokens: 300,
      temperature: 0.3,
    });
    const message = body?.choices?.[0]?.message ?? {};

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

    // look_up is the one tool the SERVER runs, because the answer is words, not a device action.
    // It takes precedence over anything else in the same turn: a person who asked a question wants
    // the answer, and a timer mentioned in the same breath is rare enough to make them say it again.
    const lookup = actions.find((a) => a.name === "look_up");
    if (lookup) {
      const query = typeof lookup.args.query === "string" && lookup.args.query.trim().length > 0 ? lookup.args.query.trim() : text;
      const reply = await lookUp(key, text, query, messages.slice(1, -1));
      const elapsedMs = Date.now() - startedAt;
      if (reply.length === 0) {
        console.log("TALK LOOKUP EMPTY " + JSON.stringify({ text, query }));
        response.status(502).json({ error: "The search found nothing to say." });
        return;
      }
      console.log("TALK LOOKUP " + JSON.stringify({ at: new Date().toISOString(), elapsedMs, text, query, reply }));
      response.status(200).json({ reply, elapsedMs, model: SEARCH_MODEL, lookedUp: true });
      return;
    }

    // Any other tool call means the device has work to do and will say what happened itself.
    if (actions.length > 0) {
      const elapsedMs = Date.now() - startedAt;
      console.log("TALK ACTIONS " + JSON.stringify({ at: new Date().toISOString(), elapsedMs, text, actions }));
      response.status(200).json({ actions, elapsedMs, model });
      return;
    }

    // Some models return their working in `reasoning` and the answer in `content`. Only the answer
    // is ever spoken.
    const reply = cleanSpoken(typeof message.content === "string" ? message.content : "");
    const elapsedMs = Date.now() - startedAt;
    if (reply.length === 0) {
      console.log("TALK EMPTY REPLY " + JSON.stringify(body).slice(0, 400));
      response.status(502).json({ error: "The model returned nothing to say." });
      return;
    }

    console.log("TALK " + JSON.stringify({ at: new Date().toISOString(), elapsedMs, model, text, reply }));
    response.status(200).json({ reply, elapsedMs, model });
  } catch (error) {
    const status = typeof error?.status === "number" ? error.status : 0;
    console.log("TALK ERROR " + status + " " + String(error) + " " + (error?.detail ?? ""));
    if (status === 429) {
      // The Groq free tier is 8,000 tokens a minute, and a turn costs several hundred. Say so,
      // because "could not be reached" sends someone to check the network when the fix is a tier.
      response.status(502).json({ error: "The model is rate limited right now. Try again in a moment." });
      return;
    }
    response.status(502).json({ error: status > 0 ? error.message : "The model could not be reached." });
  }
}
