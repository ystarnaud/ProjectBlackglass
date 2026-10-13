#!/usr/bin/env bash
# Creates a development token file for the networked server if it is missing (networking experiment, decisions 046 and 047).
# Usage: make-dev-tokens.sh [--fixture] [--force]    (the flags may come in either order)
#   (none)     the RELEASE file: $BLACKGLASS_TOKENS (default /opt/blackglass/dev-tokens.txt). No fixture identity anywhere.
#   --fixture  the FIXTURE file: $BLACKGLASS_FIXTURE_TOKENS (default /opt/blackglass/dev-tokens-fixture.txt). Freshly generated
#              tokens, never the release ones; account-a carries squad+fixture. Only a fixture build started with
#              NET_FIXTURE=1 is given this file (run-net.sh), so a release server never reads a +fixture identity.
#   --force    replace the file if it exists (each file separately).
# Mode 600. The tokens are random fixtures and are never printed. Read them with: ssh <host> cat <file>
set -eu
fixture=0
force=0
for arg in "$@"; do
  case "$arg" in
    --fixture) fixture=1 ;;
    --force) force=1 ;;
    *) echo "usage: make-dev-tokens.sh [--fixture] [--force]" >&2; exit 2 ;;
  esac
done
if [ "$fixture" = 1 ]; then
  tokens="${BLACKGLASS_FIXTURE_TOKENS:-/opt/blackglass/dev-tokens-fixture.txt}"
  squad="squad+fixture"
else
  tokens="${BLACKGLASS_TOKENS:-/opt/blackglass/dev-tokens.txt}"
  squad="squad"
fi
if [ "$fixture" != 1 ] && [ -f "$tokens" ] && grep -q '+fixture' "$tokens"; then
  echo "release token file $tokens carried a fixture identity; replacing it with fresh release tokens"
  force=1
fi
if [ -f "$tokens" ] && [ "$force" != 1 ]; then
  echo "token file exists: $tokens (use --force to replace it)"
  exit 0
fi
mkdir -p "$(dirname "$tokens")"
newtoken() { head -c 24 /dev/urandom | base64 | tr -d '/+=\n'; }
umask 077
format="squad|none [attempt]"
if [ "$fixture" = 1 ]; then format="squad|none[+fixture] [attempt]"; fi
{
  echo "# Development tokens (networking experiment). Format: account token $format"
  echo "account-a $(newtoken) $squad"
  echo "account-b $(newtoken) none"
  echo "account-ticket $(newtoken) none other-attempt"
} > "$tokens"
chmod 600 "$tokens"
echo "wrote $tokens (3 accounts)"
