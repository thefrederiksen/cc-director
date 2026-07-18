using CcDirector.Gateway;

// The local dev console host (Ctrl+C to stop). The Gateway startup logic lives in the shared
// GatewayEntryPoint so the hosted container host (CcDirector.Gateway.Host) runs byte-identical code; this
// executable just forwards its process args - including --port - straight through. This project runs on
// SQLite locally and does NOT reference the Postgres migrations assembly; only the hosted host does.
return GatewayEntryPoint.Run(args);
