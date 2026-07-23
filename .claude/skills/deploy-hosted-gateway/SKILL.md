---
name: deploy-hosted-gateway
description: Release the live hosted Gateway (the cloud service at devthrottle-gw.azurewebsites.net) by starting its GitHub deploy, watching it finish, and confirming the live service comes back healthy. This is the CLOUD Gateway, not the downloadable desktop app. Triggers on "release the gateway", "deploy the gateway", "push the gateway live", "update the hosted gateway", "redeploy the gateway".
---

# Deploy the hosted Gateway

Updates the live cloud Gateway that DevThrottle clients talk to. It rebuilds the
Gateway container and pushes it live to Azure, then confirms the service is
healthy again.

This is NOT the desktop-app release. Cutting a version and building the
downloadable `cc-director.exe` is a different job - use the `release-manager`
skill for that. This skill only redeploys the cloud service; it never touches
version numbers, release notes, tags, or the mailing list.

## What you must know first

- **A person authorizes the go-live.** Starting this deploy pushes new code to
  the live service. Get an explicit go from the human before you start it. Do not
  start it on your own initiative.
- **It deploys whatever commit you start it against.** By default that is the
  current tip of `main`. There is no separate "staging" - main goes straight to
  the live service when someone runs this.
- **It does not set up any infrastructure.** The Azure resource group, container
  registry, database connection, and storage are already provisioned and persist
  across deploys. This deploy only: rebuild the image, point the live service at
  the new image, restart, verify health. It needs no stored secrets - it signs in
  to Azure through a trust that is already configured.

## Steps

### 1. Confirm the human's go

Do not proceed without an explicit "yes, deploy" from the human for this specific
release.

### 2. Start the release

One command starts it. It runs against `main` unless told otherwise:

```
gh workflow run deploy-hosted-gateway.yml --repo thefrederiksen/devthrottle --ref main
```

A human can do the same thing by hand: GitHub -> Actions -> "Deploy hosted
Gateway" -> "Run workflow". Both are the same trigger.

### 3. Watch it run to the end

Find the run that just started and watch it. The whole thing takes roughly five
minutes, because it builds the container in Azure.

```
gh run list --repo thefrederiksen/devthrottle --workflow deploy-hosted-gateway.yml -L 1
gh run watch <run-id> --repo thefrederiksen/devthrottle --interval 30 --exit-status
```

The run itself finishes with its own health check, so a green run already means it
came back up. If the run fails at "Azure login", the deploy trust is broken - see
Troubleshooting.

### 4. Confirm the live service is healthy

```
curl -s -m 20 -o /dev/null -w "HTTP %{http_code}\n" https://devthrottle-gw.azurewebsites.net/healthz
```

A `200` means the live Gateway is up.

**Expected gotcha - a brief blip right after restart.** For a minute or two after
the restart, this check can return `502` or no response (`000`) while the fresh
container boots and runs its database migrations. This is normal cold-start
behavior, NOT a failed deploy. Do not panic and do not report failure. Poll every
15 seconds until it returns `200`:

```
for i in $(seq 1 12); do
  code=$(curl -s -m 20 -o /dev/null -w "%{http_code}" https://devthrottle-gw.azurewebsites.net/healthz)
  echo "attempt $i: HTTP $code"
  [ "$code" = "200" ] && { echo RECOVERED; break; }
  sleep 15
done
```

If it is still not `200` after a few minutes, then it is a real problem - see
Troubleshooting.

### 5. Report plainly

Tell the human, in plain language, that the live Gateway was updated and is
healthy. Describe what changed, not run numbers.

## Troubleshooting

- **Run fails in about 20 seconds at "Azure login (OIDC)".** The Azure trust that
  lets GitHub deploy is missing or wrong. The deploy signs in as an Azure app
  registration that must carry a federated credential whose subject is
  `repo:thefrederiksen/devthrottle:environment:hosted-gateway-production`. Fixing
  this needs Azure access in the tenant that owns that app registration; bring the
  human in. Full context: the deploy workflow's own header comments in
  `.github/workflows/deploy-hosted-gateway.yml`, and the provisioning runbook at
  `docs/architecture/step3-azure-deploy` in the `devthrottle_internal` repository.
- **Health check never reaches 200 after several minutes.** The container is
  failing to start. Read the App Service logs in Azure (the human, or an agent
  authenticated to the Gateway subscription, can pull them). A failed database
  migration on startup is the usual cause.

## What this skill does not do

- It does not release the desktop app (`release-manager`).
- It does not provision or change Azure infrastructure or app settings.
- It does not auto-deploy on a commit. The live Gateway updates only when a person
  deliberately runs this. Committing to main never touches the live service.
