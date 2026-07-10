// The shared Cockpit loading indicator (issue #1244). Every page wrote its own "Loading..." line in a
// slightly different wording and style; this gives them one. role="status" lets a screen reader
// announce that the page is loading (the accessibility follow-up in issue #1248 builds on this).

export interface LoadingStateProps {
  /** The message to show; defaults to the plain "Loading..." every page used. */
  message?: string;
}

export function LoadingState({ message = "Loading..." }: LoadingStateProps) {
  return (
    <div className="ui-loading" role="status">
      {message}
    </div>
  );
}
