// The people Wilson knows, and what it knows about each. Read and edited from the settings screen,
// because a memory a person cannot see and correct is not one they will trust.
//
//   GET                       everyone, with profiles and facts
//   POST { name }             make sure a person exists (the device owner, on first run)
//   POST { name, profile }    change profile fields ("" clears one)
//   POST { name, forget }     drop one remembered fact by its exact text

export default async function handler(request, response, wilson) {
  if (!wilson) {
    response.status(200).json({ people: [], editable: false, reason: "This deployment keeps no memory." });
    return;
  }
  const store = wilson.store;
  if (request.method === "GET") {
    response.status(200).json({ people: store.people().map(publicView), places: store.places(), editable: true });
    return;
  }
  if (request.method !== "POST") {
    response.status(405).json({ error: "Read people with GET, change them with POST." });
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
  if (typeof payload.forgetPlace === "string") {
    const existed = store.forgetPlace(payload.forgetPlace);
    store.log({ kind: "place-forgotten", heard: payload.forgetPlace, existed });
    response.status(200).json({ forgotten: existed, places: store.places() });
    return;
  }
  const name = typeof payload.name === "string" ? payload.name.trim() : "";
  if (name.length === 0) {
    response.status(400).json({ error: "A person needs a name." });
    return;
  }
  let person = store.person(name);
  if (payload.profile && typeof payload.profile === "object") {
    person = store.updateProfile(name, payload.profile);
    store.log({ kind: "profile-edited", person: person.key, fields: Object.keys(payload.profile) });
  }
  if (typeof payload.forget === "string") {
    store.forgetFact(name, payload.forget);
    store.log({ kind: "fact-forgotten", person: person.key });
    person = store.person(name);
  }
  response.status(200).json({ person: publicView(person), editable: true });
}

/** Voice embeddings are numbers nobody needs to see; the count is enough. */
function publicView(person) {
  const { voice, ...rest } = person;
  return { ...rest, voiceSamples: voice && Array.isArray(voice.samples) ? voice.samples.length : 0 };
}
