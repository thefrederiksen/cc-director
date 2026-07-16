// A cc-director is identified to the user by its machine name AND its Control API port - the port is
// what tells apart several Directors (slots) running on the SAME machine (e.g. two "SOREN_NORTH"
// entries). Both the roster's computer:port group headers and the New-session machine picker read the
// port out of the Director's controlEndpoint with this one helper, so they agree on how a port is
// derived from an endpoint like "http://127.0.0.1:7880" or "https://host.tailnet.ts.net:7880/".

// The port segment of a Director controlEndpoint, or "" when the endpoint carries none (an unusual
// default-port endpoint) or is empty. A pure string parse - no URL constructor, so it never throws on
// a partial/odd endpoint; it matches a trailing ":<digits>" that ends the authority (end of string or
// before the path slash), which is exactly the Control API endpoint shape the Gateway advertises.
export function directorPort(controlEndpoint: string | null | undefined): string {
  const raw = (controlEndpoint ?? "").trim();
  if (raw.length === 0) return "";
  const match = raw.match(/:(\d{2,5})(?=\/|$)/);
  return match ? match[1] : "";
}

// The human "computer:port" label for a Director - the roster group header and any compact Director
// reference. Falls back to the bare machine name only when the port is genuinely unknown (the endpoint
// carried none), never inventing one.
export function machinePortLabel(machineName: string, port: string): string {
  const name = (machineName ?? "").trim();
  const p = (port ?? "").trim();
  if (name.length === 0) return p.length > 0 ? `:${p}` : "";
  return p.length > 0 ? `${name}:${p}` : name;
}
