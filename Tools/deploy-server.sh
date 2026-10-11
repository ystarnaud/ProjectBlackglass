#!/usr/bin/env bash
# Uploads a packaged server build and swaps it into /opt/blackglass/server/ on the VM (Checkpoint A).
# Usage: Tools/deploy-server.sh [host] [archive]   (defaults: bg-dev-mission01, the newest Builds/blackglass-server-linux-*.tar.gz)
# The previous build stays in place until the new one is fully extracted and checked.
set -u
root_unix="$(cd "$(dirname "$0")/.." && pwd)"
host="${1:-bg-dev-mission01}"
archive="${2:-$(ls -t "$root_unix"/Builds/blackglass-server-linux-*.tar.gz 2>/dev/null | head -1)}"
if [ -z "$archive" ] || [ ! -f "$archive" ]; then
  echo "No archive found. Run Tools/build-server.sh first."
  exit 1
fi
name="$(basename "$archive")"
echo "deploying $name to $host"
scp -q "$archive" "$host:/tmp/$name" || { echo "upload failed"; exit 1; }
ssh "$host" "set -e
  mkdir -p /opt/blackglass/logs
  rm -rf /opt/blackglass/server.new
  mkdir -p /opt/blackglass/server.new
  tar -xzf /tmp/$name -C /opt/blackglass/server.new --strip-components=1
  chmod +x /opt/blackglass/server.new/BlackglassServer.x86_64 /opt/blackglass/server.new/*.sh
  test -x /opt/blackglass/server.new/BlackglassServer.x86_64
  test -f /opt/blackglass/server.new/build-info.txt
  rm -rf /opt/blackglass/server
  mv /opt/blackglass/server.new /opt/blackglass/server
  rm -f /tmp/$name
  echo deployed:
  sed 's/^/  /' /opt/blackglass/server/build-info.txt"
code=$?
echo "EXIT=$code"
exit $code
