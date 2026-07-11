// Local Files mission (Phase 1): classify a detected file path into ONE viewer type, purely by its
// extension, so a click knows which render mode to open. This is app-agnostic (no UI imports): the
// cockpit and the mobile /m shells both call it and then open their own viewer for the returned type.
//
// The type maps exactly to the render modes in the mission brief (decision 4):
//   image    -> <img src=fileUrl>
//   pdf      -> <iframe src=fileUrl> (browser native PDF viewer)
//   html     -> SANDBOXED <iframe src=fileUrl>
//   markdown -> fetch the text, render with markdownToHtml()
//   text     -> fetch the text, show in <pre> (covers code files too)
//   download -> unknown / binary: do not guess, offer a Download button
//
// We cannot sniff bytes on the client, so classification is extension-driven with a textual
// allowlist: a known-textual extension is 'text', and everything else unknown is 'download'
// (fail loud, no guessing) - matching the brief's "do not guess" rule for unknown/binary files.

export type FileViewerType = "image" | "pdf" | "html" | "markdown" | "text" | "download";

// Images the browser renders directly with <img>.
const IMAGE_EXTENSIONS = new Set([
  "png", "jpg", "jpeg", "gif", "svg", "webp", "bmp",
]);

// Markdown, rendered through the shared sanitized markdownToHtml().
const MARKDOWN_EXTENSIONS = new Set([
  "md", "markdown",
]);

// HTML, rendered in a sandboxed iframe (decision 3).
const HTML_EXTENSIONS = new Set([
  "html", "htm",
]);

// Textual / code extensions shown as plain text in a <pre>. The base set from the brief (txt, log,
// json, csv) plus the common code and config extensions so a source or config file a session
// produced opens as readable text rather than an opaque download.
const TEXT_EXTENSIONS = new Set([
  // Plain text and data.
  "txt", "text", "log", "json", "csv", "tsv", "xml", "yaml", "yml", "toml", "ini", "cfg", "conf",
  "properties", "env", "rst", "tex",
  // Web and stylesheets.
  "js", "jsx", "ts", "tsx", "mjs", "cjs", "css", "scss", "sass", "less",
  // General-purpose languages.
  "py", "cs", "java", "kt", "kts", "scala", "go", "rs", "rb", "php", "swift", "dart", "lua",
  "pl", "r", "vb", "fs", "clj", "ex", "exs", "erl",
  // C / C++ family.
  "c", "h", "cpp", "cc", "cxx", "hpp", "hh", "m", "mm",
  // Shell and build scripts.
  "sh", "bash", "zsh", "ps1", "psm1", "bat", "cmd", "sql", "gradle",
]);

// A few common EXTENSIONLESS textual files. Classification is extension-first, but these bare names
// are text often enough that treating them as a download would surprise the user.
const TEXT_BASENAMES = new Set([
  "dockerfile", "makefile", "readme", "license", "licence", "changelog", "gitignore",
  "gitattributes", "editorconfig", "npmrc", "env",
]);

/** The lower-cased extension WITHOUT the dot (e.g. "PNG" -> "png"), or "" when there is none. */
export function fileExtension(path: string): string {
  if (!path) return "";
  // Take the last path segment so a dot in a parent directory name is never mistaken for the ext.
  const segments = path.replace(/\\/g, "/").split("/");
  const name = segments[segments.length - 1] ?? "";
  const dot = name.lastIndexOf(".");
  // dot <= 0 means no dot, or a dotfile like ".gitignore" (no real extension).
  if (dot <= 0) return "";
  return name.slice(dot + 1).toLowerCase();
}

/** The lower-cased final path segment, dots stripped for a dotfile (".gitignore" -> "gitignore"). */
function fileBaseName(path: string): string {
  if (!path) return "";
  const segments = path.replace(/\\/g, "/").split("/");
  const name = (segments[segments.length - 1] ?? "").toLowerCase();
  return name.startsWith(".") ? name.slice(1) : name;
}

/**
 * Classify an absolute file path into the viewer type that should render it. Unknown or binary
 * extensions return "download" - the caller offers a Download button rather than guessing a render.
 */
export function classifyFile(path: string): FileViewerType {
  const ext = fileExtension(path);
  if (ext) {
    if (IMAGE_EXTENSIONS.has(ext)) return "image";
    if (ext === "pdf") return "pdf";
    if (HTML_EXTENSIONS.has(ext)) return "html";
    if (MARKDOWN_EXTENSIONS.has(ext)) return "markdown";
    if (TEXT_EXTENSIONS.has(ext)) return "text";
    return "download";
  }
  // No extension: a small set of well-known bare textual filenames reads as text; all else download.
  if (TEXT_BASENAMES.has(fileBaseName(path))) return "text";
  return "download";
}
