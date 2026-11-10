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

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# libmsquic (QUIC, sharing sticks over the internet) only exists in Microsoft's repository, so the package can
# only suggest it. Best effort on every check, so existing installations get it too.
ensure_msquic() {
    dpkg -s libmsquic >/dev/null 2>&1 && return 0
    [ -r /etc/os-release ] || return 1
    local ID="" VERSION_ID="" distro ver
    . /etc/os-release
    case "$ID" in
        ubuntu) distro=ubuntu; ver="$VERSION_ID" ;;
        debian|raspbian) distro=debian; ver="${VERSION_ID%%.*}" ;;
        *) return 1 ;;
    esac
    [ -n "$ver" ] || return 1
    if ! dpkg -s packages-microsoft-prod >/dev/null 2>&1; then
        curl -fsSL --retry 3 -o "$TMP/ms.deb" "https://packages.microsoft.com/config/$distro/$ver/packages-microsoft-prod.deb" || return 1
        dpkg -i "$TMP/ms.deb" || return 1
    fi
    apt-get update -qq || return 1
    DEBIAN_FRONTEND=noninteractive apt-get install -y libmsquic || return 1
    MSQUIC_NEW=1
}
MSQUIC_NEW=0
if ! dpkg -s libmsquic >/dev/null 2>&1; then
    if ensure_msquic; then echo "libmsquic installiert"; else echo "libmsquic nicht installierbar, Teilen über das Internet bleibt aus"; fi
fi

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
    # QUIC is detected at startup, so a freshly installed libmsquic needs a restart
    [ "$MSQUIC_NEW" = 1 ] && systemctl restart rtltcp-server
    status current "Bereits aktuell (Version $INSTALLED)"
    exit 0
fi

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
