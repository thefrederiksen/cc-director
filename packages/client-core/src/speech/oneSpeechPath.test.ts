import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, resolve, sep } from "node:path";
import { describe, expect, it } from "vitest";

// ONE PLACE THE BROWSER SPEAKS FROM, ENFORCED (issue #1031).
//
// The C# half of this contract is held by a type: a sink takes a SpokenUtterance, so a bare string does not
// compile. The browser half needs the same property, and TypeScript alone cannot give all of it - nothing stops
// a new file from reaching for `window.speechSynthesis` directly and speaking a literal, which is exactly what
// the code did before this mission and why a French refusal was read in an English voice.
//
// So there is one sanctioned sink, and this scan fails if speech appears anywhere else. It is small and sharp
// rather than sprawling because there is exactly ONE local speech call left in the product: Car Mode held the
// others and was deleted (#1028).
//
// SABOTAGE-TESTED. Planting `speechSynthesis.speak(new SpeechSynthesisUtterance("hello"))` in a page makes this
// go red naming the file and the line. A guard nobody has seen fail is a comment.

/** The ONE file allowed to talk to the platform's speech engine. Relative to the repository root. */
const SANCTIONED_SINK = join("packages", "client-core", "src", "speech", "localSpeech.ts");

/** Where product code lives. Tests and the sink's own tests are scanned too - a test that speaks a literal is
 *  not a defect, so test files are allowed the platform call but still must not be the product's speech path. */
const SCANNED = [
  join("packages", "client-core", "src"),
  join("apps", "mobile", "src"),
  join("apps", "cockpit", "src"),
];

/** How the platform is reached. Any of these means the file is speaking for itself. */
const PLATFORM_SPEECH = [/\bspeechSynthesis\b/, /new\s+SpeechSynthesisUtterance\b/];

function repoRoot(): string {
  // The vitest working directory is the client-core package; the repository root is two levels up.
  return resolve(process.cwd(), "..", "..");
}

function sourceFiles(dir: string): string[] {
  const root = join(repoRoot(), dir);
  const out: string[] = [];
  const walk = (current: string) => {
    for (const entry of readdirSync(current)) {
      const path = join(current, entry);
      if (statSync(path).isDirectory()) {
        walk(path);
        continue;
      }
      if (/\.(ts|tsx)$/.test(entry)) out.push(path);
    }
  };
  walk(root);
  return out;
}

function isTestFile(path: string): boolean {
  return /\.test\.(ts|tsx)$/.test(path);
}

function relative(path: string): string {
  return path.slice(repoRoot().length + 1).split(sep).join("/");
}

describe("the browser's one speech path", () => {
  // The scan has to be able to SEE the thing it polices, or an empty offender list proves nothing.
  it("can see the sanctioned sink and the files it is scanning", () => {
    const files = SCANNED.flatMap(sourceFiles);
    expect(files.length).toBeGreaterThan(100);

    const sink = readFileSync(join(repoRoot(), SANCTIONED_SINK), "utf8");
    expect(PLATFORM_SPEECH.some((pattern) => pattern.test(sink))).toBe(true);
  });

  /**
   * THE GUARD. No product file but the sanctioned sink touches the platform's speech engine.
   *
   * A file that reaches for speechSynthesis itself is a file that decides its own language - or forgets to - and
   * that is the defect, twice over. Everything that needs to say something calls the sink with an utterance,
   * which it cannot build without a language.
   */
  it("lets only the sanctioned sink reach the platform's speech engine", () => {
    const offenders: string[] = [];

    for (const dir of SCANNED) {
      for (const path of sourceFiles(dir)) {
        const rel = relative(path);
        if (rel === SANCTIONED_SINK.split(sep).join("/")) continue;
        if (isTestFile(path)) continue;

        const lines = readFileSync(path, "utf8").split("\n");
        lines.forEach((line, index) => {
          if (line.trimStart().startsWith("//") || line.trimStart().startsWith("*")) return;
          if (PLATFORM_SPEECH.some((pattern) => pattern.test(line))) {
            offenders.push(`${rel}:${index + 1}`);
          }
        });
      }
    }

    expect(
      offenders,
      "Only packages/client-core/src/speech/localSpeech.ts may reach the platform's speech engine (issue "
        + "#1031). Everything else must call speakLocally with a SpokenUtterance, which cannot be built without "
        + "a language - a file that speaks for itself is a file that can speak with no language at all, and that "
        + "is how a correctly translated French refusal came to be read aloud in an English voice. Offenders: "
        + offenders.join(", "),
    ).toEqual([]);
  });

  /**
   * A LITERAL STRING MUST NOT REACH THE SINK.
   *
   * The sink's parameter is a SpokenUtterance, so `speakLocally(engine, "hello")` does not type-check - the
   * compiler is the real guard here. This scan covers the shape the compiler cannot refuse: building the
   * utterance from a literal at the call site, which would be a spoken sentence living in the client with no
   * translation, no accent guard and no encoding test. Spoken content belongs on the Gateway, in the phrase
   * file, in every language.
   */
  it("never builds an utterance from a literal in product code", () => {
    const offenders: string[] = [];
    const literalUtterance = /utteranceFor\s*\([^)]*["'`]/;

    for (const dir of SCANNED) {
      for (const path of sourceFiles(dir)) {
        if (isTestFile(path)) continue;
        const lines = readFileSync(path, "utf8").split("\n");
        lines.forEach((line, index) => {
          // A language code IS a literal and is meant to be one; what must never be literal is the WORDS. The
          // second argument is the text, so a quoted second argument is the offence.
          const call = line.match(/utteranceFor\s*\(([^)]*)\)/);
          if (!call) return;
          const args = call[1].split(",");
          if (args.length >= 2 && /^\s*["'`]/.test(args[1])) {
            offenders.push(`${relative(path)}:${index + 1}`);
          }
          void literalUtterance;
        });
      }
    }

    expect(
      offenders,
      "A spoken sentence must not be written into the client (issue #1031). Every fixed sentence the product "
        + "says lives in SpokenPhrases on the Gateway, in all three languages, where the accent, completeness "
        + "and encoding guards are - a literal here would be an English fragment in a French session. "
        + "Offenders: " + offenders.join(", "),
    ).toEqual([]);
  });
});
