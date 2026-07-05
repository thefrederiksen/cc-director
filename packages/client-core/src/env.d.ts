// The one build-time environment variable the shared client reads (the sign-in site base, used by
// auth/enrollRequest.ts). Declared locally so the package type-checks on its own without depending on
// the Vite client types - each shell owns its own Vite config and augments import.meta.env there.
interface ImportMetaEnv {
  readonly VITE_DT_SITE_BASE?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
