/// <reference types="vite/client" />

// Build-time constants stamped by vite.config.ts (define) so the About page can show the cockpit's
// own build identity. Replaced with string literals at build time.
declare const __COCKPIT_COMMIT__: string;
declare const __COCKPIT_BUILD_TIME__: string;
