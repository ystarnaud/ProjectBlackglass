#!/usr/bin/env bash
# Source provenance for Tools/build-server.sh (owner ruling 2026-10-20, Stage 12 rulings §1). Unity rewrites settings it switches
# and restores during a build, so the source state is never read while or after Unity runs: it is captured before Unity starts,
# Unity is given it to stamp, and afterwards the source must be exactly as captured.
#   build-provenance.sh capture <repo> <file>   writes revision=, sourceTree=, dirty=, contentSha256=, the line-ending settings and
#                                                one status= line per change
#   build-provenance.sh verify <repo> <file>    exit 0 only if HEAD, every change (or its absence) and their content are as captured
# A "change" is anything that can reach the build and differ from the commit:
#   - git's porcelain v1 status with untracked files (tracked edits, staged or not; untracked files);
#   - ignored files under Assets, Packages and ProjectSettings ("!! " lines), which Unity imports although git hides them;
# and contentSha256 hashes all of them (the binary diff against HEAD, then every untracked and ignored file), so a dirty file
# rewritten during the build is caught even though its status line is unchanged. Index flags that hide edits from git
# (assume-unchanged, skip-worktree) and a sparse checkout are refused outright: the state can't be known.
set -u
set -o pipefail

source_dirs=(Assets Packages ProjectSettings)

refuse() { echo "provenance: $*" >&2; exit 1; }

# Refuses the states in which git status can't be trusted.
check_trustworthy() {
  local repo="$1" flagged
  [ "$(git -C "$repo" config --bool core.sparseCheckout 2>/dev/null)" = true ] && refuse "a sparse checkout"
  flagged="$(git -C "$repo" ls-files -v | awk 'substr($0, 1, 1) ~ /[a-zS]/ { print substr($0, 3) }')" || refuse "git ls-files failed"
  [ -z "$flagged" ] || refuse "index flags (assume-unchanged or skip-worktree) hide changes to: $(echo $flagged)"
}

# The change lines, sorted: status (tracked and untracked), then ignored files under the source folders.
changes_of() {
  local repo="$1"
  git -C "$repo" update-index -q --really-refresh >/dev/null 2>&1
  {
    git -C "$repo" status --porcelain=v1 --untracked-files=all --ignore-submodules=none || return 1
    git -C "$repo" ls-files --others --ignored --exclude-standard -- "${source_dirs[@]}" | sed 's/^/!! /' || return 1
  } | LC_ALL=C sort
}

# One hash over the content of every change: the binary diff of tracked files against HEAD, then each untracked or ignored file.
content_of() {
  local repo="$1"
  {
    git -C "$repo" diff --binary HEAD || return 1
    { git -C "$repo" ls-files -z --others --exclude-standard
      git -C "$repo" ls-files -z --others --ignored --exclude-standard -- "${source_dirs[@]}"; } \
      | (cd "$repo" && LC_ALL=C sort -z | xargs -0 -r sha256sum --) || return 1
  } | sha256sum | cut -d' ' -f1
}

case "${1:-}" in
  capture)
    repo="${2:?repo}"; file="${3:?file}"
    check_trustworthy "$repo"
    rev="$(git -C "$repo" rev-parse --verify HEAD)" || refuse "no HEAD"
    tree="$(git -C "$repo" rev-parse --verify 'HEAD^{tree}')" || refuse "no tree"
    changes="$(changes_of "$repo")" || refuse "git status failed"
    content="$(content_of "$repo")" || refuse "the content hash failed"
    dirty=false
    [ -n "$changes" ] && dirty=true
    {
      echo "revision=$rev"
      echo "sourceTree=$tree"
      echo "dirty=$dirty"
      echo "contentSha256=$content"
      echo "coreAutocrlf=$(git -C "$repo" config core.autocrlf || echo unset)"
      echo "coreEol=$(git -C "$repo" config core.eol || echo unset)"
      if [ -n "$changes" ]; then printf '%s\n' "$changes" | sed 's/^/status=/'; fi
    } > "$file" || refuse "cannot write $file"
    ;;
  verify)
    repo="${2:?repo}"; file="${3:?file}"
    [ -f "$file" ] || refuse "no capture"
    captured_rev="$(sed -n 's/^revision=//p' "$file")"
    rev="$(git -C "$repo" rev-parse --verify HEAD)" || refuse "no HEAD"
    if [ "$rev" != "$captured_rev" ]; then
      echo "HEAD moved during the build: $captured_rev -> $rev"
      exit 1
    fi
    check_trustworthy "$repo"
    changes="$(changes_of "$repo")" || refuse "git status failed"
    now="$(if [ -n "$changes" ]; then printf '%s\n' "$changes" | sed 's/^/status=/'; fi)"
    before="$(grep '^status=' "$file")"
    if [ "$now" != "$before" ]; then
      echo "the build left persistent changes:"
      diff <(printf '%s\n' "$before") <(printf '%s\n' "$now") | sed -n 's/^> status=/  now:    /p; s/^< status=/  before: /p'
      exit 1
    fi
    content="$(content_of "$repo")" || refuse "the content hash failed"
    if [ "$content" != "$(sed -n 's/^contentSha256=//p' "$file")" ]; then
      echo "the build left persistent changes: the content of the already-changed files differs from the capture"
      exit 1
    fi
    ;;
  *)
    echo "usage: build-provenance.sh capture|verify <repo> <file>" >&2
    exit 64
    ;;
esac
