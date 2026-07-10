import type { ReactNode } from "react";

// The shared Cockpit empty state (issue #1244) - the "there is nothing here yet" message a page shows
// when a list or a folder is legitimately empty (distinct from an error, which uses ErrorBanner). An
// optional title gives it a heading, and an optional action slot offers the obvious next step (for
// example, a "New cron job" button on an empty Schedule page).

export interface EmptyStateProps {
  /** An optional short heading above the message. */
  title?: string;
  /** The plain-English explanation of why the surface is empty and what to do. */
  message: string;
  /** An optional action (a button) offering the obvious next step. */
  action?: ReactNode;
}

export function EmptyState({ title, message, action }: EmptyStateProps) {
  return (
    <div className="ui-empty">
      {title !== undefined && title.length > 0 && <div className="ui-empty-title">{title}</div>}
      <div className="ui-empty-message">{message}</div>
      {action !== undefined && <div className="ui-empty-action">{action}</div>}
    </div>
  );
}
