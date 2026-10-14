#!/usr/bin/env bash
# Copies the committed Backend/ tree at HEAD to <vm>:/opt/blackglass/backend/src (replacing it) and records the commit,
# so every VM transcript names exactly what it ran. Uncommitted changes are not copied.
set -euo pipefail
alias="${BLACKGLASS_SERVICES_HOST:-bg-dev-services01-admin}"
repo="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
commit="$(git -C "$repo" rev-parse --short HEAD)"
if [[ -n "$(git -C "$repo" status --porcelain -- Backend)" ]]; then
  echo "warning: Backend/ has uncommitted changes; the VM gets HEAD ($commit) only" >&2
fi
git -C "$repo" archive --format=tar HEAD Backend | ssh "$alias" "sudo -n sh -c 'set -e
  rm -rf /opt/blackglass/backend/src.new && mkdir -p /opt/blackglass/backend/src.new
  tar -x -C /opt/blackglass/backend/src.new
  echo $commit > /opt/blackglass/backend/src.new/COMMIT
  rm -rf /opt/blackglass/backend/src && mv /opt/blackglass/backend/src.new /opt/blackglass/backend/src'"
echo "synced $commit to $alias:/opt/blackglass/backend/src"
