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

- **This workflow is the ONLY way to update the live Gateway.** Do not pin an
  image, restart the site, change app settings, or touch the staging slot by hand
  with `az`, and do not do it in the Azure Portal. Those paths skip the warmed
  hand-off, the outage measurement and the refusals below, and a hand-driven
  change is how you get an unmeasured outage on live sessions. If this workflow
  cannot do what is needed, that is a gap to fix in the workflow, not to work
  around. The emergency swap-back is also a workflow
  (`rollback-hosted-gateway.yml`) - use it rather than swapping by hand.
- **A person authorizes the go-live.** Starting this deploy pushes new code to
  the live service. Get an explicit go from the human before you start it. Do not
  start it on your own initiative.
- **It will only ship green main.** The run refuses, in seconds and before it
  touches anything, if it was started against any ref other than `refs/heads/main`,
  or if the commit being shipped has a check that failed or has not finished. If it
  refuses for a pending check, wait for continuous integration and start it again. This is the one
  wait the no-waiting rule (CLAUDE.md 5a) does not cover, and it is not our policy: 5a governs
  whether a MERGE is held open, while this is the deploy workflow's own refusal to push unverified
  code to a live service. Since every merge to main now gets its own run that cannot be evicted,
  that check will actually finish - before, it could be cancelled and then never go green at all.
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

**A blip here is NOT normal, and must not be waved through.** This used to say a
minute or two of `502`/`000` was expected cold-start behaviour and told you not to
report it. That was wrong, and it trained people to accept the exact failure that
took the live service down for 38.5 seconds on 2 August 2026 (issue #2383).

The deploy is a **warmed slot swap**: the old instance keeps serving until the new
one is proven healthy, so a healthy deploy has **no user-visible gap at all**.
Three consecutive deploys measured `Longest unavailable stretch: 0.0s` over a
ten-minute watch. There is no cold start on the user path, because the user is
never moved onto a cold instance.

So if `/healthz` does not answer `200` immediately after the run goes green,
something went wrong with the hand-off. Say so; do not wait it out. The run itself
now fails if the external outage exceeds five seconds, so a green run already
means production stayed up.

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
