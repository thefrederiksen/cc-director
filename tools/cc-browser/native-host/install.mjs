#!/usr/bin/env node
// CC Browser v2 - Native Messaging Host Installer
// Computes extension ID, writes host manifest, registers in Windows registry.
// Supports --extension-dir and --native-host-dir for deployed paths.
// Exports ensureInstalled() for programmatic use.

import { createHash } from 'crypto';
import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'fs';
import { join, resolve, dirname } from 'path';
import { execSync } from 'child_process';
import { fileURLToPath } from 'url';
import { homedir } from 'os';

const __dirname = dirname(fileURLToPath(import.meta.url));

// ---------------------------------------------------------------------------
// Extension ID Computation
// Chrome extension IDs are computed from the absolute path of the extension
// directory when loaded as unpacked. The algorithm:
//   1. Take the absolute path in lowercase
//   2. SHA-256 hash it
//   3. Take first 32 hex chars
//   4. Map each hex char (0-f) to (a-p)
// ---------------------------------------------------------------------------

export function computeExtensionId(extensionPath) {
  // Chrome/Brave compute unpacked extension IDs by:
  //   1. resolve() the absolute path (keep original case, keep backslashes on Windows)
  //   2. SHA-256 hash the UTF-16LE encoded bytes
  //   3. Take first 32 hex chars, map each hex digit (0-f) to (a-p)
  const resolvedPath = resolve(extensionPath);
  const buf = Buffer.from(resolvedPath, 'utf16le');
  const hash = createHash('sha256').update(buf).digest('hex');
  const first32 = hash.substring(0, 32);

  let id = '';
  for (const ch of first32) {
    id += String.fromCharCode(97 + parseInt(ch, 16));
  }
  return id;
}

// ---------------------------------------------------------------------------
// Default Deployed Paths
// ---------------------------------------------------------------------------

function getDefaultDeployDir() {
  const localAppData = process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local');
  return join(localAppData, 'cc-director', 'bin', '_cc-browser');
}

// ---------------------------------------------------------------------------
// Native Host Manifest
// ---------------------------------------------------------------------------

function createHostManifest(extensionId, nativeHostPath) {
  return {
    name: 'com.cc_browser.bridge',
    description: 'CC Browser Native Messaging Bridge',
    path: nativeHostPath,
    type: 'stdio',
    allowed_origins: [`chrome-extension://${extensionId}/`],
  };
}

// ---------------------------------------------------------------------------
// Windows Registry
// ---------------------------------------------------------------------------

const BRAVE_REG_KEY = 'HKCU\\Software\\BraveSoftware\\Brave-Browser\\NativeMessagingHosts\\com.cc_browser.bridge';
const CHROME_REG_KEY = 'HKCU\\Software\\Google\\Chrome\\NativeMessagingHosts\\com.cc_browser.bridge';
const EDGE_REG_KEY = 'HKCU\\Software\\Microsoft\\Edge\\NativeMessagingHosts\\com.cc_browser.bridge';

function readRegistryValue(regKey) {
  try {
    const output = execSync(`reg query "${regKey}" /ve`, { encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'] });
    // Output format: "    (Default)    REG_SZ    C:\path\to\manifest.json"
    const match = output.match(/REG_SZ\s+(.+)/);
    return match ? match[1].trim() : null;
  } catch {
    return null;
  }
}

function registerNativeHost(regKey, manifestPath) {
  try {
    const cmd = `reg add "${regKey}" /ve /t REG_SZ /d "${manifestPath}" /f`;
    execSync(cmd, { stdio: 'pipe' });
    console.log(`[+] Registry key set: ${regKey}`);
    console.log(`    -> ${manifestPath}`);
    return true;
  } catch (err) {
    console.error(`[-] Failed to set registry key: ${err.message}`);
    return false;
  }
}

// ---------------------------------------------------------------------------
// Extension Registry Installation (Chrome stable blocks --load-extension)
// Chrome stable reads HKCU\Software\Google\Chrome\Extensions\<id>
// with "path" pointing to a .crx file and "version" matching manifest.
// ---------------------------------------------------------------------------

function registerExtensionViaRegistry(extensionDir) {
  if (process.platform !== 'win32') return;

  // Read version from extension manifest
  const manifestPath = join(extensionDir, 'manifest.json');
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  const version = manifest.version;

  // Extension ID depends on the path
  const extensionId = computeExtensionId(extensionDir);

  // Chrome External Extensions: registry key with "path" to unpacked dir
  // Note: Chrome stable only supports .crx for external extensions.
  // For unpacked, we must use the "update_url" approach or a preferences-based install.
  // Instead, we'll register in the Chrome External Extensions JSON file approach.
  const extJsonDir = join(extensionDir, '..', 'external-extensions');
  if (!existsSync(extJsonDir)) mkdirSync(extJsonDir, { recursive: true });

  // Write external extension preferences JSON
  // Chrome reads external_extensions.json from the browser's app dir or
  // %LOCALAPPDATA%\Google\Chrome\User Data\Default\External Extensions\
  // But since we control --user-data-dir, we write to each connection's profile.
  console.log(`[install] Extension ID: ${extensionId}`);
  console.log(`[install] Extension version: ${version}`);

  return { extensionId, version };
}

function installExtensionToProfile(profileDir, extensionDir) {
  const extensionId = computeExtensionId(extensionDir);
  const manifestPath = join(extensionDir, 'manifest.json');
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  const version = manifest.version;

  const externalExtDir = join(profileDir, 'Default', 'External Extensions');
  if (!existsSync(externalExtDir)) mkdirSync(externalExtDir, { recursive: true });

  const extJson = {
    external_crx: extensionDir,
    external_version: version,
  };

  const extJsonPath = join(externalExtDir, `${extensionId}.json`);
  writeFileSync(extJsonPath, JSON.stringify(extJson, null, 2), 'utf8');
  console.log(`[install] External extension JSON: ${extJsonPath}`);
  return extensionId;
}

export { installExtensionToProfile };

// ---------------------------------------------------------------------------
// Batch Wrapper (native host must be launched by node, but Chrome expects .exe or .bat)
// ---------------------------------------------------------------------------

function createBatchWrapper(nativeHostDir) {
  const batchPath = join(nativeHostDir, 'native-host.bat');
  const content = [
    '@echo off',
    `node "%~dp0native-host.mjs" %*`,
  ].join('\r\n');
  writeFileSync(batchPath, content, 'utf8');
  console.log(`[+] Batch wrapper: ${batchPath}`);
  return batchPath;
}

// ---------------------------------------------------------------------------
// ensureInstalled - Idempotent installer (no-op if already correct)
// ---------------------------------------------------------------------------

export function ensureInstalled(opts = {}) {
  const deployDir = getDefaultDeployDir();
  const extensionDir = resolve(opts.extensionDir || join(deployDir, 'extension'));
  const nativeHostDir = resolve(opts.nativeHostDir || join(deployDir, 'native-host'));

  // Verify extension directory exists
  if (!existsSync(join(extensionDir, 'manifest.json'))) {
    console.log('[install] Extension not found, skipping native host install');
    return false;
  }

  const extensionId = computeExtensionId(extensionDir);

  // Create batch wrapper
  const batchPath = createBatchWrapper(nativeHostDir);

  // Write host manifest
  const manifest = createHostManifest(extensionId, batchPath);
  const manifestPath = join(nativeHostDir, 'com.cc_browser.bridge.json');
  const manifestJson = JSON.stringify(manifest, null, 2);

  // Check if manifest already exists and is correct
  let needsWrite = true;
  if (existsSync(manifestPath)) {
    try {
      const existing = readFileSync(manifestPath, 'utf8');
      if (existing === manifestJson) {
        needsWrite = false;
      }
    } catch {
      // Re-write if unreadable
    }
  }

  if (needsWrite) {
    writeFileSync(manifestPath, manifestJson, 'utf8');
    console.log(`[install] Host manifest written: ${manifestPath}`);
  }

  if (process.platform !== 'win32') {
    console.log('[install] Non-Windows platform, skipping registry');
    return true;
  }

  // Register for all Chromium-based browsers (Brave is primary)
  let allUpToDate = !needsWrite;

  for (const regKey of [BRAVE_REG_KEY, CHROME_REG_KEY, EDGE_REG_KEY]) {
    const current = readRegistryValue(regKey);
    if (current !== manifestPath) {
      registerNativeHost(regKey, manifestPath);
      allUpToDate = false;
    }
  }

  if (allUpToDate) {
    console.log('[install] Native messaging host already installed and up to date');
  }

  return true;
}

// ---------------------------------------------------------------------------
// CLI Main
// ---------------------------------------------------------------------------

function main() {
  const args = process.argv.slice(2);
  let extensionDir = null;
  let nativeHostDir = null;

  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--extension-dir' && args[i + 1]) {
      extensionDir = args[++i];
    } else if (args[i] === '--native-host-dir' && args[i + 1]) {
      nativeHostDir = args[++i];
    }
  }

  console.log('');
  console.log('CC Browser v2 - Native Messaging Host Installer');
  console.log('================================================');
  console.log('');

  const opts = {};
  if (extensionDir) opts.extensionDir = extensionDir;
  if (nativeHostDir) opts.nativeHostDir = nativeHostDir;

  const result = ensureInstalled(opts);

  if (!result) {
    console.error('[-] Installation failed');
    process.exit(1);
  }

  const deployDir = getDefaultDeployDir();
  const extDir = resolve(extensionDir || join(deployDir, 'extension'));
  const extId = computeExtensionId(extDir);

  console.log('');
  console.log('Installation complete.');
  console.log(`Extension ID: ${extId}`);
  console.log('');
}

// Run CLI if executed directly
const isMainModule = process.argv[1] && resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url));
if (isMainModule) {
  main();
}
