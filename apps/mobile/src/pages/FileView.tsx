import { useEffect, useState } from "react";
import { useNavigate, useParams, useSearchParams } from "react-router-dom";
import { classifyFile } from "@devthrottle/client-core/history/fileTypes";
import type { FileViewerType } from "@devthrottle/client-core/history/fileTypes";
import {
  GatewayError,
  fetchSessionFileText,
  sessionFileUrl,
} from "@devthrottle/client-core/api/client";
import { markdownToHtml } from "@devthrottle/client-core/history/historyMarkdown";

// Local Files mission (Phase 3): the mobile /m file viewer. A clicked file path - in the Chat page or
// in the Terminal mirror - navigates to this FULL-SCREEN ROUTE (mobile has no modal shell; every view
// is a route), which renders the file IN PLACE by type, streamed from the owning session's machine
// through the Gateway (sessionFileUrl / fetchSessionFileText). Only the shell differs from the Cockpit
// FileViewerModal - a page, not a modal - the render logic mirrors it exactly (brief decision 4):
//   image    -> <img>            (fit within the body, natural size scrolls)
//   pdf      -> <iframe>         (the browser's native PDF viewer; iOS Safari may not render it inline)
//   html     -> SANDBOXED <iframe sandbox="allow-scripts"> - NO allow-same-origin (brief decision 3),
//               so a self-contained report's own chart JS still runs but in a null origin with none of
//               the Gateway's cookie authority.
//   markdown -> fetch the text, render with the shared sanitized markdownToHtml()
//   text     -> fetch the text, show in a <pre> (covers code files too; horizontal scroll on a phone)
//   download -> unknown/binary: no guessed render; show the file name and a plain Download link.
//
// The absolute file path rides as a ?path= query param so a hard reload / deep link resolves it too
// (route state would be lost on refresh). The shell shows immediately; the text-fetching modes show
// "Loading..." until the bytes arrive; any load failure is a specific, visible message (never a silent
// blank). Back returns to the session the file was opened from (browser back).

/** The final path segment shown as the file's name (handles both \ and / separators). */
function baseName(path: string): string {
  const segments = path.replace(/\\/g, "/").split("/");
  return segments[segments.length - 1] || path;
}

// Turn a file-load failure into a specific, human message. A 404 is a missing file; a 503 is the
// owning session's machine being offline (the Gateway could not reach the Director). Anything else
// shows its status so the failure is never mistaken for an empty file.
function fileLoadMessage(err: unknown): string {
  if (err instanceof GatewayError) {
    if (err.status === 404) return "Could not load file: not found (404). It may have been moved or deleted.";
    if (err.status === 503) return "Could not load file: the session's machine is offline (503).";
    return `Could not load file: ${err.status}`;
  }
  return `Could not load file: ${err instanceof Error ? err.message : String(err)}`;
}

export function FileView() {
  const { sessionId } = useParams<{ sessionId: string }>();
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const path = params.get("path") ?? "";

  // Back returns to the exact session tab (Chat or Terminal) the file was opened from - the browser's
  // own back step, which is how the half-built "mobile view-links" feature intended Back to work.
  const goBack = () => navigate(-1);

  if (!sessionId || !path) {
    return (
      <div className="terminal-screen">
        <header className="app-bar">
          <button type="button" className="file-view-back" onClick={goBack}>Back</button>
          <h1 className="term-title">File</h1>
        </header>
        <div className="file-view-body">
          <div className="file-view-error">No file path was provided.</div>
        </div>
      </div>
    );
  }

  const type = classifyFile(path);
  const url = sessionFileUrl(sessionId, path);
  const name = baseName(path);

  return (
    <div className="terminal-screen">
      <header className="app-bar">
        <button type="button" className="file-view-back" onClick={goBack}>Back</button>
        <h1 className="term-title" title={path}>{name}</h1>
        <a className="file-view-download" href={url} download={name}>Download</a>
      </header>
      <div className="file-view-body">
        <FileViewContent type={type} url={url} name={name} sessionId={sessionId} path={path} />
      </div>
    </div>
  );
}

function FileViewContent(props: {
  type: FileViewerType;
  url: string;
  name: string;
  sessionId: string;
  path: string;
}) {
  const { type, url, name, sessionId, path } = props;
  switch (type) {
    case "image":
      return <ImageFile url={url} name={name} />;
    case "pdf":
      return <iframe className="file-view-frame" src={url} title={name} />;
    case "html":
      // Sandboxed WITHOUT allow-same-origin (brief decision 3): a self-contained report's own script
      // runs, but in a null origin, so it cannot use the Gateway's cookie authority to call its APIs.
      return <iframe className="file-view-frame" src={url} title={name} sandbox="allow-scripts" />;
    case "markdown":
      return <TextFile sessionId={sessionId} path={path} render="markdown" />;
    case "text":
      return <TextFile sessionId={sessionId} path={path} render="text" />;
    case "download":
    default:
      return (
        <div className="file-view-download-panel">
          <p className="file-view-download-note">This file type has no in-app preview.</p>
          <p className="file-view-download-name">{name}</p>
          <a className="file-view-download-btn" href={url} download={name}>Download</a>
        </div>
      );
  }
}

// An image renders directly from the Gateway URL. A failed load (missing file / offline machine) shows
// a specific message rather than a broken-image icon, keeping the fail-loud contract for this mode too.
function ImageFile({ url, name }: { url: string; name: string }) {
  const [failed, setFailed] = useState(false);
  if (failed) {
    return (
      <div className="file-view-error">
        Could not load image. The file may be missing (404) or the session's machine offline.
      </div>
    );
  }
  return (
    <div className="file-view-scroll">
      <img className="file-view-image" src={url} alt={name} onError={() => setFailed(true)} />
    </div>
  );
}

// The Markdown and text/code modes need the file's STRING, not an <img>/<iframe> src, so they fetch the
// text through the Gateway. Loading shows "Loading..."; a failure shows the specific reason.
function TextFile({
  sessionId,
  path,
  render,
}: {
  sessionId: string;
  path: string;
  render: "markdown" | "text";
}) {
  const [text, setText] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setText(null);
    setError(null);
    fetchSessionFileText(sessionId, path, controller.signal)
      .then((t) => setText(t))
      .catch((err) => {
        if (controller.signal.aborted) return;
        setError(fileLoadMessage(err));
      });
    return () => controller.abort();
  }, [sessionId, path]);

  if (error !== null) return <div className="file-view-error">{error}</div>;
  if (text === null) return <div className="file-view-loading">Loading...</div>;
  if (render === "markdown") {
    // markdownToHtml is the shared sanitized renderer both chat views already trust (XSS-safe).
    return <div className="file-view-md" dangerouslySetInnerHTML={{ __html: markdownToHtml(text) }} />;
  }
  return <pre className="file-view-pre">{text}</pre>;
}
