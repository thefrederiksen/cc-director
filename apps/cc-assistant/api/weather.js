// The weather, from Open-Meteo.
//
// No key, no account, no signup. That is the whole reason it was the first of the three things to
// build: nothing about it needed a human to go and register somewhere.
//
// It returns numbers, never a sentence. The page turns them into words, because a sentence composed
// where the reading was taken cannot be wrong about what the reading said.

const FORECAST_URL = "https://api.open-meteo.com/v1/forecast";
const GEOCODE_URL = "https://geocoding-api.open-meteo.com/v1/search";

const GROQ_URL = "https://api.groq.com/openai/v1/chat/completions";
const SPELLING_MODEL = "qwen/qwen3.6-27b";

/** Every candidate the geocoder has for a name, with region and country so a choice can be made. */
async function geocode(name, count = 5) {
  const url = `${GEOCODE_URL}?name=${encodeURIComponent(name)}&count=${count}&language=en&format=json`;
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`The place lookup failed (${response.status}).`);
  }
  const body = await response.json();
  return (Array.isArray(body.results) ? body.results : []).map((r) => ({
    latitude: r.latitude,
    longitude: r.longitude,
    name: r.name,
    admin1: r.admin1 || "",
    country: r.country || "",
    // GeoNames feature codes: PPL* is a populated place; AIRP an airport, and so on.
    populated: typeof r.feature_code === "string" && r.feature_code.startsWith("PPL"),
  }));
}

/**
 * The town, not its airport. Asking for Parry Sound and being told the weather at Parry Sound Area
 * Municipal Airport is technically right and obviously wrong. Populated places come first; among
 * those, or failing those, the one nearest home.
 */
function pickPlace(candidates, homePoint) {
  if (candidates.length === 0) {
    return null;
  }
  const towns = candidates.filter((c) => c.populated);
  const pool = towns.length > 0 ? towns : candidates;
  if (!homePoint) {
    return pool[0];
  }
  return pool.slice().sort((a, b) => distanceKm(a, homePoint) - distanceKm(b, homePoint))[0];
}

function distanceKm(a, b) {
  const rad = Math.PI / 180;
  const dLat = (b.latitude - a.latitude) * rad;
  const dLon = (b.longitude - a.longitude) * rad;
  const s = Math.sin(dLat / 2) ** 2 + Math.cos(a.latitude * rad) * Math.cos(b.latitude * rad) * Math.sin(dLon / 2) ** 2;
  return 6371 * 2 * Math.asin(Math.sqrt(s));
}

/**
 * The recogniser wrote "Perry Sound"; the person said Parry Sound. The geocoder cannot bridge that,
 * a language model can. Given the misspelling and where the household lives, it returns the most
 * likely real place name, or null when it has no idea.
 */
/** Edit distance between two strings, lower-cased, letters only. */
export function editDistance(a, b) {
  const x = a.toLowerCase().replace(/[^a-z]/g, "");
  const y = b.toLowerCase().replace(/[^a-z]/g, "");
  const prev = new Array(y.length + 1);
  for (let j = 0; j <= y.length; j += 1) {
    prev[j] = j;
  }
  for (let i = 1; i <= x.length; i += 1) {
    let diagonal = prev[0];
    prev[0] = i;
    for (let j = 1; j <= y.length; j += 1) {
      const temp = prev[j];
      prev[j] = Math.min(prev[j] + 1, prev[j - 1] + 1, diagonal + (x[i - 1] === y[j - 1] ? 0 : 1));
      diagonal = temp;
    }
  }
  return prev[y.length];
}

/**
 * Of several possible real names for a misheard one, the one spelled most like what was heard.
 * "Perry Sound" is one letter from "Parry Sound" and eight from "Port Perry"; a language model
 * asked to choose picked Port Perry anyway, twice, because it was nearer the hint. Letters do not
 * have opinions. Ties go to the earlier candidate, which is the model's own first choice.
 */
export function closestSpelling(heard, names) {
  let best = null;
  let bestDistance = Infinity;
  for (const name of names) {
    const d = editDistance(heard, name.split(",")[0]);
    if (d < bestDistance) {
      best = name;
      bestDistance = d;
    }
  }
  return best;
}

async function correctSpelling(key, heard, nearHint, candidates) {
  if (!key) {
    return null;
  }
  const upstream = await fetch(GROQ_URL, {
    method: "POST",
    headers: { Authorization: `Bearer ${key}`, "Content-Type": "application/json" },
    body: JSON.stringify({
      model: SPELLING_MODEL,
      messages: [
        {
          role: "system",
          content:
            "A speech recogniser wrote down a place name and a map lookup found nothing by that spelling. List up to three real places it could have been, each as \"Name, Region, Country\", " +
            "most likely first. The recogniser hears SOUNDS, so list places that sound like what was heard (same syllables, same rhythm), including any from the household's own list that fit. " +
            "Return ONLY JSON: {\"places\": [\"...\", \"...\"]} or {\"places\": []} if you cannot tell.",
        },
        {
          role: "user",
          content:
            `Heard: "${heard}". Hint: the household is near ${nearHint || "unknown"}.` +
            (candidates.length > 0 ? ` Places this household talks about: ${candidates.join(", ")}.` : ""),
        },
      ],
      reasoning_effort: "none",
      response_format: { type: "json_object" },
      max_tokens: 120,
      temperature: 0,
    }),
  });
  if (!upstream.ok) {
    throw new Error(`The spelling check refused (${upstream.status}).`);
  }
  const body = await upstream.json();
  let names = [];
  try {
    const parsed = JSON.parse(body?.choices?.[0]?.message?.content ?? "{}");
    names = Array.isArray(parsed.places) ? parsed.places.filter((p) => typeof p === "string" && p.trim().length > 0).map((p) => p.trim()) : [];
  } catch {
    return null;
  }
  // The household's own places are always in the running, whatever the model listed.
  for (const c of candidates) {
    if (!names.some((n) => n.split(",")[0].trim().toLowerCase() === c.toLowerCase())) {
      names.push(c);
    }
  }
  return closestSpelling(heard, names);
}

/**
 * Turn a place name into coordinates. Returns null when there is no such place.
 *
 * Four steps, each only when the previous found nothing: a name this household has resolved before;
 * the geocoder as heard (picking the candidate nearest home when there are several); the geocoder
 * with the model's corrected spelling; nothing. What resolves is remembered, so the same misspelling
 * never fails twice.
 */
async function findPlace(name, { key, store, home, candidates = [] }) {
  const remembered = store ? store.knownPlace(name) : null;
  if (remembered) {
    return { latitude: remembered.latitude, longitude: remembered.longitude, place: remembered.name, via: "remembered" };
  }

  const homePoint = home ? pickPlace(await geocode(home), null) : null;
  const nearest = (candidates) => pickPlace(candidates, homePoint);

  let found = nearest(await geocode(name));
  let via = "geocoder";
  if (!found) {
    const homeLabel = homePoint ? [homePoint.name, homePoint.admin1, homePoint.country].filter(Boolean).join(", ") : home;
    const corrected = await correctSpelling(key, name, homeLabel, candidates);
    if (corrected && corrected.toLowerCase() !== name.toLowerCase()) {
      // "Parry Sound, Ontario, Canada": the geocoder wants just the name, the rest disambiguates.
      const justName = corrected.split(",")[0].trim();
      const candidates = await geocode(justName);
      const wanted = corrected.toLowerCase();
      found = candidates.find((c) => wanted.includes(c.admin1.toLowerCase()) && c.admin1.length > 0) || nearest(candidates);
      via = found ? `corrected from "${name}" to "${corrected}"` : "geocoder";
    }
  }
  if (!found) {
    return null;
  }
  if (store) {
    store.rememberPlace(name, { name: found.name, admin1: found.admin1, country: found.country, latitude: found.latitude, longitude: found.longitude });
  }
  return { latitude: found.latitude, longitude: found.longitude, place: found.name, via };
}

export default async function handler(request, response, wilson) {
  if (request.method !== "POST") {
    response.status(405).json({ error: "Ask for the weather with POST." });
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

  const units = payload.units === "fahrenheit" ? "fahrenheit" : "celsius";
  let latitude = Number(payload.latitude);
  let longitude = Number(payload.longitude);
  let place = typeof payload.place === "string" ? payload.place.trim() : "";
  const home = typeof payload.home === "string" ? payload.home.trim() : "";

  // With a store and a known person, where they ARE beats where they live, and both beat the page's
  // saved home town: "I'm in Parry Sound this week" said once is enough.
  const store = wilson ? wilson.store : null;
  const speaker = typeof payload.speaker === "string" && payload.speaker.trim().length > 0 ? payload.speaker.trim() : null;
  const person = store && speaker ? store.person(speaker) : null;
  const named = payload.named === true;
  let resolvedVia = null;
  if (!named && person) {
    const known = person.profile.currentLocation || person.profile.home;
    if (typeof known === "string" && known.trim().length > 0) {
      place = known.trim();
      resolvedVia = person.profile.currentLocation ? "current location from memory" : "home from memory";
    }
  }
  const homeHint = (person && person.profile.home) || home;
  // The household's own places are the likeliest answers to a misheard name.
  const candidates = [
    ...(store ? store.knownPlaceNames() : []),
    ...(person ? [person.profile.home, person.profile.currentLocation] : []),
    home,
  ].filter((p) => typeof p === "string" && p.trim().length > 0);

  try {
    // A named place wins over coordinates: asking for the weather in London while standing in Toronto
    // means London.
    if (place.length > 0) {
      const found = await findPlace(place, { key: process.env.GROQ_API_KEY, store, home: homeHint, candidates: [...new Set(candidates)] });
      if (found === null) {
        if (store) {
          store.log({ kind: "weather", place, notFound: true });
        }
        response.status(200).json({ notFound: true, place });
        return;
      }
      latitude = found.latitude;
      longitude = found.longitude;
      place = found.place;
      resolvedVia = resolvedVia ? `${resolvedVia}; ${found.via}` : found.via;
    }

    if (!Number.isFinite(latitude) || !Number.isFinite(longitude)) {
      response.status(200).json({ noLocation: true });
      return;
    }

    const url =
      `${FORECAST_URL}?latitude=${latitude}&longitude=${longitude}` +
      "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m" +
      "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
      `&forecast_days=1&timezone=auto&temperature_unit=${units}`;

    const forecast = await fetch(url);
    if (!forecast.ok) {
      throw new Error(`The forecast failed (${forecast.status}).`);
    }
    const body = await forecast.json();
    const current = body.current || {};
    const daily = body.daily || {};

    const reading = {
      // With coordinates and no name, the timezone is the only honest label we have for where this
      // is. "America/Toronto" becomes "Toronto".
      place: place.length > 0 ? place : String(body.timezone || "here").split("/").pop().replace(/_/g, " "),
      temperature: current.temperature_2m,
      feelsLike: current.apparent_temperature ?? null,
      code: current.weather_code ?? 0,
      windSpeed: current.wind_speed_10m ?? null,
      high: Array.isArray(daily.temperature_2m_max) ? daily.temperature_2m_max[0] ?? null : null,
      low: Array.isArray(daily.temperature_2m_min) ? daily.temperature_2m_min[0] ?? null : null,
      chanceOfRain: Array.isArray(daily.precipitation_probability_max)
        ? daily.precipitation_probability_max[0] ?? null
        : null,
      units,
    };

    if (typeof reading.temperature !== "number") {
      throw new Error("The forecast came back without a temperature in it.");
    }

    console.log("WEATHER " + JSON.stringify({ at: new Date().toISOString(), place: reading.place, code: reading.code, via: resolvedVia }));
    if (store) {
      store.log({ kind: "weather", asked: typeof payload.place === "string" ? payload.place : null, place: reading.place, via: resolvedVia, temperature: reading.temperature });
    }
    response.status(200).json({ reading, via: resolvedVia });
  } catch (error) {
    console.log("WEATHER ERROR " + String(error));
    response.status(502).json({ error: "I could not reach the weather service." });
  }
}
