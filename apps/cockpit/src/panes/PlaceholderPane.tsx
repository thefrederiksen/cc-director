// A placeholder for a Cockpit page not yet ported from Blazor. Each real page (the terminal, the
// session roster, the Brief, Fleet, Schedule, ...) is its own issue under epic #967 and replaces the
// matching placeholder in the router one at a time. Rendering the page title proves the shell routes
// between panes without any feature code.
export function PlaceholderPane({ title }: { title: string }) {
  return (
    <section className="pane">
      <h1 className="pane-title">{title}</h1>
      <p className="pane-note">
        This pane is a placeholder. The real {title} page is ported from the Blazor Cockpit in a later
        issue and drops in here without changing the shell.
      </p>
    </section>
  );
}
