#!/usr/bin/env bash
# Proves the .deb's Depends are complete: installs it with apt on clean Debian/Ubuntu root
# filesystems (so apt resolves every dependency from that distribution's own archive), then
#   1. checks every ELF file in the payload has all its linked libraries,
#   2. checks the libraries the runtime loads by name at run time are present,
#   3. checks the programs the launcher runs exist,
#   4. launches the app inside the root and requires it to stay up, write its log, and
#      download the server lists over HTTPS.
# Before installing, the same library checks run against the bare payload as a control:
# they must report missing libraries there, or the check could not see a gap at all.
#
# Runs unprivileged in a user namespace (needs /etc/subuid, network, and a graphical session
# for step 4).
# Usage: packaging/verify-deb-deps.sh <package.deb> <rootfs.tar.gz> [<rootfs.tar.gz> ...]
set -uo pipefail

(( $# >= 2 )) || { echo "usage: $(basename "$0") <package.deb> <rootfs.tar.gz> [...]" >&2; exit 2; }
DEB=$(realpath "$1")
shift

rc=0
for ROOTFS in "$@"; do
    ROOTFS=$(realpath "$ROOTFS")
    WORK=$(mktemp -d)
    printf '\n== %s ==\n' "$(basename "$ROOTFS")"
    unshare --map-auto --map-root-user --mount --pid --fork bash -s "$WORK" "$ROOTFS" "$DEB" <<'INNER'
set -uo pipefail
work=$1 rootfs=$2 deb=$3
root=$work/root
failed=0
fail() { printf '  \033[31mFAIL\033[0m  %s\n' "$*"; failed=1; }
ok()   { printf '  \033[32mok\033[0m    %s\n' "$*"; }

mkdir -p "$root"
tar -xzf "$rootfs" -C "$root" --exclude='./dev/*' --exclude='dev/*' 2>/dev/null
mount -t proc proc "$root/proc" || { fail "could not mount /proc in the root"; exit 1; }
mount --rbind /dev "$root/dev"
mount --rbind /sys "$root/sys"
mount -t tmpfs tmpfs "$root/tmp"
rm -f "$root/etc/resolv.conf"
cp -L /etc/resolv.conf "$root/etc/resolv.conf"
cp "$deb" "$root/tmp/pkg.deb"
mkdir -p "$root/tmp/.X11-unix"
mount --bind /tmp/.X11-unix "$root/tmp/.X11-unix"
xauth_env=()
if [[ -n ${XAUTHORITY:-} && -f $XAUTHORITY ]]; then
    cp "$XAUTHORITY" "$root/tmp/xauth"
    xauth_env=(XAUTHORITY=/tmp/xauth)
fi

in_root() {
    chroot "$root" /usr/bin/env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin HOME=/root LANG=C.UTF-8 \
        DEBIAN_FRONTEND=noninteractive "$@"
}

. "$root/etc/os-release"
ok "root: $PRETTY_NAME"

# Libraries the runtime and Avalonia load by name (dlopen/P-Invoke) rather than link against.
dlopen_libs=(libssl.so.3 libcrypto.so.3 libX11.so.6 libXext.so.6 libXrandr.so.2 libXi.so.6
             libXcursor.so.1 libXfixes.so.3 libICE.so.6 libSM.so.6)

has_lib() {   # is a shared library known to the root's dynamic linker cache?
    in_root ldconfig -p | awk '{ print $1 }' | grep -qx "$1"
}

scan() {   # $1 = payload dir inside the root; prints "linked <lib>" / "loaded <lib>" per missing library
    local dir=$1 elf lib
    while IFS= read -r elf; do
        in_root ldd "$elf" 2>&1 | awk '/not found/ { print "linked", $1 }'
    done < <(in_root find "$dir" -type f \( -name '*.so' -o -name ACLauncher \))
    for lib in "${dlopen_libs[@]}"; do
        has_lib "$lib" || echo "loaded $lib"
    done
    has_lib libicuuc.so."$(in_root sh -c 'ls /usr/lib/x86_64-linux-gnu/libicuuc.so.[0-9]* 2>/dev/null' | sed -n 's/.*libicuuc\.so\.\([0-9]*\)$/\1/p' | head -1)" ||
        echo "loaded libicuuc (ICU)"
}

# --- controls: each check must be able to see both presence and absence -------
has_lib libc.so.6 && ok "control: the library check finds libc.so.6" || fail "control: the library check cannot find libc.so.6 — it is broken"
in_root dpkg-deb -x /tmp/pkg.deb /tmp/bare
missing_before=$(scan /tmp/bare/usr/lib/ac-launcher | sort -u)
if grep -q '^linked libfontconfig.so.1$' <<< "$missing_before"; then
    ok "control: before install, ldd reports libfontconfig.so.1 missing"
else
    fail "control: ldd did not report libfontconfig.so.1 missing on the bare root — the linked-library check is blind"
fi
if grep -q '^loaded libX11.so.6$' <<< "$missing_before"; then
    ok "control: before install, libX11.so.6 is reported missing ($(grep -c . <<< "$missing_before") gaps in all)"
else
    fail "control: libX11.so.6 was not reported missing on the bare root — the runtime-loaded check is blind"
fi
rm -rf "$root/tmp/bare"

# --- install with apt ---------------------------------------------------------
if in_root apt-get -qq update > "$work/apt.log" 2>&1 &&
   in_root apt-get -y -qq install --no-install-recommends /tmp/pkg.deb >> "$work/apt.log" 2>&1; then
    ok "apt installed the package and its dependencies"
else
    fail "apt could not install the package:"
    tail -15 "$work/apt.log" | sed 's/^/           | /'
    exit 1
fi
ver=$(in_root dpkg-query -W -f='${Version}' ac-launcher 2>/dev/null)
ok "ac-launcher $ver installed; $(in_root dpkg-query -W -f='${Depends}' ac-launcher | tr ',' '\n' | wc -l) dependency clauses"

missing_after=$(scan /usr/lib/ac-launcher | sort -u)
if [[ -z $missing_after ]]; then
    ok "every linked and runtime-loaded library is present"
else
    fail "still missing after install: $(echo $missing_after)"
fi

for tool in tar python3 xdg-open; do
    in_root sh -c "command -v $tool" > /dev/null && ok "$tool present" || fail "$tool missing"
done
[[ -s $root/etc/ssl/certs/ca-certificates.crt ]] && ok "CA certificates present" || fail "CA certificate bundle missing"
[[ -f $root/usr/share/applications/ac-launcher.desktop ]] && ok "desktop entry installed" || fail "desktop entry missing"
[[ -f $root/usr/share/icons/hicolor/256x256/apps/ac-launcher.png ]] && ok "icon installed" || fail "icon missing"

# --- launch inside the root ---------------------------------------------------
if [[ -z ${DISPLAY:-} ]]; then
    printf '  \033[33mSKIPPED\033[0m  launch: no DISPLAY\n'
else
    chroot "$root" /usr/bin/env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin HOME=/tmp/home LANG=C.UTF-8 \
        DISPLAY="$DISPLAY" "${xauth_env[@]}" AC_LAUNCHER_HOME=/tmp/achome \
        timeout -k 5 20 /usr/bin/ac-launcher > "$work/launch.log" 2>&1
    code=$?
    if (( code == 124 )); then ok "ran for 20 s and closed on TERM"; else fail "exited with code $code"; sed 's/^/           | /' "$work/launch.log" | tail -15; fi
    log=$root/tmp/achome/state/logs/ac-launcher.log
    if [[ -f $log ]] && grep -q 'starting' "$log"; then ok "app log written"; else fail "no app log"; fi
    if [[ -f $log ]] && grep -q 'Server list Community:' "$log"; then
        ok "HTTPS download worked: $(grep -m1 'Server list Community:' "$log" | cut -c31-)"
    else
        fail "server lists were not downloaded (TLS, certificates or ICU?)"
        [[ -f $log ]] && tail -5 "$log" | sed 's/^/           | /'
    fi
    if grep -qiE 'DllNotFoundException|Unable to load shared library|A fatal error|Couldn.t find a valid ICU' "$work/launch.log"; then
        fail "runtime reported a missing library:"
        grep -iE 'DllNotFoundException|Unable to load shared library|A fatal error|Couldn.t find a valid ICU' "$work/launch.log" | head -5 | sed 's/^/           | /'
    fi
fi
exit $failed
INNER
    status=$?
    (( status != 0 )) && rc=1
    # Files created inside the namespace belong to mapped ids; remove them from inside it.
    unshare --map-auto --map-root-user rm -rf "$WORK"
done

printf '\n'
(( rc == 0 )) && printf '\033[32mDEPENDENCY CHECK PASSED\033[0m\n' || printf '\033[31mDEPENDENCY CHECK FAILED\033[0m\n'
exit $rc
