// Feed a raw ANSI byte stream captured from a real terminal session through
// @xterm/headless -- the same VT engine VS Code and many other industry tools
// use -- and emit a deterministic JSON snapshot of the final terminal state
// (screen grid + scrollback + cursor).
//
// This JSON is the ground-truth expectation for CC Director's own parser.
// Our C# AnsiParser replays the same bytes and must produce an identical dump.
//
// Usage:
//   node snapshot.mjs <bin-path> <cols> <rows> [scrollback]
// Example:
//   node snapshot.mjs ../../src/CcDirector.Core.Tests/TestData/claude-stray-today.bin 147 47 1000

import fs from 'node:fs';
import path from 'node:path';
import headless from '@xterm/headless';
const { Terminal } = headless;

function die(msg) {
    process.stderr.write(`ERROR: ${msg}\n`);
    process.exit(1);
}

const args = process.argv.slice(2);
if (args.length < 3) die('Usage: node snapshot.mjs <bin-path> <cols> <rows> [scrollback]');

const binPath = path.resolve(args[0]);
const cols = parseInt(args[1], 10);
const rows = parseInt(args[2], 10);
const scrollback = args[3] ? parseInt(args[3], 10) : 1000;

if (!Number.isInteger(cols) || cols <= 0) die(`Invalid cols: ${args[1]}`);
if (!Number.isInteger(rows) || rows <= 0) die(`Invalid rows: ${args[2]}`);
if (!fs.existsSync(binPath)) die(`File not found: ${binPath}`);

const bytes = fs.readFileSync(binPath);

const term = new Terminal({
    cols,
    rows,
    scrollback,
    allowProposedApi: true,
    convertEol: false,
});

// xterm.js write is async; block until the internal write buffer is drained.
await new Promise((resolve) => term.write(bytes, () => resolve()));

// Serialize every cell in both the scrollback region and the visible screen.
// Order matches xterm.js buffer coordinates: y=0 is the top of scrollback,
// y=buffer.length-1 is the bottom of the visible screen.
const buf = term.buffer.active;

function cellToJson(cell) {
    const width = cell.getWidth();
    if (width === 0) {
        // Second half of a wide character — store as null so both sides agree.
        return { ch: '', w: 0 };
    }
    const out = {
        ch: cell.getChars() || ' ',
        w: width,
    };
    if (cell.isFgDefault()) {
        out.fg = null;
    } else if (cell.isFgRGB()) {
        const n = cell.getFgColor();
        out.fg = { mode: 'rgb', r: (n >> 16) & 0xff, g: (n >> 8) & 0xff, b: n & 0xff };
    } else if (cell.isFgPalette()) {
        out.fg = { mode: 'palette', idx: cell.getFgColor() };
    } else {
        out.fg = null;
    }
    if (cell.isBgDefault()) {
        out.bg = null;
    } else if (cell.isBgRGB()) {
        const n = cell.getBgColor();
        out.bg = { mode: 'rgb', r: (n >> 16) & 0xff, g: (n >> 8) & 0xff, b: n & 0xff };
    } else if (cell.isBgPalette()) {
        out.bg = { mode: 'palette', idx: cell.getBgColor() };
    } else {
        out.bg = null;
    }
    const attrs = [];
    if (cell.isBold())       attrs.push('bold');
    if (cell.isItalic())     attrs.push('italic');
    if (cell.isUnderline())  attrs.push('underline');
    if (cell.isDim())        attrs.push('dim');
    if (cell.isInverse())    attrs.push('inverse');
    if (cell.isInvisible())  attrs.push('invisible');
    if (cell.isStrikethrough()) attrs.push('strikethrough');
    if (cell.isBlink())      attrs.push('blink');
    if (attrs.length) out.attrs = attrs;
    return out;
}

function rowToJson(y) {
    const line = buf.getLine(y);
    if (!line) return null;
    const cells = [];
    const c = line.length;
    for (let x = 0; x < c; x++) {
        const cell = line.getCell(x);
        cells.push(cellToJson(cell));
    }
    return {
        cells,
        isWrapped: line.isWrapped,
    };
}

const totalRows = buf.length;
const viewportY = buf.viewportY;
const screenRows = [];
const scrollbackRows = [];

for (let y = 0; y < totalRows; y++) {
    const rowJson = rowToJson(y);
    if (y < viewportY) scrollbackRows.push(rowJson);
    else screenRows.push(rowJson);
}

const snapshot = {
    meta: {
        source: path.basename(binPath),
        bytes: bytes.length,
        engine: '@xterm/headless',
        cols,
        rows,
        scrollbackCapacity: scrollback,
    },
    cursor: {
        x: buf.cursorX,
        y: buf.cursorY,
    },
    dimensions: {
        cols,
        rows,
        totalBufferRows: totalRows,
        viewportY,
    },
    scrollback: scrollbackRows,
    screen: screenRows,
};

process.stdout.write(JSON.stringify(snapshot, null, 2));
process.stdout.write('\n');

term.dispose();
