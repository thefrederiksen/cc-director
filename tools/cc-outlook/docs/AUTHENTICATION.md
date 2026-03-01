# cc-outlook Authentication Guide

## Overview

cc-outlook uses **MSAL Device Code Flow** for authentication with Microsoft 365 / Outlook accounts. This is Microsoft's recommended authentication method for CLI applications.

## Why Device Code Flow?

Browser-based OAuth redirect flows have issues in CLI applications:
- OAuth state mismatches when SSO sessions exist in the browser
- Redirect URI handling problems
- "wrongplace" redirect errors
- MFA complications

Device Code Flow solves all these by separating browser authentication from the CLI.

## How It Works

1. Run `cc-outlook auth`
2. CLI displays a code and URL
3. Open https://microsoft.com/devicelogin in any browser
4. Enter the code
5. Sign in with your Microsoft account
6. Complete MFA if prompted
7. Return to terminal - authentication completes automatically

---

## Setup

### Step 1: Create Azure App Registration

1. Go to [Azure Portal](https://portal.azure.com) -> Azure Active Directory -> App registrations
2. Click "New registration"
3. Configure:
   - **Name:** `cc-outlook_cli` (or your preferred name)
   - **Supported account types:** "Accounts in any organizational directory and personal Microsoft accounts"
   - **Redirect URI:**
     - Platform: "Mobile and desktop applications"
     - URI: `https://login.microsoftonline.com/common/oauth2/nativeclient`
4. Click "Register"
5. Copy the **Application (client) ID** - you'll need this

### Step 2: Enable Public Client Flow (CRITICAL)

This setting is **required** for Device Code Flow:

1. In your app registration, go to "Authentication"
2. Scroll to "Advanced settings"
3. Set "Allow public client flows" to **Yes**
4. Click "Save"

Without this, you'll get: `Failed to create device flow`

### Step 3: Configure API Permissions

1. Go to "API permissions"
2. Click "Add a permission" -> "Microsoft Graph" -> "Delegated permissions"
3. Add these permissions:

| Permission | Purpose |
|------------|---------|
| `Mail.ReadWrite` | Read and write emails |
| `Mail.Send` | Send emails |
| `Calendars.ReadWrite` | Read and write calendar events |
| `User.Read` | Read user profile |
| `MailboxSettings.Read` | Read mailbox settings |

4. Click "Add permissions"

These are delegated permissions and don't require admin consent.

### Step 4: Add Account to cc-outlook

```bash
cc-outlook accounts add your.email@domain.com --client-id YOUR_CLIENT_ID
```

### Step 5: Authenticate

```bash
cc-outlook auth
```

You'll see:
```
============================================================
DEVICE CODE AUTHENTICATION
============================================================

To sign in, use a web browser to open the page https://microsoft.com/devicelogin
and enter the code XXXXXXXXX to authenticate.

============================================================
```

1. Go to https://microsoft.com/devicelogin
2. Enter the code
3. Sign in and accept permissions
4. Terminal shows "Authenticated as: your.email@domain.com"

---

## Commands

### Authentication

```bash
cc-outlook auth                # Authenticate (uses cached token if valid)
cc-outlook auth --force        # Force re-authentication
cc-outlook profile             # Show current account info
cc-outlook accounts list       # List all configured accounts
```

### Email

```bash
cc-outlook list                # List inbox (default 10)
cc-outlook list -n 25          # List 25 messages
cc-outlook list --unread       # Unread only
cc-outlook read MESSAGE_ID     # Read specific email
cc-outlook search "keyword"    # Search emails
cc-outlook send -t "to@email.com" -s "Subject" -b "Body"
cc-outlook draft -t "to@email.com" -s "Subject" -b "Body"
```

### Calendar

```bash
cc-outlook calendar events           # Next 7 days
cc-outlook calendar events -d 14     # Next 14 days
```

---

## Token Storage

Tokens are stored locally:
- **Windows:** `%USERPROFILE%\.cc-outlook\tokens\`
- **Linux/Mac:** `~/.cc-outlook/tokens/`

Token files contain:
- Access tokens (expire in ~1 hour, auto-refreshed by MSAL)
- Refresh tokens (expire after ~90 days of inactivity)

---

## Troubleshooting

### "Failed to create device flow"

**Cause:** Azure app doesn't have public client flow enabled.

**Fix:** Azure Portal -> App registrations -> your app -> Authentication -> "Allow public client flows" = **Yes**

### Device code expired

Codes are valid for ~15 minutes.

**Fix:** Run `cc-outlook auth` again for a fresh code.

### "Token is expired" or "Oauth Token is expired"

Refresh token expired (90+ days without use).

**Fix:** `cc-outlook auth --force`

### MFA prompt appears

This is normal. Complete MFA in the browser. The CLI detects completion automatically.

---

## Technical Details

### MSAL Integration

cc-outlook uses Microsoft's MSAL (Microsoft Authentication Library):

- `acquire_token_silent()` handles automatic token refresh
- `SerializableTokenCache` persists tokens between sessions
- Device Code Flow handles initial authentication

### Token Backend

The `MSALTokenBackend` class bridges MSAL tokens to the O365 Python library by overriding key methods:

| Method | Purpose |
|--------|---------|
| `load_token()` | Gets fresh token from MSAL |
| `token_is_expired()` | Returns False (MSAL handles expiration) |
| `should_refresh_token()` | Returns False (prevents O365 refresh) |
| `get_access_token()` | Returns `{'secret': token}` format |

This ensures all token refresh is handled by MSAL, not O365's internal mechanisms.

### Reserved Scopes

Do **not** include these in your scopes list - MSAL handles them automatically:
- `offline_access`
- `openid`
- `profile`

Including them causes: `Configuration error: reserved scope`

---

## Azure App Settings Summary

| Setting | Value |
|---------|-------|
| Supported account types | Accounts in any organizational directory and personal Microsoft accounts |
| Redirect URI (Mobile/desktop) | `https://login.microsoftonline.com/common/oauth2/nativeclient` |
| Allow public client flows | **Yes** |

### Required Permissions (Delegated)

- `Mail.ReadWrite`
- `Mail.Send`
- `Calendars.ReadWrite`
- `User.Read`
- `MailboxSettings.Read`
