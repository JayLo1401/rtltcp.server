# Sticks zwischen Standorten teilen

Zwei Wege:

1. **Über rtl.over.internet** (der normale Weg): Unter Einstellungen → „Sticks teilen über rtl.over.internet“
   meldet sich der Server mit Instanz-Code und Passwort der Instanz an. Statt abzutippen kann man die
   Zugangsdatei (`rtl-over-internet-<name>.json`, Instanzseite → „Zugangsdatei herunterladen“) hochladen, sie füllt
   Adresse, Code und Passwort aus. Die Datei selbst wird nicht gespeichert, nur das Geräte-Token
   (`remote/tokens.json`, nur für den Besitzer lesbar). Pro Instanz wählt man die freigegebenen Sticks. Am anderen
   Standort: Geräte → „+ Entfernter Stick“ → Quelle = die Instanz → Stick auswählen.
   Pro Verbindung vermittelt rtl.over.internet eine Sitzung: Kandidaten sammeln (öffentliche IPv6 zuerst,
   reflexive IPv4 über den Rendezvous-Endpunkt, LAN), Hole-Punching, dann QUIC direkt auf demselben UDP-Port.
   Am Router wird nichts freigegeben. Ablauf und Pakete: `docs/protocol.md` in rtl.over.internet.
2. **Direkt (Testbetrieb):** Gegenstelle, UDP-Port und Fingerprint von Hand (siehe unten). Der Anbieter muss
   dann per UDP erreichbar sein, im LAN oder über IPv6.

## Prinzip

```
Standort 2 (Nutzer)                                   Standort 1 (Anbieter)
home.radio / SDR++ ──TCP v1/v2──▶ rtltcp.server ══QUIC/UDP══▶ rtltcp.server ──USB──▶ Stick
                                   :1235 = entfernter Stick       remote.listenPort
```

- **QUIC** (TLS 1.3, ALPN `rtltcp-jay/1`). Beide Server zeigen ein selbst erzeugtes Zertifikat
  (`<Datenverzeichnis>/remote/identity.pfx`). Geprüft wird **gegenseitig über den SHA-256-Fingerprint**,
  nicht über eine CA. Ein unbekanntes Zertifikat führt zum Abbruch des Handshakes.
- Pro Stick ein bidirektionaler QUIC-Stream. Der Nutzer sendet zuerst die Präambel `u8 Länge | Seriennummer`
  (UTF-8), danach läuft eine normale **rtl_tcp Jay extended**-Sitzung (v2, [protocol-v2.md](protocol-v2.md)):
  HELLO, `server_info`, `status`, IQ-Frames und Befehle.
- **Anbieter:** Der entfernte Client ist ein ganz normaler v2-Client des Sticks. Es gelten die gleiche
  Belegungsregel (wer zuerst kommt), die Idle-Abschaltung und der Reconnect. `BUSY` und die Statusseite zeigen ihn
  als „<Name der Gegenstelle> (IP)“, und er lässt sich trennen.
- **Nutzer:** Der entfernte Stick ist ein Gerät wie ein USB-Stick, mit eigenem Port für v1 und v2.
  - Die QUIC-Verbindung entsteht erst, wenn ein lokaler Client kommt, und wird beim Trennen sofort
    abgebaut. Es läuft also kein Dauerstream (auch nicht mit Leerlaufzeit 0).
  - Bricht die Verbindung ab, verhält sich das wie ein abgesteckter Stick: `device_lost`, neu verbinden,
    `device_restored`, und die letzten Einstellungen werden erneut gesetzt.
  - Fehler erscheinen als `device_not_ready` mit Ursache, z. B. „Remote stick in use by …“,
    „Remote site: Stick … is not shared with this server“, „certificate fingerprint does not match“ oder
    „Remote site not responding“. Neue Versuche folgen mit Backoff (1 s bis 30 s).
- Keepalive alle 15 s, Idle-Timeout 30 s.

## Voraussetzungen

| System | QUIC |
|---|---|
| Windows 11, Server 2022 | eingebaut |
| Linux (Debian/Ubuntu/Raspberry Pi OS) | Paket `libmsquic` aus dem Microsoft-Repository (`packages.microsoft.com`) |
| Home-Assistant-Add-on | im Image enthalten |

Ohne QUIC läuft der Server normal weiter. Die Statusseite zeigt dann den Grund, warum Teilen nicht verfügbar ist.

## Testbetrieb einrichten

1. Beide Server einmal starten. Unten auf der Statusseite steht jeweils der **Fingerprint dieses Servers**
   (auch im Log: `Remote sharing: own certificate fingerprint …`).
2. **Anbieter** (`config.json`, danach Dienst neu starten):

   ```json
   "remote": {
     "listenPort": 4433,
     "bind": "::",
     "peers": [
       { "name": "Standort 2", "fingerprint": "<Fingerprint des Nutzers>", "devices": ["<Seriennummer des Sticks>"] }
     ]
   }
   ```

3. **Nutzer:** ein zusätzliches Gerät in `devices`:

   ```json
   { "serial": "", "name": "Standort 1 Stick 1", "enabled": true, "bind": "0.0.0.0", "port": 1235,
     "idleTimeoutSeconds": 30, "v1WaitForDeviceSeconds": 10,
     "defaults": { "frequency": 100000000, "sampleRate": 2048000, "gain": null, "ppm": 0, "biasTee": false },
     "remote": { "host": "192.168.100.202", "port": 4433,
                 "fingerprint": "<Fingerprint des Anbieters>", "share": "<Seriennummer des Sticks>" } }
   ```

4. Am Nutzer einen Client mit Port 1235 verbinden (home.radio, SDR++ …).

Unter **Geräte** wird der entfernte Stick als „Entfernter Stick“ angezeigt. Bearbeiten lässt er sich vorerst
nur in `config.json`.

## Noch offen

- Vermittler: Instanzen, Anmeldung per Code, Kandidaten und Fingerprints über den Steuerkanal, Hole-Punching
  (IPv6 zuerst), Lease und `revoke` (rtl.over.internet#4, hier #2 und #3)
- Weboberfläche für Instanzen, Freigaben und entfernte Sticks
- Status des entfernten Sticks (z. B. dort abgesteckt) an den lokalen v2-Client durchreichen. Bisher pausiert
  der IQ-Strom nur, und der Grund steht im Log.
- Anzeigenamen und Quelle per Jay extended (#5), Kompression (#4)
