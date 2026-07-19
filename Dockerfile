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
# The cockpit and mobile React apps ARE built into this image. They used to be skipped (with three
# Run*=false properties on the publish command) back when this image only had to prove the warm brain
# spawns a Unix-pseudo-terminal agent. That is obsolete: the hosted Gateway is the product a customer
# reaches through a browser, and without those assets it has no browser user interface at all - the
# cockpit shell answers 404 ("React Cockpit not built into this Gateway") and the mobile app answers
# 404 as well. So the publish below leaves the three properties alone; a Release configuration turns
# the cockpit build, the mobile build and the strict workspace typecheck on by default (see
# src/CcDirector.Gateway/CcDirector.Gateway.csproj), the built files are staged into the Gateway's
# wwwroot/c and wwwroot/m, and they flow into /app/publish - which is where CockpitReactApp.WebRoot
# (AppContext.BaseDirectory/wwwroot/c) and the mobile app's static files are read from at run time.
# Because those npm targets now run, the BUILD stage needs Node.js; it is installed there.
#
# HOW TO BUILD THIS IMAGE (the commit argument is required, see COCKPIT_COMMIT below):
#
#   docker build --build-arg COCKPIT_COMMIT=$(git rev-parse --short HEAD) -t devthrottle-gateway .
#

# ---- build stage -------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Node.js is a BUILD-stage requirement now that the cockpit and mobile npm targets run: the MSBuild
# targets BuildCockpitApp, BuildMobileApp and TypecheckWorkspaces all shell out to npm at the
# workspace root. Node 20 matches what the repository builds with elsewhere. This is the build stage
# only - it is discarded, and the runtime stage installs its own Node for its own reason.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates gnupg \
    && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/* \
    && node --version && npm --version

# Copy the whole repository; `dotnet publish` on the host csproj restores only the projects it
# transitively references (CcDirector.Gateway and CcDirector.Gateway.Migrations.Postgres among them), so the
# unrelated trees are ignored by the build (and pruned by .dockerignore).
COPY . .

# Framework-dependent, runtime-identifier-agnostic publish (the entry point is `dotnet <dll>`, so the
# image never needs a native apphost, and the runtime resolves the linux-x64 native assets out of the
# published runtimes/ folder). It used to pass `-r linux-x64 --no-self-contained`. Both had to go now
# that the npm targets run: those flags set global properties that make MSBuild evaluate
# CcDirector.Gateway as TWO project instances, and BuildCockpitApp then runs twice. The cockpit
# stamps its build TIME into the bundle, so the second run deletes wwwroot/c and re-emits the asset
# under a DIFFERENT content hash - after the publish has already recorded the first run's file names.
# The build then dies with MSB3030 "could not copy ... because it was not found". Measured: with the
# flags, two "[BuildCockpitApp] staged" lines and MSB3030; without them, one line and a clean publish.
#
# The three Run*=true properties run the npm-driven mobile build, cockpit build and workspace
# typecheck. Release already turns them on by default; they are stated here so the image's intent is
# readable at the publish command rather than inferred from a configuration name. They are what put
# wwwroot/c and wwwroot/m into /app/publish.
#
# Publishing the host (not CcDirector.Gateway directly) is what pulls the Postgres migrations assembly
# into /app/publish so Database.Migrate() can load it at runtime.
#
# -m:1 (one MSBuild worker) keeps the npm work serialized. Every npm target drives the ONE shared
# node_modules at the workspace root, and two `npm ci` calls running at once delete and re-link the
# same directory - observed here as "Cannot find module esbuild/install.js" and as "EEXIST symlink
# @devthrottle/mobile" while the graph still evaluated the Gateway twice. Nothing about this build is
# slow enough to be worth trading that risk for parallel workers.
#
# COCKPIT_COMMIT is required. The cockpit stamps the repository commit into its About page, and it
# normally reads that from git - which cannot work in here: the build context excludes .git, and a
# build started from a git worktree has only a pointer file there in any case. So the commit is
# handed in. There is deliberately no default: a cockpit that reports a commit it does not actually
# contain is worse than a build that stops and tells you to pass the argument.
ARG COCKPIT_COMMIT
ENV COCKPIT_COMMIT=${COCKPIT_COMMIT}
RUN test -n "$COCKPIT_COMMIT" || { \
      echo "COCKPIT_COMMIT is required. Build with:"; \
      echo "  docker build --build-arg COCKPIT_COMMIT=\$(git rev-parse --short HEAD) -t devthrottle-gateway ."; \
      exit 1; }

RUN dotnet publish src/CcDirector.Gateway.Host/CcDirector.Gateway.Host.csproj \
    -c Release -m:1 \
    -p:RunMobileBuild=true -p:RunCockpitBuild=true -p:RunWorkspaceTypecheck=true \
    -o /app/publish --nologo \
    && test -f /app/publish/wwwroot/c/index.html \
    && test -f /app/publish/wwwroot/m/index.html \
    && echo "[image] cockpit and mobile assets are present in the published output"

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
