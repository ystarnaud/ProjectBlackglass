#!/usr/bin/env bash
# Creates the development token file for the networked server if it is missing (networking experiment, decision 046).
# Usage: make-dev-tokens.sh [--force]    File: $BLACKGLASS_TOKENS (default /opt/blackglass/dev-tokens.txt), mode 600.
# account-a may drive the development fixture (decision 047); only a fixture build started with NET_FIXTURE=1 accepts it.
# The tokens are random fixtures and are never printed. Read them with: ssh <host> cat /opt/blackglass/dev-tokens.txt
set -eu
tokens="${BLACKGLASS_TOKENS:-/opt/blackglass/dev-tokens.txt}"
if [ -f "$tokens" ] && [ "${1:-}" != "--force" ]; then
  echo "token file exists: $tokens (use --force to replace it)"
  exit 0
fi
mkdir -p "$(dirname "$tokens")"
newtoken() { head -c 24 /dev/urandom | base64 | tr -d '/+=\n'; }
umask 077
{
  echo "# Development tokens (networking experiment). Format: account token squad|none[+fixture] [attempt]"
  echo "account-a $(newtoken) squad+fixture"
  echo "account-b $(newtoken) none"
  echo "account-ticket $(newtoken) none other-attempt"
} > "$tokens"
chmod 600 "$tokens"
echo "wrote $tokens (3 accounts)"
