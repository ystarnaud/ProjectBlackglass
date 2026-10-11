#!/usr/bin/env bash
# Runs the server in normal mode for a while and measures it (Checkpoint A initial smoke run; not a stability proof).
# Usage: smoke-run.sh [seconds]   (default 600). Samples CPU and memory every 5 s from /proc, then sends SIGTERM
# (SIGKILL after 30 s if still running). Writes to $BLACKGLASS_LOGS (default /opt/blackglass/logs):
#   <UTC>-<rev>-smoke.log, -smoke.csv and -smoke-summary.txt.
set -u
dir="$(cd "$(dirname "$0")" && pwd)"
logs="${BLACKGLASS_LOGS:-/opt/blackglass/logs}"
duration="${1:-600}"
interval=5
rev=$(awk -F= '$1=="revision"{print substr($2,1,7)}' "$dir/build-info.txt" 2>/dev/null)
stamp=$(date -u +%Y%m%dT%H%M%SZ)
base="$logs/$stamp-${rev:-unknown}-smoke"
log="$base.log"; csv="$base.csv"; summary="$base-summary.txt"
mkdir -p "$logs"
hz=$(getconf CLK_TCK)
cores=$(nproc)

start_ms=$(date +%s%3N)
"$dir/BlackglassServer.x86_64" -batchmode -nographics -logFile "$log" &
pid=$!
echo "elapsed_s,cpu_pct_one_core,rss_kb,hwm_kb" > "$csv"
prev_ticks=""; prev_ms=""; ready_s=""; early=""
while :; do
  now_ms=$(date +%s%3N)
  elapsed_ms=$((now_ms - start_ms))
  if [ ! -r "/proc/$pid/stat" ]; then early="$((elapsed_ms / 1000))"; break; fi
  # Fields 14 and 15 are utime and stime; the process name has no spaces, so plain field numbers are safe.
  ticks=$(awk '{print $14 + $15}' "/proc/$pid/stat")
  rss=$(awk '/^VmRSS:/{print $2}' "/proc/$pid/status")
  hwm=$(awk '/^VmHWM:/{print $2}' "/proc/$pid/status")
  cpu=""
  if [ -n "$prev_ticks" ]; then
    cpu=$(awk -v d=$((ticks - prev_ticks)) -v t=$((elapsed_ms - prev_ms)) -v hz="$hz" 'BEGIN { if (t > 0) printf "%.1f", 100 * d / hz / (t / 1000); else print "" }')
  fi
  echo "$(awk -v e="$elapsed_ms" 'BEGIN { printf "%.1f", e / 1000 }'),$cpu,$rss,$hwm" >> "$csv"
  prev_ticks=$ticks; prev_ms=$elapsed_ms
  if [ -z "$ready_s" ] && grep -q "MISSION READY" "$log" 2>/dev/null; then
    ready_s=$(awk -v e="$elapsed_ms" 'BEGIN { printf "%.1f", e / 1000 }')
  fi
  if [ "$elapsed_ms" -ge $((duration * 1000)) ]; then break; fi
  sleep "$interval"
done

stop="exited early at ${early}s"
if [ -z "$early" ]; then
  kill -TERM "$pid" 2>/dev/null
  stop="SIGTERM"
  for _ in $(seq 1 30); do
    kill -0 "$pid" 2>/dev/null || break
    sleep 1
  done
  if kill -0 "$pid" 2>/dev/null; then
    kill -KILL "$pid" 2>/dev/null
    stop="SIGTERM ignored for 30 s, then SIGKILL"
  fi
fi
wait "$pid"
status=$?

{
  echo "smoke run: duration=${duration}s interval=${interval}s cores=$cores host=$(hostname) kernel=$(uname -r)"
  echo "build: $(tr '\n' ' ' < "$dir/build-info.txt")"
  echo "stop: $stop, exit status $status"
  echo "launch to MISSION READY (wall clock, sampled every ${interval}s): ${ready_s:-never}s"
  echo "ready line: $(grep -m1 "MISSION READY" "$log" 2>/dev/null)"
  awk -F, -v ready="${ready_s:-0}" -v duration="$duration" '
    NR == 1 { next }
    { t = $1 + 0; if ($4 + 0 > hwm) hwm = $4 + 0 }
    # The CPU in a row covers the interval since the previous sample: count it only when that interval began after Ready.
    $2 != "" && prev != "" && prev + 0 >= ready { n++; sum += $2; if ($2 + 0 > max) max = $2 + 0 }
    { prev = t }
    t >= duration - 300 && $3 != "" { s++; ssum += $3; if (first == "") first = $3; last = $3 }
    END {
      printf "cpu after ready (%% of one core): mean %.1f, max %.1f over %d samples\n", (n ? sum / n : 0), max, n
      printf "rss peak (VmHWM): %.1f MB\n", hwm / 1024
      printf "rss steady (mean of the last 300 s): %.1f MB over %d samples\n", (s ? ssum / s / 1024 : 0), s
      printf "rss drift over the last 300 s: %+.1f MB (first %.1f MB, last %.1f MB)\n", (last - first) / 1024, first / 1024, last / 1024
    }' "$csv"
  fps=$(grep -o "heartbeat .*fps=[0-9.]*" "$log" | grep -o "fps=[0-9.]*" | cut -d= -f2)
  if [ -n "$fps" ]; then
    echo "heartbeat fps: min $(echo "$fps" | sort -n | head -1), max $(echo "$fps" | sort -n | tail -1), lines $(echo "$fps" | wc -l)"
  else
    echo "heartbeat fps: no heartbeat lines"
  fi
  echo "log lines containing 'Exception': $(grep -c "Exception" "$log")"
  echo "log lines containing 'Error' or 'error': $(grep -ci "error" "$log")"
  echo "files: $log $csv"
} > "$summary"
cat "$summary"
echo "summary=$summary"
