#!/usr/bin/env bash
# rtltcp.server – installs the latest release. Runs as root via rtltcp-server-update.service, which is started by
# rtltcp-server-update.path as soon as the server (unprivileged user rtltcp) creates
# /var/lib/rtltcp-server/update-request (button in the web interface or the daily automatic update).
#
# Only ever installs the official package from the fixed address below, after checking its SHA-256.
# The request file's content is ignored. Result for the web interface: /var/lib/rtltcp-server/update-status
set -euo pipefail

BASE="__BASE_URL__"
STATE_DIR=/var/lib/rtltcp-server
STATUS="$STATE_DIR/update-status"

rm -f "$STATE_DIR/update-request"

status() { # <state> <message>
    local msg="${2//\"/\'}"
    printf '{"state":"%s","message":"%s","time":"%s"}\n' "$1" "$msg" "$(date -Is)" > "$STATUS.tmp"
    chown rtltcp:rtltcp "$STATUS.tmp" 2>/dev/null || true
    mv -f "$STATUS.tmp" "$STATUS"
    echo "$1: $2"
}
trap 'status failed "Update-Skript abgebrochen (Zeile $LINENO), siehe journalctl -u rtltcp-server-update"' ERR

ARCH="$(dpkg --print-architecture)"
INSTALLED="$(dpkg-query -W -f='${Version}' rtltcp-server 2>/dev/null || echo 0)"
status checking "Suche nach Updates (installiert: $INSTALLED)"

MANIFEST="$(curl -fsSL --retry 3 "$BASE/linux/release/latest/version.json")"
VERSION="$(printf '%s' "$MANIFEST" | sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' | head -n1)"
LINE="$(printf '%s\n' "$MANIFEST" | grep "\"$ARCH\"" || true)"
URL="$(printf '%s' "$LINE" | sed -n 's/.*"url": *"\([^"]*\)".*/\1/p')"
SHA="$(printf '%s' "$LINE" | sed -n 's/.*"sha256": *"\([^"]*\)".*/\1/p')"
if [ -z "$VERSION" ] || [ -z "$URL" ] || [ -z "$SHA" ]; then
    status failed "Kein Paket für $ARCH in version.json"
    exit 1
fi
case "$URL" in "$BASE"/*) ;; *) status failed "Unerwartete Download-Adresse"; exit 1 ;; esac

if dpkg --compare-versions "$VERSION" le "$INSTALLED"; then
    status current "Bereits aktuell (Version $INSTALLED)"
    exit 0
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
DEB="$TMP/rtltcp-server_${VERSION}_${ARCH}.deb"
status installing "Lade Version $VERSION"
curl -fsSL --retry 3 -o "$DEB" "$URL"
if ! echo "$SHA  $DEB" | sha256sum -c --quiet -; then
    status failed "Prüfsumme von Version $VERSION stimmt nicht"
    exit 1
fi
chmod 755 "$TMP"; chmod 644 "$DEB"

status installing "Installiere Version $VERSION"
# The package's postinst restarts rtltcp-server.
DEBIAN_FRONTEND=noninteractive apt-get install -y -o Dpkg::Options::=--force-confold "$DEB"
status done "Version $VERSION installiert (vorher $INSTALLED)"
