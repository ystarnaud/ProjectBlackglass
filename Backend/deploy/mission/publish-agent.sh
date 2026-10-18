#!/usr/bin/env bash
# Publishes the agent as a self-contained linux-x64 release (A10-D6) into <out>/agent-<revision>/: the single-file executables
# blackglass-agent and blackglass-credential-broker (the root broker, security correction I3), the native libraries they ship with,
# record-exit, slot-guard, and RELEASE (revision, runtime identifier, and the SHA-256 of every file). No machine-wide .NET runtime is needed on the VM. Run from Backend/ on a machine with the .NET 10 SDK.
#   publish-agent.sh <out-dir>
set -euo pipefail
out="${1:?usage: publish-agent.sh <out-dir>}"
here="$(cd "$(dirname "$0")" && pwd)"
backend="$(cd "$here/../.." && pwd)"
revision="$(git -C "$backend" rev-parse --short=12 HEAD)"
[[ -z "$(git -C "$backend" status --porcelain -- src/MissionHost.Agent src/MissionHost.CredentialBroker src/Accounts.Domain deploy/mission)" ]] || { echo "refusing: uncommitted changes" >&2; exit 78; }
release="$out/agent-$revision"
rm -rf "$release" && mkdir -p "$release"
dotnet publish "$backend/src/MissionHost.Agent/MissionHost.Agent.csproj" -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:DebugType=none -o "$release" -nologo
dotnet publish "$backend/src/MissionHost.CredentialBroker/MissionHost.CredentialBroker.csproj" -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:DebugType=none -o "$release" -nologo
install -m 0755 "$here/record-exit" "$release/record-exit"
install -m 0755 "$here/slot-guard" "$release/slot-guard"
# A script checked out with CRLF line endings can't be executed (its interpreter line names "/bin/sh\r"): refuse it here.
! grep -l $'\r' "$release/record-exit" "$release/slot-guard" || { echo "refusing: CRLF line endings in a script (check out with LF)" >&2; exit 78; }
{
  echo "revision=$revision"
  echo "runtime=linux-x64"
  echo "built=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  (cd "$release" && find . -type f ! -name RELEASE -printf '%P\n' | sort | xargs sha256sum)
} > "$release/RELEASE"
echo "release: $release"
grep -E '^(revision|runtime)=|blackglass-agent$|blackglass-credential-broker$' "$release/RELEASE"
