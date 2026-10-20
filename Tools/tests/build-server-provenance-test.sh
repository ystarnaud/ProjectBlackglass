#!/usr/bin/env bash
# Regression tests for Tools/build-server.sh's source provenance (owner ruling 2026-10-20, Stage 12 rulings §1): the commit,
# source tree and cleanliness are captured before Unity starts, Unity only stamps what it is given, any change that survives the
# build fails it, and the binary's SHA-256 is recorded. Runs build-server.sh against throwaway git repositories with a fake Unity
# (UNITY_EXE) that writes a build the way ServerBuild does and touches settings as each case needs. No real Unity, no network.
# Usage: bash Tools/tests/build-server-provenance-test.sh   (prints PASS/FAIL lines; exit 0 only if every case passes)
set -u
here="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
failures=0

pass() { echo "PASS $1"; }
fail() { echo "FAIL $1: $2"; failures=$((failures + 1)); }

# The fake Unity: argument parsing as build-server.sh calls it; FAKE_MODE chooses the settings behaviour.
cat > "$work/fake-unity.sh" <<'FAKE'
#!/usr/bin/env bash
project="" log=""
while [ $# -gt 0 ]; do
  case "$1" in -projectPath) project="$2"; shift ;; -logFile) log="$2"; shift ;; esac
  shift
done
cd "$project" || exit 1
settings=ProjectSettings/ProjectSettings.asset
case "${FAKE_MODE:-restore}" in
  restore) cp "$settings" "$settings.orig"; echo "activeBuildTarget: Linux" >> "$settings" ;;  # switched during the build...
  leave) echo "upgradedByUnity: 1" >> "$settings" ;;                                              # ...or rewritten for good
  commit) git -c user.email=t@t -c user.name=t commit -q --allow-empty -m "moved during the build" ;;
esac
mkdir -p Builds/LinuxServer/BlackglassServer_Data/Managed Builds/LinuxServer/BlackglassServer_BurstDebugInformation_DoNotShip
head -c 2048 /dev/urandom > Builds/LinuxServer/BlackglassServer_Data/Managed/Blackglass.dll
echo debug > Builds/LinuxServer/BlackglassServer_BurstDebugInformation_DoNotShip/x.txt
head -c 4096 /dev/urandom > Builds/LinuxServer/BlackglassServer.x86_64
{
  echo "product=ProjectBlackglass"
  echo "revision=${BLACKGLASS_SOURCE_REVISION:-unknown}"
  echo "sourceTree=${BLACKGLASS_SOURCE_TREE:-unknown}"
  echo "dirty=${BLACKGLASS_SOURCE_DIRTY:-unknown}"
  echo "variant=release"
} > Builds/LinuxServer/build-info.txt
[ "${FAKE_MODE:-restore}" = restore ] && mv "$settings.orig" "$settings"                         # ...and restored at the end
echo "[ServerBuild] SUCCEEDED variant=release" > "$log"
exit 0
FAKE
chmod +x "$work/fake-unity.sh"

# A fresh repository holding the scripts under test and a minimal project.
new_repo() {
  local repo="$work/$1"
  mkdir -p "$repo/Tools/LinuxServer" "$repo/Assets" "$repo/ProjectSettings"
  cp "$here/build-server.sh" "$here/build-provenance.sh" "$repo/Tools/"
  echo 'echo vm' > "$repo/Tools/LinuxServer/run.sh"
  echo "class A {}" > "$repo/Assets/A.cs"
  echo "productName: ProjectBlackglass" > "$repo/ProjectSettings/ProjectSettings.asset"
  printf 'Builds/\nLogs/\n' > "$repo/.gitignore"
  git -C "$repo" init -q
  git -C "$repo" config core.autocrlf false
  git -C "$repo" add -A
  git -C "$repo" -c user.email=t@t -c user.name=t commit -q -m base
  echo "$repo"
}

build() { # build <repo> <mode>: build-server.sh's output in $out, its status in $code
  (cd "$1" && UNITY_EXE="$work/fake-unity.sh" FAKE_MODE="$2" bash Tools/build-server.sh) > "$work/out.txt" 2>&1
  code=$?
  out="$(cat "$work/out.txt")"
}

info() { awk -F= -v k="$2" '$1==k{print substr($0, length(k) + 2)}' "$1/Builds/LinuxServer/build-info.txt"; }

# 1. Clean source; Unity switches a setting during the build and restores it: stamped clean, verified, hashed.
repo="$(new_repo clean)"
build "$repo" restore
rev="$(git -C "$repo" rev-parse HEAD)"; tree="$(git -C "$repo" rev-parse 'HEAD^{tree}')"
if [ "$code" -ne 0 ]; then fail clean "exit $code: $out"
elif [ "$(info "$repo" revision)" != "$rev" ] || [ "$(info "$repo" sourceTree)" != "$tree" ]; then fail clean "embedded revision or tree differs"
elif [ "$(info "$repo" dirty)" != false ]; then fail clean "dirty=$(info "$repo" dirty)"
elif [ "$(info "$repo" provenance)" != verified ]; then fail clean "provenance=$(info "$repo" provenance)"
elif [ "$(info "$repo" binarySha256)" != "$(sha256sum "$repo/Builds/LinuxServer/BlackglassServer.x86_64" | cut -d' ' -f1)" ]; then fail clean "binarySha256"
elif [ ! -f "$repo/Builds/blackglass-server-linux-${rev:0:7}.tar.gz" ]; then fail clean "no clean archive"
elif ! tar -tzf "$repo/Builds/blackglass-server-linux-${rev:0:7}.tar.gz" | grep -qx 'LinuxServer/source-provenance.txt'; then fail clean "archive lacks source-provenance.txt"
elif [ ! -f "$repo/Builds/LinuxServer/files.sha256" ]; then fail clean "no files.sha256"
elif ! (cd "$repo/Builds/LinuxServer" && sha256sum -c --quiet files.sha256 >/dev/null 2>&1); then fail clean "files.sha256 does not verify"
elif ! grep -q ' \./BlackglassServer_Data/Managed/Blackglass\.dll$' "$repo/Builds/LinuxServer/files.sha256" || ! grep -q ' \./build-info\.txt$' "$repo/Builds/LinuxServer/files.sha256"; then fail clean "files.sha256 misses the game code or build-info.txt"
elif grep -q 'DoNotShip\|files\.sha256$' "$repo/Builds/LinuxServer/files.sha256"; then fail clean "files.sha256 lists an unshipped file or itself"
elif ! printf '%s' "$out" | grep -q "^files.sha256 sha256=$(sha256sum "$repo/Builds/LinuxServer/files.sha256" | cut -d' ' -f1)$"; then fail clean "the file list's hash is not printed"
elif [ -n "$(git -C "$repo" status --porcelain --untracked-files=all)" ]; then fail clean "the source is not clean after the build"
else pass "clean source, temporary settings change: dirty=false, provenance=verified, executable and every shipped file hashed"; fi

# 2a. A tracked change before the build: stamped dirty, its path recorded, the archive named -dirty.
repo="$(new_repo dirty-tracked)"
echo "class B {}" >> "$repo/Assets/A.cs"
build "$repo" restore
rev="$(git -C "$repo" rev-parse HEAD)"
if [ "$code" -ne 0 ]; then fail dirty-tracked "exit $code: $out"
elif [ "$(info "$repo" dirty)" != true ]; then fail dirty-tracked "dirty=$(info "$repo" dirty)"
elif ! grep -qx 'status= M Assets/A.cs' "$repo/Builds/LinuxServer/source-provenance.txt"; then fail dirty-tracked "the dirty path is not recorded"
elif [ ! -f "$repo/Builds/blackglass-server-linux-${rev:0:7}-dirty.tar.gz" ]; then fail dirty-tracked "no -dirty archive"
elif [ "$(cat "$repo/Assets/A.cs")" != "$(printf 'class A {}\nclass B {}')" ]; then fail dirty-tracked "the build changed the owner's modification"
else pass "a tracked change before the build: dirty=true, path recorded, -dirty archive, the change preserved"; fi

# 2b. An untracked source file before the build counts as dirty too.
repo="$(new_repo dirty-untracked)"
echo "class C {}" > "$repo/Assets/C.cs"
build "$repo" restore
if [ "$code" -ne 0 ]; then fail dirty-untracked "exit $code: $out"
elif [ "$(info "$repo" dirty)" != true ]; then fail dirty-untracked "dirty=$(info "$repo" dirty)"
elif ! grep -qx 'status=?? Assets/C.cs' "$repo/Builds/LinuxServer/source-provenance.txt"; then fail dirty-untracked "the untracked path is not recorded"
else pass "an untracked source file before the build: dirty=true"; fi

# 2c. An ignored file Unity would import (review 2, I-N1): git status can't see it, the capture must.
repo="$(new_repo dirty-ignored)"
printf 'Builds/\nLogs/\nTestResults/\n' > "$repo/.gitignore"
git -C "$repo" add .gitignore
git -C "$repo" -c user.email=t@t -c user.name=t commit -q -m ignore
mkdir -p "$repo/Assets/X/TestResults"
echo "class Evil {}" > "$repo/Assets/X/TestResults/Evil.cs"
build "$repo" restore
if [ "$code" -ne 0 ]; then fail dirty-ignored "exit $code: $out"
elif [ "$(info "$repo" dirty)" != true ]; then fail dirty-ignored "dirty=$(info "$repo" dirty)"
elif ! grep -qx 'status=!! Assets/X/TestResults/Evil.cs' "$repo/Builds/LinuxServer/source-provenance.txt"; then fail dirty-ignored "the ignored path is not recorded"
else pass "an ignored file under Assets before the build: dirty=true, recorded"; fi

# 2d. An index entry marked skip-worktree (or assume-unchanged) hides edits from git status: refused before Unity starts.
repo="$(new_repo flagged)"
git -C "$repo" update-index --skip-worktree Assets/A.cs
echo "class Hidden {}" >> "$repo/Assets/A.cs"
build "$repo" restore
if [ "$code" -eq 0 ]; then fail flagged "exit 0"
elif ! printf '%s' "$out" | grep -q 'BUILD FAILED: the source state could not be captured'; then fail flagged "no refusal: $out"
elif ! printf '%s' "$out" | grep -q 'Assets/A.cs'; then fail flagged "the flagged path is not named"
elif ls "$repo"/Builds/*.tar.gz >/dev/null 2>&1; then fail flagged "an archive was written"
else pass "a skip-worktree entry: refused before Unity, named, nothing built"; fi

# 2e. A dirty file Unity rewrites again during the build (review 2, M1): same status line, different content: refused.
repo="$(new_repo dirty-rewritten)"
echo "productName: Owner" > "$repo/ProjectSettings/ProjectSettings.asset"
build "$repo" leave
if [ "$code" -eq 0 ]; then fail dirty-rewritten "exit 0"
elif ! printf '%s' "$out" | grep -q 'BUILD FAILED: the build left persistent changes'; then fail dirty-rewritten "no refusal: $out"
elif [ "$(head -1 "$repo/ProjectSettings/ProjectSettings.asset")" != "productName: Owner" ]; then fail dirty-rewritten "the owner's change was lost"
else pass "a dirty file rewritten during the build: refused, the owner's change kept"; fi

# 3. Unity leaves a modification behind: the build fails and nothing is packaged.
repo="$(new_repo leave)"
build "$repo" leave
if [ "$code" -eq 0 ]; then fail leave "exit 0"
elif ! printf '%s' "$out" | grep -q 'BUILD FAILED: the build left persistent changes'; then fail leave "no refusal message: $out"
elif ! printf '%s' "$out" | grep -q 'M ProjectSettings/ProjectSettings.asset'; then fail leave "the change is not named"
elif ls "$repo"/Builds/*.tar.gz >/dev/null 2>&1; then fail leave "an archive was written"
elif [ -e "$repo/Builds/LinuxServer" ]; then fail leave "the unverified build was left behind"
else pass "a persistent modification after the build: refused, named, nothing packaged"; fi

# 3b. The commit moves during the build: refused.
repo="$(new_repo moved)"
build "$repo" commit
if [ "$code" -eq 0 ]; then fail moved "exit 0"
elif ! printf '%s' "$out" | grep -q 'BUILD FAILED: HEAD moved during the build'; then fail moved "no refusal message: $out"
else pass "HEAD moving during the build: refused"; fi

echo "failures=$failures"
[ "$failures" -eq 0 ]
