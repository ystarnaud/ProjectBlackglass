#!/usr/bin/env bash
# Starts, stops or reports the networked mission server on this machine (networking experiment, decision 046).
# Usage: run-net.sh start|stop|status
# Environment: NET_PORT (7770), NET_SECONDS (3600: `timeout` ends the process), BLACKGLASS_LOGS, BLACKGLASS_TOKENS (release tokens),
#              BLACKGLASS_FIXTURE_TOKENS (used instead when NET_FIXTURE=1), NET_FIXTURE (1: start the development fixture;
#              needs the fixture build and make-dev-tokens.sh --fixture).
# The server listens on 127.0.0.1 only; reach it with `ssh -L`. -netQuiet makes the harness repeatable (decision 046).
set -u
dir="$(cd "$(dirname "$0")" && pwd)"
logs="${BLACKGLASS_LOGS:-/opt/blackglass/logs}"
if [ "${NET_FIXTURE:-0}" = "1" ]; then
  tokens="${BLACKGLASS_FIXTURE_TOKENS:-/opt/blackglass/dev-tokens-fixture.txt}"
  tokenkind=fixture
  tokenhint="make-dev-tokens.sh --fixture"
else
  tokens="${BLACKGLASS_TOKENS:-/opt/blackglass/dev-tokens.txt}"
  tokenkind=release
  tokenhint="make-dev-tokens.sh"
fi
port="${NET_PORT:-7770}"
limit="${NET_SECONDS:-3600}"
pidfile="$logs/net-server.pid"

running() { [ -f "$pidfile" ] && kill -0 "$(cat "$pidfile")" 2>/dev/null; }

case "${1:-}" in
  start)
    [ -f "$tokens" ] || { echo "no $tokenkind token file at $tokens; run $tokenhint first"; exit 2; }
    if [ "$tokenkind" = release ] && grep -q '+fixture' "$tokens"; then
      echo "the release token file $tokens carries a +fixture identity; run make-dev-tokens.sh (it replaces such a file)"
      exit 2
    fi
    if running; then echo "already running (pid $(cat "$pidfile"))"; exit 0; fi
    mkdir -p "$logs"
    rev=$(awk -F= '$1=="revision"{print substr($2,1,7)}' "$dir/build-info.txt" 2>/dev/null)
    log="$logs/$(date -u +%Y%m%dT%H%M%SZ)-${rev:-unknown}-net.log"
    fixture_args=""
    if [ "${NET_FIXTURE:-0}" = "1" ]; then fixture_args="-netFixture"; fi
    variant=$(awk -F= '$1=="variant"{print $2}' "$dir/build-info.txt" 2>/dev/null)
    echo "build variant=${variant:-unknown} fixture=${NET_FIXTURE:-0} tokens=$tokenkind"
    setsid nohup timeout --kill-after=10 "$limit" "$dir/BlackglassServer.x86_64" -batchmode -nographics -logFile "$log" \
      -net -netQuiet $fixture_args -netPort "$port" -netTokens "$tokens" > /dev/null 2>&1 < /dev/null &
    echo $! > "$pidfile"
    for _ in $(seq 1 30); do
      if grep -q "\[Net\] listening" "$log" 2>/dev/null; then
        echo "listening on 127.0.0.1:$port"
        echo "log=$log"
        exit 0
      fi
      if grep -q "exit code=" "$log" 2>/dev/null; then
        echo "the server exited:"
        grep -E "\[Server\] exit|\[Net\]" "$log" | tail -5
        exit 1
      fi
      sleep 1
    done
    echo "no 'listening' line within 30 s; see $log"
    exit 1
    ;;
  stop)
    if running; then
      kill "$(cat "$pidfile")"
      for _ in $(seq 1 30); do
        running || break
        sleep 1
      done
    fi
    if running; then echo "still running after 30 s"; exit 1; fi
    rm -f "$pidfile"
    echo "stopped"
    ;;
  status)
    if running; then
      echo "running (pid $(cat "$pidfile"))"
      ss -lnt 2>/dev/null | grep ":$port " || true
    else
      echo "not running"
    fi
    ;;
  *)
    echo "usage: run-net.sh start|stop|status"
    exit 2
    ;;
esac
