# Changelog

Alle nennenswerten Änderungen je Version. Format angelehnt an [Keep a Changelog](https://keepachangelog.com/de/1.1.0/), Versionsnummern nach [Semantic Versioning](https://semver.org/lang/de/).

## [0.1.0] – 2026-11-05

Erster Entwicklungsstand, noch kein Release. Lizenz [PolyForm Noncommercial 1.0.0](LICENSE.md).

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
