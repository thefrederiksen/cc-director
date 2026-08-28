// Turning a weather reading into a sentence somebody would actually say.
//
// The device writes this, not the model, for the same reason the timer sentences are written here:
// what the forecast actually says is known only after the call is made, and a model asked to narrate
// its own tool call will cheerfully invent a temperature.
//
// Everything in this file is pure, so the wording can be tested without the network.

export interface WeatherNow {
  readonly place: string;
  readonly temperature: number;
  readonly feelsLike: number | null;
  readonly code: number;
  readonly windSpeed: number | null;
  readonly high: number | null;
  readonly low: number | null;
  readonly chanceOfRain: number | null;
  readonly units: "celsius" | "fahrenheit";
}

// The World Meteorological Organization codes Open-Meteo reports, in the words a person uses. Said
// out loud, so "overcast" rather than "overcast clouds" and no numbers anybody has to decode.
const CONDITIONS = new Map<number, string>([
  [0, "clear"],
  [1, "mostly clear"],
  [2, "partly cloudy"],
  [3, "overcast"],
  [45, "foggy"],
  [48, "freezing fog"],
  [51, "drizzling lightly"],
  [53, "drizzling"],
  [55, "drizzling heavily"],
  [56, "freezing drizzle"],
  [57, "freezing drizzle"],
  [61, "raining lightly"],
  [63, "raining"],
  [65, "raining heavily"],
  [66, "freezing rain"],
  [67, "freezing rain"],
  [71, "snowing lightly"],
  [73, "snowing"],
  [75, "snowing heavily"],
  [77, "snowing"],
  [80, "showery"],
  [81, "showery"],
  [82, "heavy showers"],
  [85, "snow showers"],
  [86, "heavy snow showers"],
  [95, "thundery"],
  [96, "thunderstorms with hail"],
  [99, "thunderstorms with hail"],
]);

export function describeCode(code: number): string {
  return CONDITIONS.get(code) ?? "hard to describe";
}

/** Whether the condition reads as a state ("it is raining") or a description ("it is overcast"). */
function isHappening(code: number): boolean {
  return code >= 45;
}

function round(value: number): number {
  return Math.round(value);
}

function degrees(value: number, units: WeatherNow["units"]): string {
  return `${round(value)} degrees${units === "fahrenheit" ? " Fahrenheit" : ""}`;
}

/**
 * One or two sentences about the weather right now.
 *
 * Mentions what it feels like only when that differs from the actual temperature by enough to change
 * what you would wear, and the chance of rain only when it is worth knowing. A forecast that recites
 * every number it has is one nobody listens to.
 */
export function sayWeather(now: WeatherNow): string {
  const condition = describeCode(now.code);
  const opening = isHappening(now.code)
    ? `It is ${condition} in ${now.place} and ${degrees(now.temperature, now.units)}.`
    : `It is ${degrees(now.temperature, now.units)} and ${condition} in ${now.place}.`;

  const extras: string[] = [];

  if (now.feelsLike !== null && Math.abs(now.feelsLike - now.temperature) >= 3) {
    extras.push(`feels like ${degrees(now.feelsLike, now.units)}`);
  }
  if (now.high !== null && now.low !== null) {
    extras.push(`${round(now.high)} to ${round(now.low)} today`);
  }
  if (now.chanceOfRain !== null && now.chanceOfRain >= 30) {
    extras.push(`${round(now.chanceOfRain)} percent chance of rain`);
  }

  if (extras.length === 0) {
    return opening;
  }
  const tail = extras.length === 1 ? extras[0] : `${extras.slice(0, -1).join(", ")}, and ${extras[extras.length - 1]}`;
  return `${opening} ${capitalise(tail)}.`;
}

function capitalise(text: string): string {
  return text.length === 0 ? text : text[0].toUpperCase() + text.slice(1);
}

/** What to say when there is nowhere to get the weather for. */
export function sayNoLocation(): string {
  return "I do not know where you are. Set a home town in settings and I will remember it.";
}

/** What to say when a named place could not be found. */
export function sayPlaceNotFound(place: string): string {
  return `I could not find anywhere called ${place}.`;
}
