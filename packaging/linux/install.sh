#!/usr/bin/env bash
# Installs or updates rtltcp.server from repository.jaycloud.at:
#   curl -fsSL __BASE_URL__/linux/release/latest/install.sh | bash
#
# On a fresh install it asks for the ports (web interface, first stick) and checks that they are free.
# Without a terminal, or with RTLTCP_WEB_PORT / RTLTCP_STREAM_PORT set, no questions are asked.
set -euo pipefail

BASE="${RTLTCP_BASE:-__BASE_URL__}"
DEFAULTS=/etc/default/rtltcp-server

say() { printf '%s\n' "$*"; }
die() { printf 'Fehler: %s\n' "$*" >&2; exit 1; }

command -v dpkg >/dev/null || die "Nur für Debian, Ubuntu, Raspberry Pi OS und Verwandte (dpkg/apt)."
command -v systemctl >/dev/null || die "systemd wird benötigt."
SUDO=""
if [ "$(id -u)" -ne 0 ]; then
    command -v sudo >/dev/null || die "Bitte als root ausführen oder sudo installieren."
    SUDO="sudo"
fi

case "$(dpkg --print-architecture)" in
    amd64) ARCH=amd64 ;;
    arm64) ARCH=arm64 ;;
    armhf) ARCH=armhf ;;
    *) die "Architektur $(dpkg --print-architecture) wird nicht unterstützt (amd64, arm64, armhf)." ;;
esac

port_in_use() {
    if command -v ss >/dev/null; then ss -Hltn "sport = :$1" | grep -q .; else return 1; fi
}

ask_port() { # <question> <default> <other port that must differ>
    local p
    while true; do
        read -r -p "$1 [$2]: " p </dev/tty || p=""
        p="${p:-$2}"
        if ! [[ "$p" =~ ^[0-9]+$ ]] || [ "$p" -lt 1 ] || [ "$p" -gt 65535 ]; then say "  Bitte eine Zahl von 1 bis 65535." >&2; continue; fi
        if [ "$p" = "${3:-}" ]; then say "  Dieser Port ist schon vergeben." >&2; continue; fi
        if port_in_use "$p"; then say "  Port $p ist bereits belegt – bitte einen anderen wählen." >&2; continue; fi
        printf '%s' "$p"; return
    done
}

# Ports: only on a fresh install (an update keeps the existing configuration)
if [ ! -f "$DEFAULTS" ] && ! dpkg -s rtltcp-server >/dev/null 2>&1; then
    WEB="${RTLTCP_WEB_PORT:-}"
    STREAM="${RTLTCP_STREAM_PORT:-}"
    if [ -z "$WEB$STREAM" ] && [ -r /dev/tty ] && [ -w /dev/tty ]; then
        say "rtltcp.server – Einrichtung"
        WEB="$(ask_port "Port der Weboberfläche" 8080 "")"
        STREAM="$(ask_port "Port für den ersten Stick (rtl_tcp)" 1234 "$WEB")"
    fi
    WEB="${WEB:-8080}"; STREAM="${STREAM:-1234}"
    port_in_use "$WEB" && die "Port $WEB ist belegt. Mit RTLTCP_WEB_PORT=<port> einen anderen wählen."
    port_in_use "$STREAM" && die "Port $STREAM ist belegt. Mit RTLTCP_STREAM_PORT=<port> einen anderen wählen."
    printf '# rtltcp.server – vom Installer angelegt. Nach Änderungen: sudo systemctl restart rtltcp-server\nRTLTCP_WEB_PORT=%s\nRTLTCP_STREAM_PORT=%s\n' \
        "$WEB" "$STREAM" | $SUDO tee "$DEFAULTS" >/dev/null
fi

MANIFEST="$(curl -fsSL "$BASE/linux/release/latest/version.json")"
VERSION="$(printf '%s' "$MANIFEST" | sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' | head -n1)"
LINE="$(printf '%s\n' "$MANIFEST" | grep "\"$ARCH\"")" || die "Kein Paket für $ARCH in version.json."
URL="$(printf '%s' "$LINE" | sed -n 's/.*"url": *"\([^"]*\)".*/\1/p')"
SHA="$(printf '%s' "$LINE" | sed -n 's/.*"sha256": *"\([^"]*\)".*/\1/p')"
[ -n "$VERSION" ] && [ -n "$URL" ] && [ -n "$SHA" ] || die "version.json unvollständig."

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
DEB="$TMP/rtltcp-server_${VERSION}_${ARCH}.deb"
say "Lade rtltcp.server $VERSION ($ARCH) …"
curl -fsSL -o "$DEB" "$URL"
echo "$SHA  $DEB" | sha256sum -c --quiet - || die "Prüfsumme stimmt nicht."
chmod 644 "$DEB"; chmod 755 "$TMP"
$SUDO apt-get install -y "$DEB"
say "rtltcp.server $VERSION installiert."
