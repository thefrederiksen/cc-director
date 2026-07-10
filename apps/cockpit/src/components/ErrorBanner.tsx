import { Button } from "./Button";

// The shared Cockpit error banner (issue #1244). When a Gateway or cloud read fails, a page must say so
// loudly rather than showing a fabricated empty or signed-out view (the no-fallback rule). This is the
// one red banner every page uses to surface that failure, with an optional Retry button when the action
// can simply be tried again. role="alert" makes a screen reader announce the failure immediately.

export interface ErrorBannerProps {
  /** The plain-English failure message (usually gatewayErrorMessage of the caught error). */
  message: string;
  /** When provided, a retry control appears and calls this; omit it when there is nothing to retry. */
  onRetry?: () => void;
  /** The retry control's label; defaults to "Try again". */
  retryLabel?: string;
}

export function ErrorBanner({ message, onRetry, retryLabel = "Try again" }: ErrorBannerProps) {
  return (
    <div className="ui-error-banner" role="alert">
      <span className="ui-error-banner-text">{message}</span>
      {onRetry !== undefined && (
        <Button variant="secondary" className="ui-error-banner-retry" onClick={onRetry}>
          {retryLabel}
        </Button>
      )}
    </div>
  );
}
