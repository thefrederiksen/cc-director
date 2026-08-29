// Wilson's own service. Serves the built page, runs the api/ functions in-process, and gives them
// the one thing Vercel cannot: a place to keep things (server/store.mjs).
//
//   node server/wilson.mjs            after `npm run build`
//   http://localhost:5183/cc-assistant/
//
// The api/ handlers keep the Vercel signature (request, response) and take an OPTIONAL third
// argument, the Wilson context, which only this server passes. On Vercel they run without it and
// Wilson has no memory there; that is a stated property of that deployment, not a fallback.
//
// The key comes from GROQ_API_KEY, or from the credentials file named by WILSON_CREDENTIALS_FILE
// (a .env file with a GROQ_API_KEY= line), read at start.

import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { Store } from "./store.mjs";

const APP = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const DIST = path.join(APP, "dist");
const BASE = "/cc-assistant";
const PORT = Number(process.env.WILSON_PORT || 5183);

function loadKey() {
  if (process.env.GROQ_API_KEY) {
    return;
  }
  const file = process.env.WILSON_CREDENTIALS_FILE;
  if (!file) {
    throw new Error("Set GROQ_API_KEY, or WILSON_CREDENTIALS_FILE to a .env file that has a GROQ_API_KEY line.");
  }
  const line = fs.readFileSync(file, "utf8").split(/\r?\n/).find((l) => l.startsWith("GROQ_API_KEY="));
  if (!line) {
    throw new Error(`No GROQ_API_KEY line in ${file}.`);
  }
  process.env.GROQ_API_KEY = line.slice("GROQ_API_KEY=".length).trim().replace(/^"|"$/g, "");
}
loadKey();

if (!fs.existsSync(path.join(DIST, "index.html"))) {
  throw new Error(`No build at ${DIST}. Run: npm run build`);
}

const store = new Store();
const wilson = { store };

const api = {};
for (const name of ["talk", "weather", "result", "turn", "soul", "people", "voice"]) {
  api[name] = (await import(`file:///${APP.replace(/\\/g, "/")}/api/${name}.js`)).default;
}
const speak = (await import(`file:///${APP.replace(/\\/g, "/")}/api/speak.js`)).default;

const MIME = {
  ".html": "text/html", ".js": "text/javascript", ".css": "text/css", ".json": "application/json", ".wav": "audio/wav",
  ".svg": "image/svg+xml", ".png": "image/png", ".webmanifest": "application/manifest+json", ".ico": "image/x-icon", ".onnx": "application/octet-stream",
};

function readBody(req) {
  return new Promise((resolve) => {
    let data = "";
    req.on("data", (c) => (data += c));
    req.on("end", () => resolve(data));
  });
}

http
  .createServer(async (req, res) => {
    const url = new URL(req.url, "http://localhost");
    try {
      if (url.pathname === `${BASE}/api/speak`) {
        const raw = await readBody(req);
        const started = Date.now();
        const response = await speak(new Request("http://localhost" + req.url, { method: req.method, headers: { "content-type": "application/json" }, body: raw }));
        res.writeHead(response.status, Object.fromEntries(response.headers));
        if (response.body === null) {
          res.end();
          return;
        }
        const reader = response.body.getReader();
        let first = null;
        let bytes = 0;
        for (;;) {
          const { done, value } = await reader.read();
          if (done) {
            break;
          }
          if (first === null) {
            first = Date.now() - started;
          }
          bytes += value.length;
          res.write(Buffer.from(value));
        }
        res.end();
        console.log(`${new Date().toISOString()} speak ${response.status} first-bytes ${first}ms total ${Date.now() - started}ms ${bytes} bytes`);
        return;
      }

      const match = url.pathname.match(new RegExp(`^${BASE}/api/([a-z]+)$`));
      if (match && api[match[1]]) {
        const raw = await readBody(req);
        let body = raw;
        try {
          body = JSON.parse(raw);
        } catch {
          // Handlers cope with strings and empty bodies.
        }
        const response = {
          code: 200,
          status(c) {
            this.code = c;
            return this;
          },
          json(obj) {
            res.writeHead(this.code, { "Content-Type": "application/json", "Cache-Control": "no-store" });
            res.end(JSON.stringify(obj));
          },
        };
        const started = Date.now();
        await api[match[1]]({ method: req.method, body, query: Object.fromEntries(url.searchParams) }, response, wilson);
        console.log(`${new Date().toISOString()} ${match[1]} ${response.code} ${Date.now() - started}ms`);
        return;
      }

      let rel = url.pathname.startsWith(BASE) ? url.pathname.slice(BASE.length) : url.pathname;
      if (rel === "" || rel === "/") {
        rel = "/index.html";
      }
      let file = path.join(DIST, rel);
      if (!fs.existsSync(file) || fs.statSync(file).isDirectory()) {
        file = path.join(DIST, "index.html");
      }
      res.writeHead(200, { "Content-Type": MIME[path.extname(file)] || "application/octet-stream", "Cache-Control": "no-cache" });
      fs.createReadStream(file).pipe(res);
    } catch (error) {
      console.log(`${new Date().toISOString()} ERROR ${req.url} ${String(error)}`);
      if (!res.headersSent) {
        res.writeHead(500, { "Content-Type": "application/json" });
      }
      res.end(JSON.stringify({ error: String(error) }));
    }
  })
  .listen(PORT, "0.0.0.0", () => {
    console.log(`Wilson on http://localhost:${PORT}${BASE}/`);
    console.log(`Data in ${store.directory}`);
  });
