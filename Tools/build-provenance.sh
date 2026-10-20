#!/usr/bin/env bash
# Source provenance for Tools/build-server.sh (owner ruling 2026-10-20, Stage 12 rulings §1). Unity rewrites settings it switches
# and restores during a build, so the source state is never read while or after Unity runs: it is captured before Unity starts,
# Unity is given it to stamp, and afterwards the source must be exactly as captured.
#   build-provenance.sh capture <repo> <file>   writes revision=, sourceTree=, dirty= and one status= line per change
#   build-provenance.sh verify <repo> <file>    exit 0 only if HEAD and every change (or its absence) are exactly as captured
# "Changes" are git's porcelain v1 status with untracked files (ignored ones excluded), so an untracked source file counts.
set -u

status_of() { git -C "$1" status --porcelain=v1 --untracked-files=all --ignore-submodules=none; }

case "${1:-}" in
  capture)
    repo="${2:?repo}"; file="${3:?file}"
    rev="$(git -C "$repo" rev-parse --verify HEAD)" || { echo "provenance: no HEAD" >&2; exit 1; }
    tree="$(git -C "$repo" rev-parse --verify 'HEAD^{tree}')" || { echo "provenance: no tree" >&2; exit 1; }
    status="$(status_of "$repo")" || { echo "provenance: git status failed" >&2; exit 1; }
    dirty=false
    [ -n "$status" ] && dirty=true
    {
      echo "revision=$rev"
      echo "sourceTree=$tree"
      echo "dirty=$dirty"
      if [ -n "$status" ]; then printf '%s\n' "$status" | sed 's/^/status=/'; fi
    } > "$file" || { echo "provenance: cannot write $file" >&2; exit 1; }
    ;;
  verify)
    repo="${2:?repo}"; file="${3:?file}"
    [ -f "$file" ] || { echo "provenance: no capture" >&2; exit 1; }
    captured_rev="$(sed -n 's/^revision=//p' "$file")"
    rev="$(git -C "$repo" rev-parse --verify HEAD)" || { echo "provenance: no HEAD" >&2; exit 1; }
    if [ "$rev" != "$captured_rev" ]; then
      echo "HEAD moved during the build: $captured_rev -> $rev"
      exit 1
    fi
    status="$(status_of "$repo")" || { echo "provenance: git status failed" >&2; exit 1; }
    now="$(if [ -n "$status" ]; then printf '%s\n' "$status" | sed 's/^/status=/'; fi)"
    before="$(grep '^status=' "$file")"
    if [ "$now" != "$before" ]; then
      echo "the build left persistent changes:"
      diff <(printf '%s\n' "$before") <(printf '%s\n' "$now") | sed -n 's/^> status=/  now:    /p; s/^< status=/  before: /p'
      exit 1
    fi
    ;;
  *)
    echo "usage: build-provenance.sh capture|verify <repo> <file>" >&2
    exit 64
    ;;
esac
