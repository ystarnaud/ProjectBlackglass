#!/usr/bin/env bash
# From the PC: the listed TCP ports on the VM's SSH address must be closed or filtered, and 22 must answer.
# IPv4/TCP only (the full IPv4/IPv6, TCP/UDP scans are Stage 17). The address comes from the SSH alias and is never printed.
#   offhost-probe.sh [alias] [ports...]     default: bg-dev-services01-admin 5080 5432 55432
set -uo pipefail
alias="${1:-bg-dev-services01-admin}"; shift || true
ports=("$@"); (( ${#ports[@]} )) || ports=(5080 5432 55432)
host="$(ssh -G "$alias" | awk '$1 == "hostname" { print $2 }')"
fail=0
for port in "${ports[@]}"; do
  if timeout 3 bash -c "</dev/tcp/$host/$port" 2>/dev/null; then echo "FAIL - $port answers from the PC"; fail=1
  else echo "ok   - $port closed or filtered from the PC"; fi
done
if timeout 3 bash -c "</dev/tcp/$host/22" 2>/dev/null; then echo "ok   - 22 answers (SSH)"; else echo "FAIL - SSH (22) does not answer"; fail=1; fi
exit $fail
