#!/usr/bin/env bash
# Smoke-tests a built AC Launcher artifact: unpacks it, checks the payload inventory,
# then LAUNCHES it and requires it to stay running.
#
# Build and install steps report on themselves, not on the product: a package that
# copied too little still builds and installs cleanly and then cannot start. Only
# running the app shows that, so every release artifact goes through this first.
#
# Usage: packaging/smoke-test.sh <artifact> [...]
#   <artifact>: the release .tar.gz, the .deb, or an Arch .pkg.tar.zst
# Exit: 0 all checks passed | 1 a check failed | 2 the launch test could not run
set -uo pipefail

MIN_ASSEMBLIES=150
LAUNCH_SECONDS=15

fail() { printf '  \033[31mFAIL\033[0m  %s\n' "$*"; FAILED=1; }
ok()   { printf '  \033[32mok\033[0m    %s\n' "$*"; }

smoke_one() {
    local artifact="$1"
    local tmp payload
    FAILED=0
    printf '\n== %s ==\n' "$(basename "$artifact")"

    [[ -f $artifact ]] || { fail "no such file"; return 1; }
    tmp=$(mktemp -d) || return 1
    trap 'rm -rf "$tmp"' RETURN

    case $artifact in
        *.tar.gz|*.tgz|*.pkg.tar.zst|*.pkg.tar.xz)
            bsdtar -xf "$artifact" -C "$tmp" ;;
        *.deb)
            if command -v dpkg-deb >/dev/null; then
                dpkg-deb -x "$artifact" "$tmp"
            else
                bsdtar -xOf "$artifact" 'data.tar.*' | bsdtar -xf - -C "$tmp"
            fi ;;
        *)  fail "unrecognised artifact type"; return 1 ;;
    esac
    [[ $? -eq 0 ]] || { fail "could not unpack"; return 1; }

    payload=$(find "$tmp" -name ACLauncher -type f -perm -u+x -printf '%h\n' | head -1)
    [[ -n $payload ]] || { fail "no ACLauncher apphost in the artifact"; return 1; }
    ok "payload at ${payload#"$tmp"}"

    # --- inventory -------------------------------------------------------------
    local n f
    n=$(find "$payload" -maxdepth 1 -name '*.dll' | wc -l)
    if (( n < MIN_ASSEMBLIES )); then
        fail "only $n managed assemblies (expected >= $MIN_ASSEMBLIES)"
    else
        ok "$n managed assemblies"
    fi
    for f in ACLauncher.dll ACLauncher.Core.dll ACLauncher.runtimeconfig.json ACLauncher.deps.json \
             System.Private.CoreLib.dll Avalonia.Base.dll Avalonia.X11.dll \
             libcoreclr.so libhostfxr.so libhostpolicy.so libSkiaSharp.so libHarfBuzzSharp.so; do
        [[ -f "$payload/$f" ]] && ok "$f" || fail "$f is missing"
    done

    local icon desktop
    icon=$(find "$tmp" -path '*hicolor/256x256/apps/ac-launcher.png' | head -1)
    desktop=$(find "$tmp" -name ac-launcher.desktop | head -1)
    [[ -n $icon ]] && ok "icon ${icon#"$tmp"}" || fail "256px icon is missing"
    [[ -n $desktop ]] && ok "desktop entry ${desktop#"$tmp"}" || fail "desktop entry is missing"

    # --- launch ----------------------------------------------------------------
    if [[ -z ${DISPLAY:-} && -z ${WAYLAND_DISPLAY:-} ]]; then
        printf '  \033[33mSKIPPED\033[0m  launch test: no DISPLAY/WAYLAND_DISPLAY.\n'
        printf '           Inventory alone is NOT a pass. Re-run in a graphical session.\n'
        return 2
    fi

    # A throwaway settings folder, so the test never touches the real configuration.
    local log=$tmp/launch.log pid i
    ( cd "$payload" && AC_LAUNCHER_HOME="$tmp/home" ./ACLauncher ) > "$log" 2>&1 &
    pid=$!
    for ((i = 1; i <= LAUNCH_SECONDS; i++)); do
        kill -0 "$pid" 2>/dev/null || break
        sleep 1
    done

    if kill -0 "$pid" 2>/dev/null; then
        ok "still running after ${LAUNCH_SECONDS}s"
        kill "$pid" 2>/dev/null
        wait "$pid" 2>/dev/null
    else
        wait "$pid" 2>/dev/null
        fail "exited after ${i}s (code $?)"
        sed 's/^/           | /' "$log" | head -20
    fi

    if grep -qiE 'does not exist|A fatal error|Failed to load|FileNotFoundException|DllNotFoundException' "$log"; then
        fail "host/runtime error in output:"
        grep -iE 'does not exist|A fatal error|Failed to load|FileNotFoundException|DllNotFoundException' "$log" |
            sed 's/^/           | /' | head -5
    fi
    if [[ -f $tmp/home/state/logs/ac-launcher.log ]] && grep -q 'starting' "$tmp/home/state/logs/ac-launcher.log"; then
        ok "app log: $(grep -m1 'starting' "$tmp/home/state/logs/ac-launcher.log" | cut -c25-60)"
    else
        fail "the app never wrote its startup log line"
    fi

    return $FAILED
}

(( $# )) || { echo "usage: $(basename "$0") <artifact> [...]" >&2; exit 2; }

rc=0
for a in "$@"; do
    smoke_one "$a" || rc=$?
done

printf '\n'
case $rc in
    0) printf '\033[32mSMOKE TEST PASSED\033[0m — artifact launches.\n' ;;
    2) printf '\033[33mSMOKE TEST INCOMPLETE\033[0m — inventory only, app never launched.\n' ;;
    *) printf '\033[31mSMOKE TEST FAILED\033[0m — do NOT release this artifact.\n' ;;
esac
exit $rc
