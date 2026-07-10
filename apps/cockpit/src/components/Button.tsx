import type { ButtonHTMLAttributes } from "react";

// The shared Cockpit button (issue #1244). Every page in the Cockpit used to hand-roll its own button
// class (act-btn, sched-btn, qbtn, linkbtn, and roughly fifteen more), so a button looked and behaved
// differently on every page. This one component gives the whole app four named button variants on the
// shared navy palette (docs/CockpitVisualStyle.md), so a "primary" or a "danger" button reads the same
// everywhere. It forwards every native button attribute (onClick, disabled, type, title, ...), so it is
// a drop-in replacement for a plain <button>.

/**
 * The four button roles the Cockpit needs.
 *   - primary: the main action on a surface (accent blue). One per surface.
 *   - secondary: a supporting action (a bordered navy button). The default.
 *   - danger: a destructive action (red). Always the confirm button of a ConfirmDialog.
 *   - ghost: a borderless link-like action that blends into its surface (a small inline control).
 */
export type ButtonVariant = "primary" | "secondary" | "danger" | "ghost";

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  /** The button role; defaults to "secondary" (a plain supporting action). */
  variant?: ButtonVariant;
}

export function Button({ variant = "secondary", className, type, ...rest }: ButtonProps) {
  // Default type to "button": a Cockpit button is an action, never an accidental form submit.
  const classes = ["ui-btn", `ui-btn-${variant}`];
  if (className !== undefined && className.length > 0) classes.push(className);
  return <button type={type ?? "button"} className={classes.join(" ")} {...rest} />;
}
