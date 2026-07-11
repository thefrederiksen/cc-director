/// <reference types="vite/client" />
/// <reference types="vite-plugin-pwa/client" />

// The client build id, inlined by Vite's `define` at build time (see vite.config.ts). It is the git
// short commit sha plus the build timestamp, shown on the Car Mode screen so the owner can confirm at a
// glance he is on the latest page, not an old cached bundle.
declare const __CLIENT_BUILD_ID__: string;
