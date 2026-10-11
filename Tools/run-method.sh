#!/usr/bin/env bash
# Runs one static Editor method in batch mode. The Unity Editor must be closed.
# Usage: Tools/run-method.sh Namespace.Class.Method
set -u
method="${1:?usage: Tools/run-method.sh Namespace.Class.Method}"
root="$(cd "$(dirname "$0")/.." && (pwd -W 2>/dev/null || pwd))"
unity="${UNITY_EXE:-/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe}"
mkdir -p "$root/Logs"
log="$root/Logs/Method-${method//./_}.log"
"$unity" -batchmode -quit -projectPath "$root" -executeMethod "$method" -logFile "$log"
code=$?
if grep -q "another Unity instance is running" "$log" 2>/dev/null; then
  echo "The Unity Editor has this project open. Close it and run again."
fi
grep -h "error CS" "$log" 2>/dev/null | sort -u
grep -hE "Exception" -A4 "$log" 2>/dev/null | head -40
echo "EXIT=$code"
exit $code
