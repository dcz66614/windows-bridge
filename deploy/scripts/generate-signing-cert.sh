#!/usr/bin/env bash
# Generate the OpenIddict signing PFX for the relay.
# This certificate signs TOKENS, not TLS (Caddy terminates TLS
# with its own Let's Encrypt certificate). A self-signed PFX is
# correct here: it is a signing key, not a browser-trusted cert.
# Usage: ./generate-signing-cert.sh [output-path] [password]
# The password should come from the environment or a prompt, not
# from the command line history. Back the PFX up offline.
set -euo pipefail

OUT="${1:-./secrets/signing.pfx}"
if [ -z "${SIGNING_CERT_PASSWORD:-}" ]; then
  read -rsp "PFX password: " SIGNING_CERT_PASSWORD
  echo
fi
mkdir -p "$(dirname "$OUT")"
# 2048-bit RSA, 10-year validity, no CA extensions needed.
openssl req -x509 -newkey rsa:2048 -keyout /tmp/bridge-signing.key \
  -out /tmp/bridge-signing.crt -days 3650 -nodes \
  -subj "/CN=bridge-relay-signing"
openssl pkcs12 -export -out "$OUT" \
  -inkey /tmp/bridge-signing.key -in /tmp/bridge-signing.crt \
  -passout "pass:$SIGNING_CERT_PASSWORD"
rm -f /tmp/bridge-signing.key /tmp/bridge-signing.crt
chmod 600 "$OUT"
echo "Wrote $OUT (600). Back it up offline — losing it invalidates every token."
