using CcDirector.Gateway;

// The IMMUTABLE hosted identity. This executable is the ONLY one that ships in the hosted container image,
// so stamping the marker here - at compile time, onto the artifact itself - makes "am I the hosted build"
// impossible to drop with a slot swap, a config restore, or a lost environment variable. The shared
// GatewayEntryPoint reads it (GatewayHostedMode.IsHostedImage) and refuses to start unless the full hosted
// contract holds. The dev console host (CcDirector.Gateway) and the Windows tray skin do NOT apply it, so
// they stay self-host.
[assembly: HostedGatewayImage]

// The hosted container entry point (see CcDirector.Gateway.Host.csproj for why this executable exists at
// all). It runs the IDENTICAL Gateway startup as the local dev console host via the shared GatewayEntryPoint,
// forwarding its process args verbatim - including the --port the Dockerfile passes - so the CC_GATEWAY_HOSTED
// platform-port resolution and the --port fallback behave exactly as they do locally. The only difference
// between this executable and CcDirector.Gateway is that this one also references the Postgres migrations
// assembly, so that assembly ships in this image and Database.Migrate() can load it on Postgres.
return GatewayEntryPoint.Run(args);
