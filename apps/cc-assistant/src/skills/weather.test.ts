import { describe, expect, it } from "vitest";
import { describeCode, sayNoLocation, sayPlaceNotFound, sayWeather, type WeatherNow } from "./weather";

function reading(over: Partial<WeatherNow> = {}): WeatherNow {
  return {
    place: "Toronto",
    temperature: 18,
    feelsLike: 18,
    code: 0,
    windSpeed: 10,
    high: null,
    low: null,
    chanceOfRain: null,
    units: "celsius",
    ...over,
  };
}

describe("describing conditions", () => {
  it("uses words a person would say", () => {
    expect(describeCode(0)).toBe("clear");
    expect(describeCode(3)).toBe("overcast");
    expect(describeCode(63)).toBe("raining");
    expect(describeCode(95)).toBe("thundery");
  });

  it("admits when it does not recognise a code rather than inventing one", () => {
    expect(describeCode(1234)).toBe("hard to describe");
  });
});

describe("saying the weather", () => {
  it("puts the temperature first when the sky is just a description", () => {
    expect(sayWeather(reading())).toBe("It is 18 degrees and clear in Toronto.");
  });

  it("puts the condition first when something is actually happening", () => {
    expect(sayWeather(reading({ code: 63, temperature: 9, feelsLike: 9 })))
      .toBe("It is raining in Toronto and 9 degrees.");
  });

  it("rounds, because nobody says nineteen point four degrees", () => {
    expect(sayWeather(reading({ temperature: 19.4 }))).toMatch(/19 degrees/);
  });

  // A forecast that recites every number it has is one nobody listens to.
  it("stays quiet about feels-like when it matches the temperature", () => {
    expect(sayWeather(reading({ feelsLike: 19 }))).not.toMatch(/feels like/);
  });

  it("mentions feels-like when it would change what you wear", () => {
    expect(sayWeather(reading({ temperature: 2, feelsLike: -6 }))).toMatch(/Feels like -6 degrees/);
  });

  it("stays quiet about a small chance of rain", () => {
    expect(sayWeather(reading({ chanceOfRain: 10 }))).not.toMatch(/chance of rain/);
  });

  it("mentions a real chance of rain", () => {
    expect(sayWeather(reading({ chanceOfRain: 70 }))).toMatch(/70 percent chance of rain/);
  });

  it("gives the day's range when it has one", () => {
    expect(sayWeather(reading({ high: 22, low: 11 }))).toMatch(/22 to 11 today/);
  });

  it("joins several extras readably", () => {
    const said = sayWeather(reading({ temperature: 1, feelsLike: -7, high: 4, low: -2, chanceOfRain: 60 }));
    expect(said).toBe("It is 1 degrees and clear in Toronto. Feels like -7 degrees, 4 to -2 today, and 60 percent chance of rain.");
  });

  it("names the unit when it is not celsius", () => {
    expect(sayWeather(reading({ units: "fahrenheit", temperature: 64 }))).toMatch(/64 degrees Fahrenheit/);
  });
});

describe("when it cannot answer", () => {
  it("asks for a home town rather than guessing", () => {
    expect(sayNoLocation()).toMatch(/home town/);
  });

  it("says which place it could not find", () => {
    expect(sayPlaceNotFound("Llanfairpwll")).toBe("I could not find anywhere called Llanfairpwll.");
  });
});
