#!/usr/bin/env bash
# Builds the Linux release artifacts into packaging/out:
#   ac-launcher-<ver>-linux-x64.tar.gz   self-contained app in ac-launcher-<ver>/, runs from anywhere;
#                                        share/ holds the desktop entry and icons (the AUR package uses them)
#   ac-launcher_<ver>_amd64.deb          system install to /usr/lib/ac-launcher
# Run packaging/smoke-test.sh, packaging/verify-deb-deps.sh and packaging/verify-arch-pkg.sh on the
# results before releasing.
set -euo pipefail
cd "$(dirname "$0")/.."

VER=$(grep -oPm1 '(?<=<Version>)[^<]+' Directory.Build.props)
OUT=packaging/out
PUB=$OUT/ac-launcher-$VER
rm -rf "$OUT"
mkdir -p "$PUB"

# Timestamps come from the last commit, so rebuilding the same commit gives the same metadata.
export SOURCE_DATE_EPOCH=${SOURCE_DATE_EPOCH:-$(git log -1 --format=%ct)}

echo "── publish v$VER (self-contained linux-x64)"
dotnet publish src/ACLauncher/ACLauncher.csproj -c Release -r linux-x64 --self-contained true \
    -p:DebugType=none -p:ContinuousIntegrationBuild=true -o "$PUB" -v q
[[ -x $PUB/ACLauncher && -f $PUB/ACLauncher.dll ]] || { echo "publish produced no ACLauncher" >&2; exit 1; }
# The runtime's LTTng event-tracing provider links liblttng-ust, which no desktop system needs;
# without the provider the runtime simply skips LTTng tracing.
rm -f "$PUB/libcoreclrtraceptprovider.so"

# SkiaSharp's native libraries ship with their symbol tables; the dynamic symbols stay.
strip --strip-unneeded "$PUB/libSkiaSharp.so" "$PUB/libHarfBuzzSharp.so"

# dotnet publish marks every file executable. Only the programs are.
find "$PUB" -type f -exec chmod 644 {} +
chmod 755 "$PUB/ACLauncher" "$PUB/createdump"

install -m644 LICENSE "$PUB/LICENSE"
install -m644 THIRD-PARTY-NOTICES.md "$PUB/THIRD-PARTY-NOTICES.md"
install -Dm644 packaging/ac-launcher.desktop "$PUB/share/applications/ac-launcher.desktop"
for size in 32 48 64 128 256; do
    install -Dm644 "packaging/icons/ac-launcher-$size.png" "$PUB/share/icons/hicolor/${size}x${size}/apps/ac-launcher.png"
done
mkdir -p "$PUB/share/man/man1"
sed -e "s/@VERSION@/$VER/" -e "s/@DATE@/$(date -u -d "@$SOURCE_DATE_EPOCH" +%Y-%m-%d)/" packaging/ac-launcher.1 |
    gzip -9n > "$PUB/share/man/man1/ac-launcher.1.gz"
chmod 644 "$PUB/share/man/man1/ac-launcher.1.gz"

echo "── tarball"
tar --sort=name --mtime="@$SOURCE_DATE_EPOCH" --owner=0 --group=0 --numeric-owner \
    -C "$OUT" -czf "$OUT/ac-launcher-$VER-linux-x64.tar.gz" "ac-launcher-$VER"

echo "── deb"
DEB=$OUT/debroot
DOC=$DEB/usr/share/doc/ac-launcher
mkdir -p "$DEB/DEBIAN" "$DEB/usr/lib/ac-launcher" "$DEB/usr/bin" "$DOC" "$DEB/usr/share/lintian/overrides"
cp -a "$PUB/." "$DEB/usr/lib/ac-launcher/"
cp -a "$DEB/usr/lib/ac-launcher/share/." "$DEB/usr/share/"
rm -rf "$DEB/usr/lib/ac-launcher/share" "$DEB/usr/lib/ac-launcher/LICENSE" "$DEB/usr/lib/ac-launcher/THIRD-PARTY-NOTICES.md"
install -m644 packaging/debian/copyright "$DOC/copyright"
install -m644 THIRD-PARTY-NOTICES.md "$DOC/THIRD-PARTY-NOTICES.md"
install -m644 packaging/debian/lintian-overrides "$DEB/usr/share/lintian/overrides/ac-launcher"
{
    printf 'ac-launcher (%s) unstable; urgency=medium\n\n' "$VER"
    printf '  * Release %s. Release notes:\n' "$VER"
    printf '    https://github.com/codingncaffeine/AC-Launcher/releases/tag/v%s\n\n' "$VER"
    printf ' -- codingncaffeine <codingncaffeine@users.noreply.github.com>  %s\n' "$(date -u -R -d "@$SOURCE_DATE_EPOCH")"
} | gzip -9n > "$DOC/changelog.gz"
chmod 644 "$DOC/changelog.gz"
cat > "$DEB/usr/bin/ac-launcher" <<'WRAP'
#!/bin/sh
exec /usr/lib/ac-launcher/ACLauncher "$@"
WRAP
chmod 755 "$DEB/usr/bin/ac-launcher"
find "$DEB" -type d -exec chmod 755 {} +

# Depends: every shared library the payload links or loads at run time (checked by
# packaging/verify-deb-deps.sh on clean Debian and Ubuntu roots), plus the programs the
# launcher runs: python3 runs umu-launcher's self-contained release, xdg-open opens links and
# folders. (tar, which unpacks Proton, is Essential and never listed.)
INSTALLED_KB=$(du -sk "$DEB/usr" | cut -f1)
cat > "$DEB/DEBIAN/control" <<CTRL
Package: ac-launcher
Version: $VER
Section: utils
Priority: optional
Architecture: amd64
Installed-Size: $INSTALLED_KB
Depends: libc6, libgcc-s1, libstdc++6, libicu76 | libicu74 | libicu72 | libicu70, libssl3t64 | libssl3, ca-certificates, libx11-6, libxext6, libxrandr2, libxi6, libxcursor1, libxfixes3, libice6, libsm6, libfontconfig1, python3, xdg-utils
Recommends: libgl1, libegl1, libgtk-3-0t64 | libgtk-3-0, libsecret-tools
Maintainer: codingncaffeine <codingncaffeine@users.noreply.github.com>
Homepage: https://github.com/codingncaffeine/AC-Launcher
Description: Launcher for Asheron's Call emulator servers
 Keeps accounts and servers in one place and starts one or many game
 clients at once. The Windows game client runs through umu-launcher and
 Proton, without Steam; Proton versions are downloaded and managed by
 the launcher.
CTRL
dpkg-deb --build --root-owner-group "$DEB" "$OUT/ac-launcher_${VER}_amd64.deb" > /dev/null
rm -rf "$DEB"

echo "── artifacts"
ls -sh1 "$OUT"/*.tar.gz "$OUT"/*.deb
