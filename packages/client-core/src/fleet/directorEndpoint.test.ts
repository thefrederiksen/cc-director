import { describe, it, expect } from "vitest";
import { directorPort, machinePortLabel } from "./directorEndpoint";

describe("directorPort", () => {
  it("reads the port from a loopback Control API endpoint", () => {
    expect(directorPort("http://127.0.0.1:7880")).toBe("7880");
  });

  it("reads the port when the endpoint has a trailing slash", () => {
    expect(directorPort("http://127.0.0.1:7885/")).toBe("7885");
  });

  it("reads the port from an https tailnet endpoint", () => {
    expect(directorPort("https://host.tailnet.ts.net:7880")).toBe("7880");
  });

  it("returns empty for an endpoint carrying no explicit port", () => {
    expect(directorPort("https://host.tailnet.ts.net")).toBe("");
  });

  it("returns empty for an empty or nullish endpoint", () => {
    expect(directorPort("")).toBe("");
    expect(directorPort(null)).toBe("");
    expect(directorPort(undefined)).toBe("");
  });

  it("does not mistake the scheme colon for a port", () => {
    expect(directorPort("http://localhost")).toBe("");
  });
});

describe("machinePortLabel", () => {
  it("joins machine and port with a colon", () => {
    expect(machinePortLabel("SOREN_NORTH", "7880")).toBe("SOREN_NORTH:7880");
  });

  it("shows the bare machine name when the port is unknown", () => {
    expect(machinePortLabel("SOREN_NORTH", "")).toBe("SOREN_NORTH");
  });

  it("shows just the port when the machine name is missing", () => {
    expect(machinePortLabel("", "7880")).toBe(":7880");
  });
});
