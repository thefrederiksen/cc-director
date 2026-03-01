# cc-outlook MSAL Device Code Flow Implementation

## Overview

This document describes the authentication implementation for cc-outlook using MSAL (Microsoft Authentication Library) with Device Code Flow.

---

## The Problem

The O365 library's browser-based OAuth with redirect URIs consistently failed with:

1. **"wrongplace" redirect errors** - OAuth state mismatch when browser has cached SSO sessions
2. **State mismatch issues** - Browser automation made it worse
3. **MFA complications** - Multi-factor auth added redirect complexity

Browser redirect OAuth is fundamentally unreliable for CLI applications.

---

## The Solution: Device Code Flow

Device Code Flow is Microsoft's recommended authentication method for CLI/desktop apps.

### How It Works

1. CLI requests a device code from Microsoft
2. User sees: "Go to https://microsoft.com/devicelogin and enter code XXXXXXXX"
3. User opens browser manually, enters code, signs in
4. CLI polls Microsoft until authentication completes
5. Token is returned to CLI

### Why It Works

- **No redirect URI involved** - eliminates all redirect issues
- **No OAuth state to mismatch** - state is managed by device code
- **Works with MFA** - user completes MFA in browser, CLI just waits
- **Browser independence** - user can even use a different device

---

## Implementation Details

### Files

| File | Purpose |
|------|---------|
| `src/auth.py` | Core authentication logic with MSAL |
| `src/cli.py` | CLI command updates |
| `requirements.txt` | Added `msal>=1.20.0` |
| `cc-outlook.spec` | PyInstaller config |

### MSALTokenBackend Class

Custom token backend that bridges MSAL tokens to O365 library.

```python
class MSALTokenBackend(BaseTokenBackend):
```

**Purpose:** O365 library expects a token backend. This class:
- Loads tokens from MSAL's SerializableTokenCache
- Converts MSAL token format to O365 expected format
- Handles automatic token refresh via `acquire_token_silent()`
- Intercepts O365's internal token checking

**Token Storage:**
- Path: `~/.cc-outlook/tokens/{email}_msal.json`
- Format: MSAL's SerializableTokenCache JSON (~13KB)

### Critical Method Overrides

| Method | Purpose |
|--------|---------|
| `load_token()` | Returns token dict with access_token from MSAL |
| `token_is_expired()` | Always returns False - MSAL handles expiration |
| `should_refresh_token()` | Always returns False - prevents O365 refresh attempts |
| `get_access_token()` | Returns `{'secret': access_token}` - format O365 expects |
| `check_token()` | Calls load_token() to verify MSAL can provide token |

**Why these overrides are necessary:**
- O365's `Connection.get_session()` expects `get_access_token()` to return a dict with `'secret'` key
- O365's internal caching checks `token_is_expired()` before API calls
- Without these overrides, O365 tries to refresh using its own mechanism, which fails

### The is_authenticated Workaround

```python
if account.is_authenticated:
    return account
else:
    # Even if O365 says not authenticated, if we have a token, try anyway
    if result and 'access_token' in result:
        return account
```

**Why this exists:**
- O365's `Account.is_authenticated` checks token format
- MSAL tokens have a different format than O365 expects
- `is_authenticated` returns `False` even with valid MSAL tokens
- BUT: Actual API calls work because they go through `load_token()` which returns correct format

---

## Token Flow Diagram

```
User runs: cc-outlook auth
              |
              v
    +-------------------+
    | Check for cached  |
    | token             |
    +-------------------+
              |
     [token exists?]
        /         \
      Yes          No
       |            |
       v            v
+------------+  +------------------+
| Try silent |  | Device Code Flow |
| refresh    |  |                  |
+------------+  +------------------+
       |                |
  [success?]            |
    /    \              |
  Yes     No            |
   |       \           /
   |        \         /
   v         v       v
+------+  +-------------------+
|Return|  | Display code      |
|token |  | User goes to URL  |
+------+  | Enters code       |
          | Signs in + MFA    |
          +-------------------+
                   |
                   v
          +-------------------+
          | Poll for token    |
          | Save to cache     |
          +-------------------+
                   |
                   v
          +-------------------+
          | Create O365 Acct  |
          | with MSAL backend |
          +-------------------+
                   |
                   v
          +-------------------+
          | is_authenticated  |
          | = False (ignore!) |
          +-------------------+
                   |
                   v
          +-------------------+
          | Return account    |
          | (it works anyway) |
          +-------------------+
```

---

## Azure App Configuration

### Required Settings

| Setting | Value |
|---------|-------|
| Account Types | Accounts in any organizational directory and personal Microsoft accounts |
| Redirect URI | `https://login.microsoftonline.com/common/oauth2/nativeclient` (Mobile/desktop) |
| Allow public client flows | **Yes** (CRITICAL) |

### Required API Permissions (Delegated)

- `Mail.ReadWrite`
- `Mail.Send`
- `Calendars.ReadWrite`
- `User.Read`
- `MailboxSettings.Read`

---

## Reserved Scopes

MSAL automatically handles certain scopes. Do NOT include these in your scopes list:
- `offline_access`
- `openid`
- `profile`

Including them causes: `Configuration error: You cannot use any scope value that is reserved`

---

## Troubleshooting

### "Failed to create device flow"

**Cause:** Azure app doesn't have public client flow enabled.

**Fix:** Azure Portal -> App registrations -> your app -> Authentication -> "Allow public client flows" = Yes

### Device code expired

**Cause:** Code is valid ~15 minutes.

**Fix:** Run `cc-outlook auth` again for fresh code.

### Token expired after ~90 days

**Cause:** Refresh token expired due to inactivity.

**Fix:** `cc-outlook auth --force`

### "string indices must be integers" error

**Cause:** `get_access_token()` returned a string instead of dict.

**Fix:** Must return `{'secret': access_token_string}`

### MFA screen appears

**This is normal.** Complete MFA in browser. CLI will detect completion automatically.

---

## Build

```bash
cd src/cc-outlook
./build.ps1   # Windows
./build.sh    # Linux/Mac
```

### PyInstaller Note

The `cc-outlook.spec` includes:
```python
datas=collect_data_files('rich'),
hiddenimports=[...] + collect_submodules('rich._unicode_data'),
```

Required because rich library has unicode data files that PyInstaller doesn't find automatically.

Also add `flush=True` to print statements for immediate output in PyInstaller builds:
```python
print(flow["message"], flush=True)
```

---

## File Locations

| Item | Path |
|------|------|
| Config dir | `~/.cc-outlook/` |
| Profiles | `~/.cc-outlook/profiles.json` |
| Token cache | `~/.cc-outlook/tokens/{email}_msal.json` |
