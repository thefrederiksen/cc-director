# Headless Linux container image for the DevThrottle Gateway.
#
# This ships the cross-platform Gateway host (src/CcDirector.Gateway.Host), NOT the Windows tray skin
# (src/CcDirector.GatewayApp, which is net10.0-windows). The Gateway library is already net10.0 and
# framework-dependent; the tray is the only Windows-pinned project, and the container has no tray.
#
# The entry point is the headless container host (src/CcDirector.Gateway.Host), a thin executable that
# calls the SAME shared GatewayEntryPoint the dev console host (src/CcDirector.Gateway) runs - identical
# startup, identical arg forwarding - and additionally references the Postgres migrations assembly
# (CcDirector.Gateway.Migrations.Postgres) so that assembly ships in this image and Database.Migrate() can
# load it when the hosted Gateway runs on Postgres. CcDirector.Gateway itself cannot reference the
# migrations assembly (that would be a build cycle), which is exactly why this separate host exists. It is
# the same no-user-interface process, with the managed self-update loop and autostart both off, which is
# what a container wants (the container runtime keeps the process alive; no start-on-login, no self-update).
#
# The cockpit and mobile React apps are deliberately NOT built into this image (the npm build targets
# are skipped with the three Run*=false properties below). Those static assets are not needed to prove
# the warm brain spawns a Unix-pseudo-terminal agent, and the React build is already cross-platform, so
# skipping it hides no port risk. A full-asset image is a later concern.

# ---- build stage -------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the whole repository; `dotnet publish` on the host csproj restores only the projects it
# transitively references (CcDirector.Gateway and CcDirector.Gateway.Migrations.Postgres among them), so the
# unrelated trees are ignored by the build (and pruned by .dockerignore).
COPY . .

# Framework-dependent publish for linux-x64. The three Run*=false properties skip the npm-driven
# mobile build, cockpit build, and workspace typecheck, so the build stage needs no Node.js at all.
# Publishing the host (not CcDirector.Gateway directly) is what pulls the Postgres migrations assembly
# into /app/publish so Database.Migrate() can load it at runtime.
RUN dotnet publish src/CcDirector.Gateway.Host/CcDirector.Gateway.Host.csproj \
    -c Release -r linux-x64 --no-self-contained \
    -p:RunMobileBuild=false -p:RunCockpitBuild=false -p:RunWorkspaceTypecheck=false \
    -o /app/publish --nologo

# ---- runtime stage -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# The warm brain spawns a coding-agent command-line tool through a Unix pseudo-terminal. Install
# Node.js and the agent CLI so `POST /gateway/brain/restart` can actually spawn the agent process on
# Linux. Authentication for that agent is supplied at run time (an environment variable or a mounted
# credential) and is recorded in the QA document - never a silent fallback.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates gnupg \
    && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && npm install -g @anthropic-ai/claude-code \
    && apt-get purge -y gnupg \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

# Run as a non-root user. This is standard container hardening, and it is also required for the warm
# brain: the coding-agent command-line tool refuses to run with the skip-permissions flag under
# root/sudo, so the agent can only spawn as a normal user. HOME is set explicitly so the Gateway's
# local storage and the agent's own config land under the user's home directory.
RUN useradd --create-home --shell /bin/bash gateway
ENV HOME=/home/gateway
# Pin the Gateway's local storage to a writable path under the user's home. CcStorage honors
# CC_DIRECTOR_ROOT as its base directory; without it the base resolves relative to the read-only
# /app working directory and the non-root user cannot create it.
ENV CC_DIRECTOR_ROOT=/home/gateway/cc-director
USER gateway

# The Gateway binds to loopback (127.0.0.1:7878) by design - it is reached over the tunnel, not a public
# port. Exercise it from inside the container (docker exec ... curl 127.0.0.1:7878/...); this EXPOSE is
# documentation of the internal port, not a public bind.
EXPOSE 7878

ENTRYPOINT ["dotnet", "CcDirector.Gateway.Host.dll", "--port", "7878"]
