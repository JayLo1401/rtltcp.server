# Changelog

Alle nennenswerten Änderungen je Version. Format angelehnt an [Keep a Changelog](https://keepachangelog.com/de/1.1.0/), Versionsnummern nach [Semantic Versioning](https://semver.org/lang/de/).

## [Unreleased]

### Behoben

- Home Assistant: Das Add-on startete nicht, wenn Port 8080 belegt war (#1). Die Einstellungen aus dem Dockerfile
  kamen beim Programm nicht an, deshalb lief die Weboberfläche auf dem Standardport 8080 statt auf 8099.

### Geändert

- Home Assistant: Der Zugang über die Seitenleiste nutzt jetzt einen Port, den Home Assistant frei wählt. Es gibt
  keine Kollision mehr mit anderen Add-ons.
- Home Assistant: neue Optionen `web_port` (direkter Zugriff, leer = nur über Home Assistant) und `stream_port`
  (erster Stick beim ersten Start). Ist `web_port` belegt, startet das Add-on trotzdem und meldet das im Protokoll.

## [1.0.0] – 2026-11-05

Erste veröffentlichte Version. Lizenz [PolyForm Noncommercial 1.0.0](LICENSE.md): frei für private und
nicht-kommerzielle Nutzung, kommerzielle Nutzung nicht erlaubt.

### Updates

- Automatische Updates unter Windows und Linux, täglich zu einer festen Uhrzeit (Standard 04:00, einstellbar). Ist
  gerade ein Client verbunden, wird bis zu einer Stunde gewartet.
- Hinweis auf neue Versionen in der Weboberfläche, Update per Knopfdruck.
- Windows: Der Installer wird geladen, per SHA-256 geprüft und still ausgeführt. Linux: Ein eigener root-Dienst
  (`rtltcp-server-update`) installiert nur das offizielle Paket; der Server selbst läuft ohne root-Rechte.
- Home Assistant: Updates wie gewohnt über Home Assistant.

### Installation

- Linux und Raspberry Pi mit einem Befehl (`install.sh`): fragt die Ports ab, installiert das `.deb` für x64,
  arm64 oder armhf, richtet systemd-Dienst, udev-Regel und Treibersperre ein.
- Windows-Installer: fragt die Ports ab, richtet den Windows-Dienst ein und öffnet die Firewall.
- Home-Assistant-Add-on mit Weboberfläche in der Seitenleiste.

### Weboberfläche

- Status, Geräte, Stream-Benutzer, Einstellungen und Protokoll im Design von jaycloud.at, hell und dunkel, auch am Handy.
- Erste Anmeldung `admin` / `admin` mit Pflicht zur Passwortänderung. Das Standardpasswort gilt nur im lokalen Netz.
- Schutz vor Fremd-Requests (CSRF) und Sperre nach zu vielen Fehlversuchen.

### Protokoll (Logs)

- Eine Logdatei pro Tag, standardmäßig 14 Tage aufbewahrt. In der Oberfläche live ansehen, filtern, herunterladen.

### Stream-Server

- Drop-in-Ersatz für `rtl_tcp`: v1-Clients (SDR++, SDR#, GQRX …) funktionieren unverändert.
- Ein Port pro Stick, Sticks werden über die Seriennummer zugeordnet. Beim ersten Start wird jeder gefundene Stick ab Port 1234 eingetragen.
- Erster Client gewinnt. Weitere v2-Clients bekommen `Server already in use by <IP>`, v1-Clients werden sofort getrennt statt zu hängen.
- Idle-Abschaltung: Der Stick wird nach einstellbarer Zeit ohne Client geschlossen (Tuner und Demodulator im Standby).
- Auto-Reconnect: Ist der Stick kurz weg, wird er per Seriennummer neu geöffnet, die letzten Einstellungen werden wieder gesetzt. Die Client-Verbindung bleibt bestehen.
- Läuft als Windows-Dienst oder unter systemd.

### Protokoll rtltcp_v2

- Aushandlung per HELLO, abwärtskompatibel in beide Richtungen ([Spezifikation](docs/protocol-v2.md)).
- Statusmeldungen: `ready`, `device_not_ready`, `device_lost`, `device_restored`. Fehler: `BUSY`, `AUTH_FAILED`, `KICKED`, `INVALID_COMMAND`.
- Optionale Authentifizierung mit SCRAM-SHA-256. Das Passwort geht nie über das Netz, der Server speichert nichts Passwortgleiches.
- Vertrauenswürdige Netze (Standard: private Adressbereiche) brauchen keine Anmeldung.

### Treiber

- Eigener RTL2832U-Treiber und R820T/R820T2/R828D-Tuner-Treiber in C#, über libusb, ohne GPL-Code.
- Getestet mit NooElec NESDR SMArt v5 (R820T2): 2,4 und 3,2 MS/s stabil, UKW-Empfang.
