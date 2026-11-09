#!/usr/bin/env bash
# Builds the .deb from a published single-file binary.
#
#   packaging/linux/build-deb.sh <version> <deb-arch: amd64|arm64|armhf> <publish-dir> <out-dir> [update-base-url]
#
# Installs /opt/rtltcp-server/rtltcp-server, a systemd service (user rtltcp, data in /var/lib/rtltcp-server),
# a udev rule for the sticks, a blacklist for the DVB-T kernel driver and the update units
# (rtltcp-server-update.path/.service + update.sh, used by the web interface and the automatic update).
set -euo pipefail

VERSION="${1:?version missing}"
ARCH="${2:?deb architecture missing}"
PUBLISH="${3:?publish dir missing}"
OUT="${4:?output dir missing}"
BASE_URL="${5:-https://repository.jaycloud.at/repository/rtltcp.server}"

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

install -d "$STAGE/DEBIAN" "$STAGE/opt/rtltcp-server" "$STAGE/lib/systemd/system" "$STAGE/lib/udev/rules.d" \
  "$STAGE/etc/modprobe.d" "$STAGE/usr/bin" "$STAGE/usr/share/doc/rtltcp-server"

install -m 755 "$PUBLISH/rtltcp-server" "$STAGE/opt/rtltcp-server/rtltcp-server"
ln -s /opt/rtltcp-server/rtltcp-server "$STAGE/usr/bin/rtltcp-server"
sed "s#__BASE_URL__#$BASE_URL#g" "$HERE/update.sh" > "$STAGE/opt/rtltcp-server/update.sh"
chmod 755 "$STAGE/opt/rtltcp-server/update.sh"
install -m 644 "$HERE/rtltcp-server.service" "$STAGE/lib/systemd/system/rtltcp-server.service"
install -m 644 "$HERE/rtltcp-server-update.service" "$HERE/rtltcp-server-update.path" "$STAGE/lib/systemd/system/"
install -m 644 "$HERE/60-rtltcp-server.rules" "$STAGE/lib/udev/rules.d/60-rtltcp-server.rules"
install -m 644 "$HERE/rtltcp-server-blacklist.conf" "$STAGE/etc/modprobe.d/rtltcp-server-blacklist.conf"
install -m 644 "$ROOT/LICENSE.md" "$ROOT/THIRD-PARTY-NOTICES.md" "$ROOT/README.md" "$ROOT/CHANGELOG.md" \
  "$STAGE/usr/share/doc/rtltcp-server/"
install -m 755 "$HERE/postinst" "$HERE/prerm" "$HERE/postrm" "$STAGE/DEBIAN/"
echo "/etc/modprobe.d/rtltcp-server-blacklist.conf" > "$STAGE/DEBIAN/conffiles"

SIZE_KB="$(du -sk "$STAGE" | cut -f1)"
cat > "$STAGE/DEBIAN/control" <<EOF
Package: rtltcp-server
Version: $VERSION
Section: hamradio
Priority: optional
Architecture: $ARCH
Depends: libusb-1.0-0, libssl3t64 | libssl3 | libssl1.1, libc6, libstdc++6, libgcc-s1 | libgcc1, zlib1g, passwd, curl, ca-certificates
Suggests: libmsquic
Installed-Size: $SIZE_KB
Maintainer: jaycloud <noreply@jaycloud.at>
Homepage: https://github.com/JayLo1401/rtltcp.server
Description: rtl_tcp-compatible server for RTL-SDR sticks
 Drop-in replacement for rtl_tcp with web interface, automatic reconnect
 when the stick disappears, idle power-down, several sticks on separate ports
 and rtl_tcp Jay extended (status messages); sticks can be shared between sites.
EOF

mkdir -p "$OUT"
DEB="$OUT/rtltcp-server_${VERSION}_${ARCH}.deb"
dpkg-deb --build --root-owner-group -Zxz "$STAGE" "$DEB"
echo "Package: $DEB"
