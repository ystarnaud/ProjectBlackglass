#!/usr/bin/env bash
# services-vm-check.sh: run as root on the services VM (read-only). Prints ok/FAIL lines; exit 0 only if every check passed.
# A10-D3, the Phase B and Phase C procedures.
#   services-vm-check.sh --mission-addr <a> [--expect-session] [--since @<epoch seconds>]
# --mission-addr: the address the tunnel connects from (for sshd -T -C); it is never printed.
# --expect-session: the tunnel is up, so a bg-api-tunnel-v2 session must exist and run as 47080:47080.
# --since: verify-tunnel.sh's start; the server must have logged its denials since then.
set -uo pipefail
[[ "$(id -u)" == 0 ]] || { echo "run as root" >&2; exit 77; }
mission="" session=0 since=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --mission-addr) mission="${2:?}"; shift 2 ;;
    --expect-session) session=1; shift ;;
    --since) since="${2:?}"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 64 ;;
  esac
done
[[ -n "$mission" ]] || { echo "--mission-addr is required" >&2; exit 64; }
USER_V2=bg-api-tunnel-v2 ID=47080 OLD=bg-api-tunnel PG_UID=999
KEYS=/etc/ssh/authorized_keys.d DROPIN=/etc/ssh/sshd_config.d/60-bg-api-tunnel.conf SYSUSERS=/etc/sysusers.d/bg-api-tunnel-v2.conf
fail=0
ok() { echo "ok   - $*"; }
bad() { echo "FAIL - $*"; fail=1; }
check() { local what="$1"; shift; if "$@"; then ok "$what"; else bad "$what"; fi; }
mode() { stat -c '%U:%G %a' "$1" 2>/dev/null; }

# the account
check "$USER_V2 is uid and gid $ID, home /nonexistent, shell /usr/sbin/nologin" \
  [ "$(getent passwd "$USER_V2" | cut -d: -f3,4,6,7)" = "$ID:$ID:/nonexistent:/usr/sbin/nologin" ]
check "its only group is its own private group $ID" [ "$(id -G "$USER_V2" 2>/dev/null) $(getent group "$ID" | cut -d: -f1,4)" = "$ID $USER_V2:" ]
check "its password is locked" bash -c "getent shadow '$USER_V2' | cut -d: -f2 | grep -q '^!'"
check "/nonexistent does not exist" [ ! -e /nonexistent ]
check "no other account or group uses $ID" [ "$(getent passwd | awk -F: -v i="$ID" '$3 == i' | wc -l) $(getent group | awk -F: -v i="$ID" '$3 == i' | wc -l)" = "1 1" ]
check "no file is owned by uid or gid $ID" [ -z "$(find / -xdev \( -path /proc -o -path /sys \) -prune -o \( -uid "$ID" -o -gid "$ID" \) -print -quit 2>/dev/null)" ]
check "no subordinate uid or gid range covers $ID" bash -c "! awk -F: -v i=$ID '\$2 <= i && i < \$2 + \$3' /etc/subuid /etc/subgid 2>/dev/null | grep -q ."
check "$SYSUSERS is root:root 644" [ "$(mode "$SYSUSERS")" = "root:root 644" ]

# PostgreSQL's files stay out of reach: its data (the Docker volumes' directories uid $PG_UID owns) and the stacks' password files
# (the files inside a *secrets directory under /opt/blackglass). Other uid-$PG_UID files (the test runner's world-readable
# results) are not PostgreSQL's. A positive control first proves the probe really runs as $ID.
mapfile -t data < <(find /var/lib/docker/volumes -xdev -mindepth 2 -maxdepth 2 -name _data -uid "$PG_UID" -type d 2>/dev/null | sort)
mapfile -t secrets < <(find /opt/blackglass -xdev -path '*secrets/*' -type f 2>/dev/null | sort)
check "PostgreSQL's data volumes found (${#data[@]}, owned by uid $PG_UID) and its password files (${#secrets[@]})" [ "${#data[@]}" -gt 0 -a "${#secrets[@]}" -gt 0 ]
check "the probe runs as $USER_V2 ($ID:$ID)" [ "$(runuser -u "$USER_V2" -- id -u):$(runuser -u "$USER_V2" -- id -g)" = "$ID:$ID" ]
check "the probe can read a world-readable file (positive control)" runuser -u "$USER_V2" -- test -r /etc/os-release
readable="$(runuser -u "$USER_V2" -- find "${data[@]}" "${secrets[@]}" -readable -print 2>/dev/null | head -5 | tr '\n' ' ')"
check "$USER_V2 can read none of PostgreSQL's data or password files${readable:+: $readable}" [ -z "$readable" ]
check "PostgreSQL still runs as uid $PG_UID (and only PostgreSQL does)" bash -c "[ -n \"\$(ps -o comm= -u $PG_UID)\" ] && [ -z \"\$(ps -o comm= -u $PG_UID | grep -vx postgres)\" ]"
# the route the uid collision opened: a same-uid process reaches a container's files through /proc/<pid>/root
reach=""; for pid in $(ps -o pid= -u "$PG_UID"); do
  runuser -u "$USER_V2" -- test -r "/proc/$pid/root/" 2>/dev/null && reach+=" $pid"
  runuser -u "$USER_V2" -- test -r "/proc/$pid/environ" 2>/dev/null && reach+=" $pid(environ)"
done
check "$USER_V2 cannot reach PostgreSQL's processes through /proc (root, environ)${reach:+:$reach}" [ -z "$reach" ]

# the SSH files and the effective configuration
check "$KEYS/$USER_V2 is root:root 644 with exactly one key line" \
  bash -c "[ \"\$(stat -c '%U:%G %a' $KEYS/$USER_V2)\" = 'root:root 644' ] && [ \"\$(grep -vc '^#' $KEYS/$USER_V2)\" = 1 ] && grep -q '^restrict,port-forwarding,permitopen=\"127.0.0.1:5080\",command=\"/bin/false\" ssh-ed25519 ' $KEYS/$USER_V2"
check "the retired $OLD has no key file" [ ! -e "$KEYS/$OLD" ]
check "$DROPIN is root:root 644" [ "$(mode "$DROPIN")" = "root:root 644" ]
check "sshd -t is clean" bash -c '[ -z "$(sshd -t 2>&1)" ]'
expected_v2="allowagentforwarding no
allowstreamlocalforwarding no
allowtcpforwarding local
authenticationmethods publickey
authorizedkeysfile /etc/ssh/authorized_keys.d/$USER_V2
forcecommand /bin/false
gatewayports no
kbdinteractiveauthentication no
passwordauthentication no
permitlisten none
permitopen 127.0.0.1:5080
permittty no
permittunnel no
permituserrc no
x11forwarding no"
got_v2="$(sshd -T -C "user=$USER_V2,host=bg-dev-mission01,addr=$mission" | grep -E '^(allowagentforwarding|allowstreamlocalforwarding|allowtcpforwarding|authenticationmethods|authorizedkeysfile|forcecommand|gatewayports|kbdinteractiveauthentication|passwordauthentication|permitlisten|permitopen|permittty|permittunnel|permituserrc|x11forwarding) ' | sort)"
check "no other key source for $USER_V2 (no AuthorizedKeysCommand, principals or trusted CA)" \
  [ "$(sshd -T -C "user=$USER_V2,host=bg-dev-mission01,addr=$mission" | grep -E '^(authorizedkeyscommand|authorizedprincipalsfile|authorizedprincipalscommand|trustedusercakeys) ' | awk '{print $2}' | sort -u)" = none ]
check "sshd -T -C for $USER_V2 gives exactly the restricted settings" [ "$got_v2" = "$expected_v2" ]
[[ "$got_v2" == "$expected_v2" ]] || diff <(echo "$expected_v2") <(echo "$got_v2") | sed 's/^/       /'
expected_old="authorizedkeysfile none
disableforwarding yes
forcecommand /bin/false
gssapiauthentication no
hostbasedauthentication no
kbdinteractiveauthentication no
passwordauthentication no
permittty no
pubkeyauthentication no"
# from the mission VM's address and from any other source (TEST-NET-3 stands for "anyone else")
for from in "$mission" 203.0.113.7; do
  got_old="$(sshd -T -C "user=$OLD,host=elsewhere,addr=$from" | grep -E '^(authorizedkeysfile|disableforwarding|forcecommand|gssapiauthentication|hostbasedauthentication|kbdinteractiveauthentication|passwordauthentication|permittty|pubkeyauthentication) ' | sort)"
  check "sshd -T -C for the retired $OLD allows no authentication method ($([ "$from" = "$mission" ] && echo "from the mission VM" || echo "from another address"))" [ "$got_old" = "$expected_old" ]
done
# and the account holds no credential of its own (it is not modified while PostgreSQL runs under its uid)
check "the retired $OLD has a locked password and the nologin shell" \
  bash -c "getent shadow '$OLD' | cut -d: -f2 | grep -q '^!' && [ \"\$(getent passwd '$OLD' | cut -d: -f7)\" = /usr/sbin/nologin ]"

# the sessions
v2sessions="$(ps -o uid=,gid=,args= -C sshd-session | grep -F "sshd-session: $USER_V2" | grep -v '\[priv\]')"
if [[ "$session" == 1 ]]; then
  check "a $USER_V2 session exists and every one runs as $ID:$ID" \
    bash -c "[ -n \"\$1\" ] && [ -z \"\$(awk -v i=$ID '\$1 != i || \$2 != i' <<<\"\$1\")\" ]" _ "$v2sessions"
fi
check "no sshd process runs as uid $PG_UID" [ -z "$(ps -o comm= -u "$PG_UID" | grep -E '^(sshd|sshd-session)')" ]
if [[ -n "$since" ]]; then
  # an absolute instant (verify-tunnel.sh prints @<epoch seconds>): a zone-less time would be read in this VM's local zone
  [[ "$since" =~ ^@[0-9]+$ || "$since" =~ \ UTC$ ]] || { bad "--since must be @<epoch seconds> or end in ' UTC': $since"; exit 1; }
  log="$(journalctl --since "$since" --no-pager -o cat _COMM=sshd-session + _COMM=sshd 2>/dev/null)"
  check "the server denied the four TCP destinations and the SOCKS request since $since (at least 5 denials)" \
    [ "$(grep -c 'but the request was denied' <<<"$log")" -ge 5 ]
  check "the server refused the stream-local forward" grep -qF 'refused streamlocal port forward' <<<"$log"
  check "every accepted tunnel login since then was $USER_V2 with the one tunnel key" \
    bash -c "! grep -E 'Accepted .* for $OLD ' <<<\"\$1\" | grep -q . && grep -qE 'Accepted publickey for $USER_V2 ' <<<\"\$1\"" _ "$log"
fi
exit "$fail"
