// What Wilson keeps. The household, its people, what it has learned, its soul, and every turn.
//
// Plain files in one directory, so a person can open them and see exactly what Wilson knows and
// delete any of it. That is the privacy model for the local version: nothing is hidden and nothing
// leaves the machine. When Wilson becomes a hosted service this becomes a database behind the same
// functions; the shapes below are the contract.
//
//   household.json   people, their profiles and remembered facts, resolved place names
//   soul.md          who Wilson is, in the household's own words
//   turns.jsonl      one line per event, appended, never rewritten
//
// The directory is WILSON_DATA_DIR, or %LOCALAPPDATA%\wilson on Windows and ~/.wilson elsewhere.

import fs from "node:fs";
import path from "node:path";
import os from "node:os";

const DEFAULT_SOUL = `# Wilson

You are Wilson, the assistant in this house. You are calm, quick and plain-spoken. You answer in one
short sentence unless asked to explain. You are voice only: there is no screen, so you never give a
link or anything that has to be read; you say what to search for instead. You use the name a person
asked to be called. You do not perform enthusiasm and you never apologise twice.
`;

const MAX_FACTS_PER_PERSON = 200;

export function dataDirectory() {
  if (process.env.WILSON_DATA_DIR) {
    return process.env.WILSON_DATA_DIR;
  }
  if (process.platform === "win32" && process.env.LOCALAPPDATA) {
    return path.join(process.env.LOCALAPPDATA, "wilson");
  }
  return path.join(os.homedir(), ".wilson");
}

function personKey(name) {
  return String(name || "").trim().toLowerCase().replace(/\s+/g, " ");
}

export class Store {
  constructor(directory = dataDirectory()) {
    this.directory = directory;
    fs.mkdirSync(directory, { recursive: true });
    this.householdPath = path.join(directory, "household.json");
    this.soulPath = path.join(directory, "soul.md");
    this.turnsPath = path.join(directory, "turns.jsonl");
    if (!fs.existsSync(this.householdPath)) {
      this.writeHousehold({ people: {}, places: {} });
    }
    if (!fs.existsSync(this.soulPath)) {
      fs.writeFileSync(this.soulPath, DEFAULT_SOUL, "utf8");
    }
  }

  readHousehold() {
    return JSON.parse(fs.readFileSync(this.householdPath, "utf8"));
  }

  writeHousehold(household) {
    // Written whole and atomically, so a crash mid-write cannot leave half a household.
    const tmp = this.householdPath + ".tmp";
    fs.writeFileSync(tmp, JSON.stringify(household, null, 2), "utf8");
    fs.renameSync(tmp, this.householdPath);
  }

  // ---- people --------------------------------------------------------------------------------

  /** The person record, created on first sight. `name` is how they asked to be known. */
  person(name) {
    const key = personKey(name);
    if (key.length === 0) {
      return null;
    }
    const household = this.readHousehold();
    if (!household.people[key]) {
      household.people[key] = { name: String(name).trim(), profile: {}, facts: [], voice: { samples: [] }, createdAt: new Date().toISOString() };
      this.writeHousehold(household);
    }
    return { key, ...household.people[key] };
  }

  people() {
    const household = this.readHousehold();
    return Object.entries(household.people).map(([key, p]) => ({ key, ...p }));
  }

  /** Merge profile fields. Empty strings delete a field; everything else replaces. */
  updateProfile(name, fields) {
    const key = personKey(name);
    const household = this.readHousehold();
    const person = household.people[key];
    if (!person) {
      return null;
    }
    for (const [field, value] of Object.entries(fields || {})) {
      if (value === "" || value === null || value === undefined) {
        delete person.profile[field];
      } else {
        person.profile[field] = value;
      }
    }
    person.updatedAt = new Date().toISOString();
    this.writeHousehold(household);
    return { key, ...person };
  }

  /** Remember a fact about a person. Repeats are ignored; the list is capped at the newest 200. */
  addFacts(name, facts, source = "said") {
    const key = personKey(name);
    const household = this.readHousehold();
    const person = household.people[key];
    if (!person) {
      return [];
    }
    const known = new Set(person.facts.map((f) => f.text.toLowerCase()));
    const added = [];
    for (const text of facts) {
      const clean = String(text).trim();
      if (clean.length === 0 || known.has(clean.toLowerCase())) {
        continue;
      }
      known.add(clean.toLowerCase());
      const fact = { text: clean, at: new Date().toISOString(), source };
      person.facts.push(fact);
      added.push(fact);
    }
    if (person.facts.length > MAX_FACTS_PER_PERSON) {
      person.facts = person.facts.slice(-MAX_FACTS_PER_PERSON);
    }
    if (added.length > 0) {
      this.writeHousehold(household);
    }
    return added;
  }

  forgetFact(name, text) {
    const key = personKey(name);
    const household = this.readHousehold();
    const person = household.people[key];
    if (!person) {
      return false;
    }
    const before = person.facts.length;
    person.facts = person.facts.filter((f) => f.text !== text);
    this.writeHousehold(household);
    return person.facts.length < before;
  }

  /** Voice samples for speaker identification: embeddings stored as plain number arrays. */
  addVoiceSample(name, embedding, label) {
    const key = personKey(name);
    const household = this.readHousehold();
    const person = household.people[key];
    if (!person) {
      return null;
    }
    person.voice = person.voice || { samples: [] };
    person.voice.samples.push({ embedding, label, at: new Date().toISOString() });
    this.writeHousehold(household);
    return person.voice.samples.length;
  }

  clearVoice(name) {
    const key = personKey(name);
    const household = this.readHousehold();
    const person = household.people[key];
    if (!person) {
      return;
    }
    person.voice = { samples: [] };
    this.writeHousehold(household);
  }

  // ---- places --------------------------------------------------------------------------------

  /** A place name as it was heard, mapped to what it resolved to. "perry sound" -> Parry Sound. */
  rememberPlace(heard, resolved) {
    const household = this.readHousehold();
    household.places[personKey(heard)] = { ...resolved, heard, at: new Date().toISOString() };
    this.writeHousehold(household);
  }

  knownPlace(heard) {
    const household = this.readHousehold();
    return household.places[personKey(heard)] || null;
  }

  knownPlaceNames() {
    const household = this.readHousehold();
    return [...new Set(Object.values(household.places).map((p) => p.name))];
  }

  places() {
    const household = this.readHousehold();
    return Object.entries(household.places).map(([heard, p]) => ({ heard: p.heard || heard, ...p }));
  }

  /** A wrong mapping must not stick: "perry sound" resolved to the wrong town once is forgotten here. */
  forgetPlace(heard) {
    const household = this.readHousehold();
    const key = personKey(heard);
    const existed = key in household.places;
    delete household.places[key];
    this.writeHousehold(household);
    return existed;
  }

  // ---- soul ----------------------------------------------------------------------------------

  soul() {
    return fs.readFileSync(this.soulPath, "utf8");
  }

  writeSoul(text) {
    // Every previous version is kept, so a bad edit is a file away from undone.
    if (fs.existsSync(this.soulPath)) {
      const stamp = new Date().toISOString().replace(/[:.]/g, "-");
      fs.copyFileSync(this.soulPath, path.join(this.directory, `soul.${stamp}.md`));
    }
    fs.writeFileSync(this.soulPath, String(text), "utf8");
  }

  // ---- turns ---------------------------------------------------------------------------------

  /** Append one event. Never throws on a bad line: the log must not take the assistant down. */
  log(record) {
    try {
      fs.appendFileSync(this.turnsPath, JSON.stringify({ at: new Date().toISOString(), ...record }) + "\n", "utf8");
    } catch (error) {
      console.log("STORE LOG FAILED " + String(error));
    }
  }

  /** The most recent events, newest last. */
  recentTurns(limit = 50) {
    if (!fs.existsSync(this.turnsPath)) {
      return [];
    }
    const lines = fs.readFileSync(this.turnsPath, "utf8").trim().split("\n").filter((l) => l.length > 0);
    return lines.slice(-limit).map((l) => {
      try {
        return JSON.parse(l);
      } catch {
        return { kind: "unreadable", line: l };
      }
    });
  }

  clearTurns() {
    if (fs.existsSync(this.turnsPath)) {
      fs.unlinkSync(this.turnsPath);
    }
  }
}
