import type { NetworkDiag } from "../api/client";

// The shape of the Gateway's finished status-pill verdict. Shared by the phone and Cockpit; clients render
// it verbatim and contain no connection-quality ruling of their own.

export type NetStatus = NetworkDiag["connectionVerdict"];
export type NetStatusLevel = NetStatus["level"];
