import type { ReactNode } from "react";

// The shared Cockpit page header (issue #1244). The Cockpit had roughly eighteen distinct page-header
// class names, one per page, so the title, the one-line description, and the right-aligned actions sat
// at different sizes and spacings on every page. This one component gives every page the same header:
// a title, an optional one-line subtitle beneath it, and an optional actions slot pinned to the right.

export interface PageHeaderProps {
  /** The page title (rendered as the page's single h1). */
  title: string;
  /** An optional one-line description shown beneath the title. */
  subtitle?: string;
  /** Optional right-aligned actions (buttons, links) for this page. */
  actions?: ReactNode;
}

export function PageHeader({ title, subtitle, actions }: PageHeaderProps) {
  return (
    <header className="ui-page-header">
      <div className="ui-page-header-text">
        <h1 className="ui-page-title">{title}</h1>
        {subtitle !== undefined && subtitle.length > 0 && (
          <p className="ui-page-subtitle">{subtitle}</p>
        )}
      </div>
      {actions !== undefined && <div className="ui-page-header-actions">{actions}</div>}
    </header>
  );
}
