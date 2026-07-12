#!/usr/bin/env bash
#
# make-signing-certificate.sh — One-time setup: create a stable local
# code-signing certificate for CC Director builds on this Mac.
#
# Why this exists: macOS privacy permissions (the popups like
# "cc-director-mac3 would like to access files in your Desktop folder", and
# Full Disk Access grants) are keyed to the code signature of the binary.
# Our builds were ad-hoc signed, and an ad-hoc signature is just a hash of
# the binary — it changes on EVERY rebuild. So macOS forgot every "Allow"
# click at the next build and asked again, which stalls unattended Directors.
#
# This script creates a self-signed certificate named "CC Director Local
# Signing" in your login keychain and trusts it for code signing. Once it
# exists, scripts/local-build-mac.sh signs every slot binary with it (with a
# stable identifier per slot), so privacy grants survive rebuilds.
#
# Run it once per Mac. Re-running is safe (idempotent). The trust step needs
# an administrator password.
#
# After this script succeeds, rebuild the slots (only the ones whose Director
# is not running), then either:
#   • click "Allow" once per slot the next time a popup appears — it now
#     sticks across rebuilds, or
#   • grant the slot binaries Full Disk Access once in System Settings →
#     Privacy & Security → Full Disk Access (press the plus button, then
#     Command+Shift+G, go to <repo>/local_builds/mac, add each cc-director-mac
#     binary). Full Disk Access covers Desktop, Documents and Downloads with
#     no popups at all.
#
set -euo pipefail

IDENTITY_NAME="CC Director Local Signing"
KEYCHAIN="$HOME/Library/Keychains/login.keychain-db"

# Already present and valid for code signing? Nothing to do.
if security find-identity -v -p codesigning 2>/dev/null | grep -q "$IDENTITY_NAME"; then
    echo "Signing identity '$IDENTITY_NAME' already exists and is valid. Nothing to do."
    exit 0
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
CERT_PEM="$TMP/certificate.pem"

if security find-certificate -c "$IDENTITY_NAME" >/dev/null 2>&1; then
    # Created on an earlier run but the trust step did not complete — export
    # the existing certificate so we can (re)trust it below.
    echo "Certificate already in the keychain, but not yet trusted for code signing."
    security find-certificate -c "$IDENTITY_NAME" -p > "$CERT_PEM"
else
    echo "Creating certificate '$IDENTITY_NAME' (valid 10 years)..."
    # Use the system LibreSSL explicitly: a Homebrew OpenSSL 3 on the PATH
    # exports PKCS12 files in a newer format that `security import` rejects
    # ("MAC verification failed"). A configuration file (instead of
    # command-line extension flags) keeps the request portable too.
    OPENSSL="/usr/bin/openssl"
    cat > "$TMP/openssl.cnf" <<CNF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = $IDENTITY_NAME
[ext]
keyUsage = critical, digitalSignature
extendedKeyUsage = critical, codeSigning
basicConstraints = critical, CA:false
CNF
    "$OPENSSL" req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
        -config "$TMP/openssl.cnf" \
        -keyout "$TMP/key.pem" -out "$CERT_PEM" 2>/dev/null

    "$OPENSSL" pkcs12 -export -inkey "$TMP/key.pem" -in "$CERT_PEM" \
        -name "$IDENTITY_NAME" -out "$TMP/identity.p12" -passout pass:ccdirector

    # -A lets any application use this key without a per-use keychain prompt.
    # That is acceptable here: this is a machine-local development signing key
    # with no value outside this Mac, not a real Apple identity.
    security import "$TMP/identity.p12" -k "$KEYCHAIN" -P ccdirector -A
    echo "Imported into the login keychain."
fi

# Trust the certificate for code signing. This writes to the system trust
# store, so it needs administrator rights.
if sudo -n true 2>/dev/null || [[ -t 0 ]]; then
    echo "Trusting the certificate for code signing (your administrator password may be requested)..."
    sudo security add-trusted-cert -d -r trustRoot -p codeSign \
        -k /Library/Keychains/System.keychain "$CERT_PEM"
else
    # No terminal to ask for the password (for example when run by an agent).
    # Leave the certificate behind and tell the human exactly what to run.
    CERT_COPY="$HOME/.cc-director-signing-certificate.pem"
    cp "$CERT_PEM" "$CERT_COPY"
    echo ""
    echo "The certificate is created, but trusting it needs an administrator"
    echo "password, which cannot be asked for here. Run this yourself:"
    echo ""
    echo "  sudo security add-trusted-cert -d -r trustRoot -p codeSign -k /Library/Keychains/System.keychain \"$CERT_COPY\""
    echo ""
    echo "then re-run this script to confirm, and delete $CERT_COPY."
    exit 2
fi

if security find-identity -v -p codesigning 2>/dev/null | grep -q "$IDENTITY_NAME"; then
    echo ""
    echo "Done. '$IDENTITY_NAME' is ready."
    echo "Rebuild each slot (only while its Director is NOT running) with"
    echo "scripts/mac-rebuild.sh — from then on macOS privacy grants stick."
else
    echo "ERROR: the identity is still not valid for code signing after the trust step." >&2
    exit 1
fi
