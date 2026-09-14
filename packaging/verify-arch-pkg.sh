#!/usr/bin/env bash
# Proves the Arch package's depends are complete: installs the built .pkg.tar.zst with pacman on a
# clean Arch root (the official bootstrap tarball), letting pacman resolve every dependency from the
# repositories, then checks every library the payload links or loads at run time, the programs the
# launcher runs, and launches the app inside the root.
# Before installing, the same library check runs against the bare payload as a control: it must
# report missing libraries there, or it could not see a gap at all.
#
# The throwaway root skips package signature checks (it is deleted afterwards and never touches
# the host); packages still come over HTTPS from the official mirror.
# Runs unprivileged in a user namespace (needs /etc/subuid, network, a graphical session for the launch).
# Usage: packaging/verify-arch-pkg.sh <package.pkg.tar.zst> <archlinux-bootstrap-x86_64.tar.zst>
set -uo pipefail

(( $# == 2 )) || { echo "usage: $(basename "$0") <package.pkg.tar.zst> <archlinux-bootstrap.tar.zst>" >&2; exit 2; }
PKG=$(realpath "$1")
BOOTSTRAP=$(realpath "$2")
WORK=$(mktemp -d)

unshare --map-auto --map-root-user --mount --pid --fork bash -s "$WORK" "$BOOTSTRAP" "$PKG" <<'INNER'
set -uo pipefail
work=$1 bootstrap=$2 pkg=$3
failed=0
fail() { printf '  \033[31mFAIL\033[0m  %s\n' "$*"; failed=1; }
ok()   { printf '  \033[32mok\033[0m    %s\n' "$*"; }

bsdtar -xf "$bootstrap" -C "$work" 2>/dev/null
root=$work/root.x86_64
[[ -d $root ]] || { fail "bootstrap did not unpack to root.x86_64"; exit 1; }
mount --bind "$root" "$root"
mount -t proc proc "$root/proc" || { fail "could not mount /proc in the root"; exit 1; }
mount --rbind /dev "$root/dev"
mount --rbind /sys "$root/sys"
mount -t tmpfs tmpfs "$root/tmp"
cp -L /etc/resolv.conf "$root/etc/resolv.conf"
cp "$pkg" "$root/tmp/"
pkgfile=/tmp/$(basename "$pkg")
mkdir -p "$root/tmp/.X11-unix"
mount --bind /tmp/.X11-unix "$root/tmp/.X11-unix"
xauth_env=()
if [[ -n ${XAUTHORITY:-} && -f $XAUTHORITY ]]; then
    cp "$XAUTHORITY" "$root/tmp/xauth"
    xauth_env=(XAUTHORITY=/tmp/xauth)
fi
echo 'Server = https://geo.mirror.pkgbuild.com/$repo/os/$arch' > "$root/etc/pacman.d/mirrorlist"
sed -i -e 's/^SigLevel.*/SigLevel = Never/' -e 's/^#DisableSandbox/DisableSandbox/' "$root/etc/pacman.conf"
grep -q '^DisableSandbox' "$root/etc/pacman.conf" || sed -i 's/^\[options\]/[options]\nDisableSandbox/' "$root/etc/pacman.conf"

in_root() {
    chroot "$root" /usr/bin/env -i PATH=/usr/bin HOME=/root LANG=C.UTF-8 "$@"
}

dlopen_libs=(libssl.so.3 libcrypto.so.3 libX11.so.6 libXext.so.6 libXrandr.so.2 libXi.so.6
             libXcursor.so.1 libXfixes.so.3 libICE.so.6 libSM.so.6)

scan() {   # $1 = payload dir inside the root; prints "linked <lib>" / "loaded <lib>" per missing library
    local dir=$1 elf lib
    while IFS= read -r elf; do
        in_root ldd "$elf" 2>&1 | awk '/not found/ { print "linked", $1 }'
    done < <(in_root find "$dir" -type f \( -name '*.so' -o -name ACLauncher \))
    for lib in "${dlopen_libs[@]}"; do
        [[ -e $root/usr/lib/$lib ]] || echo "loaded $lib"
    done
    compgen -G "$root/usr/lib/libicuuc.so.[0-9]*" > /dev/null || echo "loaded libicuuc (ICU)"
}

ok "root: $(. "$root/etc/os-release"; echo "$PRETTY_NAME")"
if ! in_root pacman -Sy --noconfirm glibc > "$work/pacman.log" 2>&1; then
    fail "pacman could not sync the repositories:"; tail -10 "$work/pacman.log" | sed 's/^/           | /'; exit 1
fi

mkdir -p "$root/tmp/bare"
bsdtar -xf "$root$pkgfile" -C "$root/tmp/bare"
missing_before=$(scan /tmp/bare/usr/lib/ac-launcher | sort -u)
[[ -e $root/usr/lib/libc.so.6 ]] && ok "control: the library check finds libc.so.6" || fail "control: libc.so.6 not found — the library check is broken"
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

if in_root pacman -U --noconfirm "$pkgfile" >> "$work/pacman.log" 2>&1; then
    ok "pacman installed $(in_root pacman -Q ac-launcher-bin) and its dependencies"
else
    fail "pacman could not install the package:"; tail -15 "$work/pacman.log" | sed 's/^/           | /'; exit 1
fi

missing_after=$(scan /usr/lib/ac-launcher | sort -u)
[[ -z $missing_after ]] && ok "every linked and runtime-loaded library is present" || fail "still missing after install: $(echo $missing_after)"
for tool in tar python3 xdg-open; do
    [[ -x $root/usr/bin/$tool ]] && ok "$tool present" || fail "$tool missing"
done
[[ -s $root/etc/ssl/certs/ca-certificates.crt ]] && ok "CA certificates present" || fail "CA certificate bundle missing"
[[ -f $root/usr/share/applications/ac-launcher.desktop ]] && ok "desktop entry installed" || fail "desktop entry missing"
[[ -f $root/usr/share/icons/hicolor/256x256/apps/ac-launcher.png ]] && ok "icon installed" || fail "icon missing"
[[ -f $root/usr/share/licenses/ac-launcher-bin/LICENSE ]] && ok "license installed" || fail "license missing"

if [[ -z ${DISPLAY:-} ]]; then
    printf '  \033[33mSKIPPED\033[0m  launch: no DISPLAY\n'
else
    chroot "$root" /usr/bin/env -i PATH=/usr/bin HOME=/tmp/home LANG=C.UTF-8 DISPLAY="$DISPLAY" "${xauth_env[@]}" \
        AC_LAUNCHER_HOME=/tmp/achome timeout -k 5 20 /usr/bin/ac-launcher > "$work/launch.log" 2>&1
    code=$?
    (( code == 124 )) && ok "ran for 20 s and closed on TERM" || { fail "exited with code $code"; tail -15 "$work/launch.log" | sed 's/^/           | /'; }
    log=$root/tmp/achome/state/logs/ac-launcher.log
    [[ -f $log ]] && grep -q 'starting' "$log" && ok "app log written" || fail "no app log"
    if [[ -f $log ]] && grep -q 'Server list Community:' "$log"; then
        ok "HTTPS download worked: $(grep -m1 'Server list Community:' "$log" | cut -c31-)"
    else
        fail "server lists were not downloaded (TLS, certificates or ICU?)"
    fi
    if grep -qiE 'DllNotFoundException|Unable to load shared library|A fatal error|Couldn.t find a valid ICU' "$work/launch.log"; then
        fail "runtime reported a missing library:"
        grep -iE 'DllNotFoundException|Unable to load shared library|A fatal error|Couldn.t find a valid ICU' "$work/launch.log" | head -5 | sed 's/^/           | /'
    fi
fi
exit $failed
INNER
status=$?
unshare --map-auto --map-root-user rm -rf "$WORK"
printf '\n'
(( status == 0 )) && printf '\033[32mARCH DEPENDENCY CHECK PASSED\033[0m\n' || printf '\033[31mARCH DEPENDENCY CHECK FAILED\033[0m\n'
exit $status
