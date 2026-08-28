// The weather, from Open-Meteo.
//
// No key, no account, no signup. That is the whole reason it was the first of the three things to
// build: nothing about it needed a human to go and register somewhere.
//
// It returns numbers, never a sentence. The page turns them into words, because a sentence composed
// where the reading was taken cannot be wrong about what the reading said.

const FORECAST_URL = "https://api.open-meteo.com/v1/forecast";
const GEOCODE_URL = "https://geocoding-api.open-meteo.com/v1/search";

/** Turn a place name into coordinates. Returns null when there is no such place. */
async function findPlace(name) {
  const url = `${GEOCODE_URL}?name=${encodeURIComponent(name)}&count=1&language=en&format=json`;
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`The place lookup failed (${response.status}).`);
  }
  const body = await response.json();
  const first = Array.isArray(body.results) ? body.results[0] : null;
  if (!first) {
    return null;
  }
  return { latitude: first.latitude, longitude: first.longitude, place: first.name };
}

export default async function handler(request, response) {
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

  try {
    // A named place wins over coordinates: asking for the weather in London while standing in Toronto
    // means London.
    if (place.length > 0) {
      const found = await findPlace(place);
      if (found === null) {
        response.status(200).json({ notFound: true, place });
        return;
      }
      latitude = found.latitude;
      longitude = found.longitude;
      place = found.place;
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

    console.log("WEATHER " + JSON.stringify({ at: new Date().toISOString(), place: reading.place, code: reading.code }));
    response.status(200).json({ reading });
  } catch (error) {
    console.log("WEATHER ERROR " + String(error));
    response.status(502).json({ error: "I could not reach the weather service." });
  }
}
