#!/usr/bin/env node

/**
 * cc-fox-browser deploy script
 *
 * Copies source files to the deployed cc-fox-browser directory and removes any
 * stale .exe that would shadow the .cmd entry point.
 *
 * Usage: npm run deploy  (from tools/cc-fox-browser/)
 */

import { copyFileSync, cpSync, readdirSync, unlinkSync, existsSync, mkdirSync, writeFileSync } from 'fs';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));

const LOCAL_APP_DATA = process.env.LOCALAPPDATA;
if (!LOCAL_APP_DATA) {
  console.error('ERROR: LOCALAPPDATA environment variable not set');
  process.exit(1);
}

const DEPLOY_BASE = join(LOCAL_APP_DATA, 'cc-director', 'bin');
const DEPLOY_DIR = join(DEPLOY_BASE, '_cc-fox-browser', 'src');
const SOURCE_DIR = join(__dirname, 'src');
const STALE_EXE = join(DEPLOY_BASE, 'cc-fox-browser.exe');

// Ensure target directory exists
if (!existsSync(DEPLOY_DIR)) {
  mkdirSync(DEPLOY_DIR, { recursive: true });
}

// Copy all .mjs source files
const files = readdirSync(SOURCE_DIR).filter(f => f.endsWith('.mjs'));
let copied = 0;

for (const file of files) {
  const src = join(SOURCE_DIR, file);
  const dst = join(DEPLOY_DIR, file);
  copyFileSync(src, dst);
  console.log(`  [+] ${file}`);
  copied++;
}

console.log(`Deployed ${copied} files to ${DEPLOY_DIR}`);

// Remove stale .exe if present
if (existsSync(STALE_EXE)) {
  unlinkSync(STALE_EXE);
  console.log(`Removed stale exe: ${STALE_EXE}`);
}

// Copy package.json
const pkgSrc = join(__dirname, 'package.json');
const pkgDst = join(DEPLOY_BASE, '_cc-fox-browser', 'package.json');
copyFileSync(pkgSrc, pkgDst);
console.log('  [+] package.json');

// Copy node_modules if they exist
const nodeModulesSrc = join(__dirname, 'node_modules');
const nodeModulesDst = join(DEPLOY_BASE, '_cc-fox-browser', 'node_modules');

if (existsSync(nodeModulesSrc)) {
  cpSync(nodeModulesSrc, nodeModulesDst, { recursive: true, force: true });
  console.log('  [+] node_modules/');
}

// Create launcher .cmd in bin directory
const launcherCmd = '@node "%~dp0_cc-fox-browser\\src\\cli.mjs" %*';
writeFileSync(join(DEPLOY_BASE, 'cc-fox-browser.cmd'), launcherCmd);
console.log('  [+] cc-fox-browser.cmd (launcher)');

// Create launcher sh in bin directory (for Git Bash)
const launcherSh = '#!/bin/sh\nnode "$(dirname "$0")/_cc-fox-browser/src/cli.mjs" "$@"';
writeFileSync(join(DEPLOY_BASE, 'cc-fox-browser'), launcherSh);
console.log('  [+] cc-fox-browser (bash launcher)');

console.log('');
console.log('Done. To use:');
console.log('  cc-fox-browser daemon');
console.log('  cc-fox-browser start --workspace upwork');
