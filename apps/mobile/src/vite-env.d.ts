/// <reference types="vite/client" />
/// <reference types="vite-plugin-pwa/client" />

// Build-time constants stamped by vite.config.ts (define) so the About page can show this app's own
// build identity instead of the opaque content hash of its script filename. Replaced with string
// literals at build time.
declare const __MOBILE_COMMIT__: string;
declare const __MOBILE_BUILD_TIME__: string;
