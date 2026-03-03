# Teams Remote Bot Setup Guide

This guide walks through setting up the Teams Remote Switchboard for CC Director.

## Overview

The Teams bot allows you to control CC Director sessions remotely via 1:1 Teams chat:
- List and select sessions
- Send prompts to Claude
- Get screenshots and terminal summaries
- Receive notifications when tasks complete

---

## Architecture

```
+------------------+      HTTPS       +----------------------+
|   Microsoft      | <--------------> |   Dev Tunnels Relay  |
|   Teams Client   |                  |   (Microsoft Cloud)  |
+------------------+                  +----------------------+
        |                                       |
        | Teams Bot Framework                   | In-process SDK
        v                                       v
+------------------+                  +----------------------+
|   Azure Bot      |                  |   CC Director        |
|   Service        |                  |   Dev Tunnels SDK    |
|                  |                  |   + Bot Server       |
+------------------+                  |   (ASP.NET Core)     |
        |                             +----------------------+
        | Routes to messaging endpoint          |
        | (your tunnel URL)                     v
        |                             +----------------------+
        +---------------------------> |   Session Manager    |
                                      |   Terminal Control   |
                                      +----------------------+
```

### How It Works

1. **You send a message** in Teams to your bot

2. **Teams routes to Azure Bot Service** using Bot Framework protocol

3. **Azure Bot Service forwards** to your configured messaging endpoint
   (e.g., `https://abc123-3978.usw2.devtunnels.ms/api/messages`)

4. **Dev Tunnels SDK** (running in-process inside CC Director) receives
   the HTTPS request through Microsoft's relay cloud

5. **CC Director's bot server** processes the message on localhost:3978
   and sends responses back through the same path

### Dev Tunnel Lifecycle

```
FIRST TIME CC DIRECTOR STARTS:
    CC Director automatically:
    1. Authenticates via Azure (DefaultAzureCredential)
    2. Creates a persistent tunnel via Dev Tunnels API
    3. Adds port 3978 with anonymous access
    4. Stores tunnel ID locally for reuse
    5. Connects the in-process tunnel host
    -> You get a permanent URL like:
       https://abc123-3978.usw2.devtunnels.ms

EVERY SUBSEQUENT START:
    CC Director automatically:
    1. Loads saved tunnel ID from disk
    2. Verifies tunnel still exists (recreates if deleted)
    3. Connects the in-process tunnel host
    -> Same URL as before

WHEN CC DIRECTOR STOPS:
    Tunnel host disconnects gracefully
    URL returns errors until next start
    Bot appears "unavailable" in Teams

WHEN CC DIRECTOR RESTARTS:
    Same URL works again
    No reconfiguration needed
```

### Key Characteristics

| Aspect | Behavior |
|--------|----------|
| Tunnel URL | Persistent - same URL forever (tied to your MS account) |
| Azure Bot config | One-time setup - endpoint URL never changes |
| Tunnel activation | Automatic - CC Director handles everything via SDK |
| Offline behavior | Bot appears unavailable when CC Director not running |
| Multiple PCs | Tunnel is tied to account, can only host from one PC at a time |
| Port | Always 3978 (standard Bot Framework port) |
| CLI dependency | None - Dev Tunnels SDK is bundled in the application |

### Why Dev Tunnels?

Teams/Azure Bot requires an **HTTPS endpoint** reachable from the internet.
Options:

1. **Public server** - Deploy bot to cloud (complex, costs money)
2. **ngrok** - Works but URL changes on each restart (reconfigure Azure Bot each time)
3. **Dev Tunnels SDK** - Persistent URL, free, bundled in-app, no external tools needed

Dev Tunnels give you a stable public URL that always points to your local machine
when the tunnel is active. Perfect for development and personal use.

---

## Prerequisites

- Azure subscription (free tier works)
- Microsoft 365 account with Teams
- Azure CLI logged in (`az login`) or Visual Studio signed in to Azure

---

## Step 1: Create Azure Bot Registration

1. Go to [Azure Portal](https://portal.azure.com)

2. Click **Create a resource** > Search for **Azure Bot** > **Create**

3. Fill in the basics:
   - **Bot handle**: `cc-director-bot` (or your preferred name)
   - **Subscription**: Select your subscription
   - **Resource group**: Create new or use existing
   - **Pricing tier**: F0 (Free) is sufficient
   - **Type of App**: Single Tenant
   - **Creation type**: Create new Microsoft App ID

4. Click **Review + create** > **Create**

5. Once created, go to the resource and note:
   - **Microsoft App ID** (on Overview page)

6. Go to **Configuration** > **Manage Password** > **New client secret**
   - Description: `cc-director-bot-secret`
   - Expires: 24 months (or your preference)
   - Click **Add**
   - **COPY THE SECRET VALUE NOW** - you won't see it again

7. Go to **Configuration** and set:
   - **Messaging endpoint**: (leave blank for now - you'll get the URL when CC Director first starts)

8. Click **Apply**

---

## Step 2: Enable Teams Channel

1. In your Azure Bot resource, go to **Channels**

2. Click **Microsoft Teams** icon

3. Accept the terms of service

4. Click **Apply**

The Teams channel is now enabled.

---

## Step 3: Configure CC Director

Edit `appsettings.json` in your CC Director folder:

```json
{
  "TeamsBot": {
    "Enabled": true,
    "MicrosoftAppId": "YOUR-APP-ID-FROM-STEP-1",
    "MicrosoftAppPassword": "YOUR-SECRET-FROM-STEP-1",
    "Port": 3978,
    "TunnelName": "cc-director-bot",
    "WhitelistPath": "%LOCALAPPDATA%/CcDirector/teams-whitelist.json",
    "NotificationQuiescenceMs": 3000
  }
}
```

---

## Step 4: First Launch and Tunnel URL

1. Start CC Director (with Teams bot enabled)

2. On first launch, the app will automatically:
   - Authenticate with Azure via DefaultAzureCredential
   - Create a persistent Dev Tunnel
   - Start hosting the tunnel

3. Check the log file for the tunnel URL:
   ```
   [DevTunnel] Public URL: https://xxx-3978.usw2.devtunnels.ms
   [TeamsRemote] Bot started, URL: https://xxx-3978.usw2.devtunnels.ms/api/messages
   ```

4. Copy this URL and go back to Azure Portal:
   - Open your Azure Bot resource > **Configuration**
   - Set **Messaging endpoint** to: `https://YOUR-TUNNEL-URL/api/messages`
   - Click **Apply**

This URL is persistent - you only need to set it once.

---

## Step 5: Get Your Teams User ID

1. In Teams, search for your bot by name (the Bot handle from Step 1)

2. Send any message to the bot

3. You'll see "Access denied" - this is expected

4. Check the file: `%LOCALAPPDATA%\CcDirector\teams-unknown-users.log`

   You'll see an entry like:
   ```
   [2024-01-15 10:30:00] UserId=29:1a2b3c4d-..., Name=Your Name, Message=hello
   ```

5. Copy the UserId value (the full `29:xxx...` string)

---

## Step 6: Add Yourself to Whitelist

Create/edit `%LOCALAPPDATA%\CcDirector\teams-whitelist.json`:

```json
{
  "AllowedUserIds": [
    "29:1a2b3c4d-your-full-user-id-here"
  ],
  "Comment": "Add Teams user IDs to allow access"
}
```

Restart CC Director or send `/reload` in Teams chat.

---

## Step 7: Test the Bot

In Teams, send these commands to your bot:

| Command | Description |
|---------|-------------|
| `/help` | Show available commands |
| `/ls` | List all sessions |
| `/new cc_director` | Create session for a repo |
| `/s a1b2` | Select session by ID prefix |
| `/snap` | Screenshot + terminal text |
| `/sum` | Terminal summary |
| `/kill` | Kill active session |
| (any text) | Send as prompt to session |

---

## Troubleshooting

### Bot doesn't respond

1. Check CC Director log for errors
2. Verify tunnel is connected (check log for `[DevTunnel] Tunnel host connected, IsRunning=True`)
3. Ensure messaging endpoint in Azure matches your tunnel URL

### "Access denied" message

1. Check `teams-unknown-users.log` for your user ID
2. Add the ID to `teams-whitelist.json`
3. Send `/reload` or restart CC Director

### Dev Tunnel authentication fails

1. Ensure you are logged in to Azure CLI: `az login`
2. Or ensure Visual Studio is signed in to an Azure account
3. The Dev Tunnels API requires an authenticated Microsoft account

### Screenshots don't work

The `/snap` command only works for the currently displayed session. If the session isn't active in the UI, switch to it first.

### Bot goes offline when PC sleeps

Dev Tunnel disconnects when your PC sleeps. The tunnel will automatically reconnect when CC Director is running and your PC wakes up.

---

## Security Notes

- **Whitelist**: Only user IDs in the whitelist can control sessions
- **Personal mode**: Bot is 1:1 chat only, not for team channels
- **Local network**: Bot endpoint only accessible via Dev Tunnel (not exposed locally)
- **Credentials**: Store App ID and Password securely; don't commit to git

---

## Quick Reference

| File | Purpose |
|------|---------|
| `appsettings.json` | Bot configuration (App ID, password, etc.) |
| `%LOCALAPPDATA%\cc-director\config\teams-tunnel-state.json` | Saved tunnel ID for reuse |
| `%LOCALAPPDATA%\CcDirector\teams-whitelist.json` | Allowed user IDs |
| `%LOCALAPPDATA%\CcDirector\teams-unknown-users.log` | Rejected user attempts |
| `%LOCALAPPDATA%\CcDirector\logs\director-*.log` | CC Director logs |

---

## Commands Reference

| Command | Description |
|---------|-------------|
| `/ls` or `/list` | List all active sessions with status |
| `/s <id>` | Select session by ID prefix (e.g., `/s a1b2`) |
| `/new <repo>` | Create new session for repository |
| `/snap` | Screenshot + last 50 lines of terminal |
| `/sum` | Last 100 lines of terminal text |
| `/kill [id]` | Kill session (active or by ID) |
| `/reload` | Reload whitelist from disk |
| `/help` | Show help message |
| (plain text) | Send as input to active session |

Status icons in `/ls`:
- `[OK]` - Ready for input
- `[..]` - Working
- `[!]` - Waiting for permission
- `[X]` - Exited
