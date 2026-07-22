# Hosted Gateway CI deploy

`.github/workflows/deploy-hosted-gateway.yml` redeploys the hosted Gateway container to Azure App
Service (`devthrottle-gw`) automatically after **CI** goes green on `main` - the Gateway equivalent
of the website's Vercel auto-deploy. It only ever **redeploys** (rebuild image in ACR, repin the App
Service to the new immutable digest, restart, verify `/healthz`); it never provisions resources or
sets app settings. Full provisioning stays in `devthrottle_internal`
`docs/architecture/step3-azure-deploy/deploy.sh`, and the resource group, ACR, plan, storage mount and
`CC_GATEWAY_DB_CONNECTION` persist across deploys - so **this workflow stores no secrets**.

## Auth: OIDC, no stored secret

This is a PUBLIC repo, so no long-lived credential is stored. The workflow logs in to Azure with a
GitHub OIDC token (workload identity federation) as the `devthrottle-hosted-gateway` service principal
(appId `d809a2e9-6e0c-47d3-817f-551227f5eda0`), which already holds **Contributor** on the DevThrottle
subscription. Secrets are never exposed to fork pull requests, and the deploy only runs after a
push-triggered CI success on `main`, so untrusted code cannot reach the credential.

## One-time setup

Two of these three are already applied by the CI-wiring change; the **federated credential requires an
Azure AD admin** and must be run by hand.

### 1. Repo variables (non-secret identifiers) - already set

```
AZURE_GW_CLIENT_ID        = d809a2e9-6e0c-47d3-817f-551227f5eda0
AZURE_GW_TENANT_ID        = ab06f736-a43b-4764-aed6-e4f92addd9d8
AZURE_GW_SUBSCRIPTION_ID  = 8641a436-ec6f-471b-a3ed-04c92b76569c
```

### 2. Environment - already created

A `hosted-gateway-production` environment scopes the OIDC subject (and can carry required-reviewer
protection if you want a manual approval gate before each production deploy).

### 3. Federated credential (Azure AD admin - RUN THIS ONCE)

The service principal cannot add this to itself. As an Azure AD admin:

```
az ad app federated-credential create \
  --id d809a2e9-6e0c-47d3-817f-551227f5eda0 \
  --parameters '{
    "name": "github-devthrottle-hosted-gateway-production",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:thefrederiksen/devthrottle:environment:hosted-gateway-production",
    "audiences": ["api://AzureADTokenExchange"]
  }'
```

After that, every merge to `main` that passes CI redeploys the Gateway. Trigger a manual redeploy of
current `main` any time from the Actions tab (**Deploy hosted Gateway -> Run workflow**).

## Verifying

The workflow polls `GET https://devthrottle-gw.azurewebsites.net/healthz` for a `200` after restart
(the container reboots and runs EF migrations first, so a brief `502` window is normal). A failed
`/healthz` fails the job.
