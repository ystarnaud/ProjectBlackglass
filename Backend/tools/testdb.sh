#!/usr/bin/env bash
# The integration-test PostgreSQL on bg-dev-services01 (Compose project blackglass-testdb, 127.0.0.1:55432 on the VM).
#   testdb.sh up        sync Backend/, create the secrets once, start the cluster, copy the secrets to ~/.blackglass/testdb-secrets
#   testdb.sh tunnel    forward 127.0.0.1:55432 on this PC to the VM's loopback (foreground; Ctrl+C to stop)
#   testdb.sh down      stop the cluster (keeps its data volume)
#   testdb.sh destroy   stop it and delete the volume
set -euo pipefail
alias="${BLACKGLASS_SERVICES_HOST:-bg-dev-services01-admin}"
vm=/opt/blackglass/backend
services="$vm/src/Backend/deploy/services"
local_secrets="$HOME/.blackglass/testdb-secrets"
compose="sudo -n env BLACKGLASS_SECRETS_DIR=$vm/testdb-secrets docker compose -f $services/compose.test.yaml"
case "${1:-}" in
  up)
    bash "$(dirname "$0")/vm-sync.sh"
    ssh "$alias" "set -e; sudo -n bash $services/make-secrets.sh $vm/testdb-secrets; $compose up -d --wait"
    mkdir -p "$local_secrets"   # under the Windows user profile, which only this user can read
    for name in superuser migrator app; do
      ssh "$alias" "sudo -n cat $vm/testdb-secrets/postgres/${name}_password" > "$local_secrets/pg_${name}_password"
    done
    echo "test cluster up; secrets copied to $local_secrets" ;;
  tunnel)  exec ssh -N -o ExitOnForwardFailure=yes -L 127.0.0.1:55432:127.0.0.1:55432 "$alias" ;;
  down)    ssh "$alias" "$compose down" ;;
  destroy) ssh "$alias" "$compose down -v" ;;
  *) echo "usage: testdb.sh up|tunnel|down|destroy" >&2; exit 64 ;;
esac
