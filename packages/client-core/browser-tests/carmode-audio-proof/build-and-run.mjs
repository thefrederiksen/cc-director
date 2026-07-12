// Build and serve the Car Mode "whole reply is heard" audio-event proof (harness.html).
//
// It bundles the REAL product playback leaf - packages/client-core/src/carmode/audioPlayback.ts - into a
// classic script that sets globalThis.CarModeAudioPlayback, so the harness tests the ACTUAL shipping code,
// not a copy. Then it serves this directory over http (a real origin, so the browser will load the module
// and play blob audio without file:// restrictions) and prints the URL to open.
//
// Run:  node build-and-run.mjs
// Then drive the printed URL in a real browser (browser-harness) and read window.__RESULT__ - that JSON is
// the committed evidence. See README.md.
//
// ASCII only, plain English, no external network.
import { build } from "esbuild";
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { dirname, join, extname } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const entry = join(here, "..", "..", "src", "carmode", "audioPlayback.ts");
const port = Number(process.env.PORT || 8791);

const contentTypes = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8" };

async function main() {
  // Bundle the product file to an IIFE global. It has no imports, so this is a pure type-strip + wrap.
  const result = await build({
    entryPoints: [entry],
    bundle: true,
    format: "iife",
    globalName: "CarModeAudioPlayback",
    write: false,
    target: "es2021",
    logLevel: "warning",
  });
  const moduleJs = result.outputFiles[0].text;
  console.log(`[audio-proof] bundled product playClip from ${entry} (${moduleJs.length} bytes)`);

  const server = createServer(async (req, res) => {
    const path = (req.url || "/").split("?")[0];
    try {
      if (path === "/audioPlayback.iife.js") {
        res.writeHead(200, { "content-type": contentTypes[".js"] });
        res.end(moduleJs);
        return;
      }
      const file = path === "/" ? "harness.html" : path.replace(/^\/+/, "");
      const body = await readFile(join(here, file));
      res.writeHead(200, { "content-type": contentTypes[extname(file)] || "application/octet-stream" });
      res.end(body);
    } catch {
      res.writeHead(404, { "content-type": "text/plain" });
      res.end("not found");
    }
  });

  server.listen(port, "127.0.0.1", () => {
    console.log(`[audio-proof] serving at http://127.0.0.1:${port}/harness.html`);
    console.log("[audio-proof] open it in a real browser, then read window.__RESULT__ for the verdict.");
  });
}

main().catch((err) => {
  console.error(`[audio-proof] FAILED: ${err && err.message ? err.message : err}`);
  process.exit(1);
});
