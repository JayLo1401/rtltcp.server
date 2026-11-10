# Sticks zwischen Standorten teilen

Sticks werden über **rtl.over.internet** geteilt. Der Vermittler kümmert sich nur um Anmeldung und Verbindungsaufbau.
Der Stream läuft direkt zwischen den beiden Servern, ohne VPN und ohne Portfreigabe am Router.

## Einrichten

1. **Instanz hinzufügen** (beide Standorte): Einstellungen → „Sticks teilen über rtl.over.internet“. Dort Adresse,
   Instanz-Code und Passwort der Instanz eintragen. Statt abzutippen kann man die Zugangsdatei
   (`rtl-over-internet-<name>.json`, Instanzseite → „Zugangsdatei herunterladen“) hochladen, sie füllt die Felder aus.
   Gespeichert wird nur das Geräte-Token (`remote/tokens.json`, nur für den Besitzer lesbar), weder die Datei noch
   das Passwort.
2. **Freigeben** (Anbieter, Standort 1): Bei der Instanz die Sticks einschalten, die geteilt werden sollen, und
   „Freigaben speichern“. Name, Standort und Antenne des Sticks werden in der Instanz mit angezeigt.
3. **Nutzen** (Standort 2): Geräte → „+ Entfernter Stick“ → Instanz wählen → einen der freigegebenen Sticks
   auswählen → „Stick übernehmen“ → Speichern. Name, Standort und Antenne kommen vom Anbieter. Der Stick bekommt
   einen eigenen Port, wie ein USB-Stick.

## Prinzip

```
Standort 2 (Nutzer)                                   Standort 1 (Anbieter)
home.radio / SDR++ ──TCP v1/v2──▶ rtltcp.server ══QUIC/UDP══▶ rtltcp.server ──USB──▶ Stick
                                   :1235 = entfernter Stick
```

- Pro Verbindung vermittelt rtl.over.internet eine Sitzung: Kandidaten sammeln (öffentliche IPv6 zuerst,
  reflexive IPv4 über den Rendezvous-Endpunkt, LAN), Hole-Punching, dann QUIC direkt auf demselben UDP-Port.
  Ablauf und Pakete: `docs/protocol.md` in rtl.over.internet.
- **QUIC** (TLS 1.3, ALPN `rtltcp-jay/1`). Beide Server zeigen ein selbst erzeugtes Zertifikat
  (`<Datenverzeichnis>/remote/identity.pfx`). Geprüft wird **gegenseitig über den SHA-256-Fingerprint**, den
  rtl.over.internet für die Sitzung mitteilt. Ein anderes Zertifikat führt zum Abbruch des Handshakes.
- Pro Stick ein bidirektionaler QUIC-Stream. Der Nutzer sendet zuerst die Präambel `u8 Länge | Seriennummer`
  (UTF-8), danach läuft eine normale **rtl_tcp Jay extended**-Sitzung (v2, [protocol-v2.md](protocol-v2.md)):
  HELLO, `server_info`, `status`, IQ-Frames und Befehle.
- **Anbieter:** Der entfernte Client ist ein ganz normaler v2-Client des Sticks. Es gelten die gleiche
  Belegungsregel (wer zuerst kommt), die Idle-Abschaltung und der Reconnect. `BUSY` und die Statusseite zeigen ihn
  als „<Name des anderen Servers> (IP)“, und er lässt sich trennen.
- **Nutzer:** Der entfernte Stick ist ein Gerät wie ein USB-Stick, mit eigenem Port für v1 und v2.
  - Die QUIC-Verbindung entsteht erst, wenn ein lokaler Client kommt, und wird beim Trennen sofort
    abgebaut. Es läuft also kein Dauerstream (auch nicht mit Leerlaufzeit 0).
  - Bricht die Verbindung ab, verhält sich das wie ein abgesteckter Stick: `device_lost`, neu verbinden,
    `device_restored`, und die letzten Einstellungen werden erneut gesetzt.
  - Fehler erscheinen als `device_not_ready` mit Ursache, z. B. „Remote stick in use by …“,
    „Remote site: Stick … is not shared with this server“ oder „Remote site not responding“. Neue Versuche
    folgen mit Backoff (1 s bis 30 s).
- Keepalive alle 15 s, Idle-Timeout 30 s.

## Datenrate

Ein Stick liefert pro Sample 2 Byte (I und Q, je 8 Bit). Übertragen wird also **Samplerate × 16 Bit**:

| Samplerate | Datenrate |
|---|---|
| 2,4 MS/s | 38,4 Mbit/s |
| 2,048 MS/s | 32,8 Mbit/s |
| 1,024 MS/s | 16,4 Mbit/s |
| 0,25 MS/s | 4 Mbit/s |

Die Statusseite zeigt pro Stick die erwartete Rate und was tatsächlich fließt: beim Anbieter „Gesendet
(Internet)“, beim Nutzer „Empfangen (Internet)“ und „An den Client“. Liegt ein Wert deutlich unter der
erwarteten Rate oder werden Puffer verworfen, schafft die Leitung (meist der Upload des Anbieters) die Samplerate
nicht. Dann im Client eine kleinere Samplerate wählen.

Komprimiert wird nicht. An echten IQ-Daten gemessen spart verlustfreie Kompression nur etwa 8 % (Brotli, die
Daten sind fast reines Rauschen), Deflate macht sie sogar größer. Spürbar weniger geht nur über eine kleinere
Samplerate.

## Voraussetzungen

| System | QUIC |
|---|---|
| Windows 11, Server 2022 | eingebaut |
| Linux (Debian/Ubuntu/Raspberry Pi OS) | Paket `libmsquic` aus dem Microsoft-Repository (`packages.microsoft.com`). Installer und Update richten es automatisch ein. |
| Home-Assistant-Add-on | im Image enthalten |

Ohne QUIC läuft der Server normal weiter. Die Statusseite zeigt dann den Grund, warum Teilen nicht verfügbar ist.

Der frühere Testbetrieb mit fester Adresse und Fingerprint (bis 1.10) gibt es nicht mehr. Solche Geräte werden
als „nicht nutzbar“ angezeigt und müssen über „+ Entfernter Stick“ neu angelegt werden.
