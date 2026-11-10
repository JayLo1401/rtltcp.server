# Changelog

Alle nennenswerten Änderungen je Version. Format angelehnt an [Keep a Changelog](https://keepachangelog.com/de/1.1.0/), Versionsnummern nach [Semantic Versioning](https://semver.org/lang/de/).

Neue Einträge kommen unter „[Unreleased]“. Beim Release macht der Workflow daraus automatisch die neue Version
mit Datum. Steht dort nichts, werden die Commit-Titel seit dem letzten Release eingetragen.

## [Unreleased]

## [1.11.1] – 2026-11-10

### Behoben
- Hängenden Stick erkennen und per USB-Reset neu öffnen

## [1.11.0] – 2026-11-10

### Neu

- Entfernten Stick hinzufügen in zwei Schritten: Instanz wählen, dann einen der dort freigegebenen Sticks. Name,
  Standort und Antenne werden vom teilenden Server übernommen.
- Statusseite zeigt die echte Datenrate in Mbit/s: erwartet (Samplerate × 16 Bit), vom Stick bzw. aus dem Internet
  empfangen, an den Client bzw. ins Internet gesendet, und verworfene Puffer, wenn die Leitung zu langsam ist.

### Geändert

- Das Feld „Adresse“ bei „Instanz hinzufügen“ ist leer: rtl.over.internet hostet jeder selbst.

### Entfernt

- Direkt teilen ohne rtl.over.internet (Testbetrieb mit fester Adresse, UDP-Port und Fingerprint). Solche Geräte
  müssen über „+ Entfernter Stick“ neu angelegt werden.

## [1.10.0] – 2026-11-10

### Neu

- Instanz hinzufügen per Zugangsdatei von rtl.over.internet: Datei hochladen, Adresse, Instanz-Code und Passwort
  werden ausgefüllt. Gespeichert wird weiterhin nur das Geräte-Token.

## [1.9.1] – 2026-11-10

### Geändert

- Changelog für 1.2.0 bis 1.9.0 nachgetragen. Er wird ab jetzt bei jedem Release automatisch fortgeschrieben,
  auch der im Home-Assistant-Add-on.

## [1.9.0] – 2026-11-10

### Neu

- Standort und Antenne pro Stick (freier Text, in der Geräteansicht einstellbar). Beides steht im Status, wird
  über rtl_tcp Jay extended an Clients geschickt (`server_info`, Liste der Sticks) und bei geteilten Sticks über
  rtl.over.internet mitgeliefert. Entfernte Sticks zeigen die Angaben des Anbieters.

## [1.8.0] – 2026-11-10

### Neu

- rtl_tcp Jay extended: `server_info` enthält Anzeigename, Port und Quelle des Sticks (`usb` oder `remote`, bei
  entfernten Sticks auch Instanz und Standort).
- Neuer Info-HELLO (`F2 "RTLI"`): Der Server schickt die Liste aller Sticks mit Status und trennt dann. So
  sehen Clients alle Sticks eines Servers, ohne einen zu öffnen (#5).
- Status zeigt pro Client, ob er `rtl_tcp` oder `rtl_tcp Jay extended` spricht.

## [1.7.0] – 2026-11-09

### Entfernt

- Anmeldung für Stream-Clients (SCRAM) und die Seite „Stream-Benutzer“. Wer einen Stick nutzen darf, regelt das
  Netz bzw. rtl.over.internet. Der Admin-Login der Weboberfläche bleibt.

## [1.6.0] – 2026-11-09

### Geändert

- Instanz von rtl.over.internet hinzufügen nur noch mit Adresse, Instanz-Code und Passwort. Gespeichert wird nur
  ein Geräte-Token.

## [1.5.0] – 2026-11-09

### Neu

- Sticks über rtl.over.internet teilen: Anmeldung der Instanz, Steuerkanal, Freigaben pro Instanz, UDP-Hole-Punching.
  Der Stream läuft verschlüsselt (QUIC) direkt zwischen den Servern, ohne VPN und ohne Portfreigabe.

## [1.4.1] – 2026-11-09

### Behoben

- Windows: „QUIC nicht verfügbar“ im Release-Build. `msquic.dll` liegt jetzt neben der exe.

## [1.4.0] – 2026-11-09

### Neu

- Teilen über das Internet in der Weboberfläche einstellen (Testbetrieb mit fester Adresse): eigene Kennung,
  Gegenstellen, freigegebene Sticks, entfernte Sticks als Gerät anlegen.

## [1.3.0] – 2026-11-09

### Neu

- Sticks direkt zwischen zwei Servern teilen (QUIC mit TLS 1.3, gegenseitige Prüfung per Fingerabdruck). Ein
  entfernter Stick erscheint lokal wie ein eigener Port, öffnet sich erst mit einem Client und verbindet sich nach
  Abbrüchen neu.

## [1.2.0] – 2026-11-06

### Geändert

- Windows: Die Deinstallation fragt, ob auch Einstellungen, Benutzer und Protokolle gelöscht werden sollen
  (Standard: behalten; still mit `/DELETEDATA`). Bei einer Neuinstallation mit vorhandenen Daten sagt die
  Abschlussseite jetzt, dass Einstellungen und Login übernommen wurden, statt `admin` / `admin` anzuzeigen.

## [1.1.0] – 2026-11-06

### Neu

- Windows: Tray-Symbol (`rtltcp-tray.exe`) zeigt, ob der Server läuft (grau = beendet, Warnsymbol = Stick fehlt),
  mit Tooltip pro Stick. Menü: Weboberfläche öffnen (auch per Doppelklick), Server neu starten, beenden oder
  starten (mit UAC-Abfrage), Tray-Symbol schließen. Startet bei jeder Anmeldung, abwählbar im Installer. Nach
  Updates startet es sich selbst neu (#7).
- Home Assistant: Das Add-on wird als fertiges Image von `ghcr.io` geladen statt auf dem Gerät gebaut.

### Behoben

- Home Assistant: Die Weboberfläche in der Seitenleiste wurde vom Browser blockiert (`ERR_BLOCKED_BY_RESPONSE`).
  Über den Ingress-Proxy darf sie jetzt von Home Assistant (gleiche Adresse) eingebettet werden.

## [1.0.1] – 2026-11-06

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
