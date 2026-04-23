// Regenerate all expected-snapshot JSON files from the captured .bin fixtures.
// Writes into src/CcDirector.Core.Tests/TestData/<name>.expected.json.
//
// Dimensions for each capture match the grid size the capture was taken at
// (see each capture's companion .json metadata).
//
// Usage:
//   node snapshot-all.mjs

import { execFileSync } from 'node:child_process';
import path from 'node:path';
import url from 'node:url';
import fs from 'node:fs';

const here = path.dirname(url.fileURLToPath(import.meta.url));
const testDataDir = path.resolve(here, '..', '..', 'src', 'CcDirector.Core.Tests', 'TestData');

const fixtures = [
    { bin: 'claude-startup.bin',         cols: 120, rows: 30 },
    { bin: 'claude-stray-chars.bin',     cols: 147, rows: 50 },
    { bin: 'claude-stray-today.bin',     cols: 147, rows: 47 },
    { bin: 'claude-resize-gap.bin',      cols: 147, rows: 50 },
    { bin: 'claude-session-medium.bin',  cols: 147, rows: 50 },
    { bin: 'claude-session-large-47.bin',cols: 147, rows: 47 },
    { bin: 'claude-session-huge-50.bin', cols: 147, rows: 50 },
];

for (const f of fixtures) {
    const binPath = path.join(testDataDir, f.bin);
    const outPath = path.join(testDataDir, f.bin.replace(/\.bin$/, '.expected.json'));

    if (!fs.existsSync(binPath)) {
        process.stderr.write(`SKIP ${f.bin} (not found)\n`);
        continue;
    }

    const json = execFileSync(
        process.execPath,
        [path.join(here, 'snapshot.mjs'), binPath, String(f.cols), String(f.rows), '1000'],
        { encoding: 'utf8', maxBuffer: 256 * 1024 * 1024 },
    );

    fs.writeFileSync(outPath, json);
    const bytes = fs.statSync(outPath).size;
    process.stdout.write(`WROTE ${path.relative(testDataDir, outPath)} (${(bytes / 1024 / 1024).toFixed(2)} MB)\n`);
}
