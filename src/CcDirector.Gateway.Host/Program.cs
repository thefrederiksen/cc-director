using CcDirector.Gateway;

// The hosted container entry point (see CcDirector.Gateway.Host.csproj for why this executable exists at
// all). It runs the IDENTICAL Gateway startup as the local dev console host via the shared GatewayEntryPoint,
// forwarding its process args verbatim - including the --port the Dockerfile passes - so the CC_GATEWAY_HOSTED
// platform-port resolution and the --port fallback behave exactly as they do locally. The only difference
// between this executable and CcDirector.Gateway is that this one also references the Postgres migrations
// assembly, so that assembly ships in this image and Database.Migrate() can load it on Postgres.
return GatewayEntryPoint.Run(args);
