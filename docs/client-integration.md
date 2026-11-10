# rtl_tcp Jay extended in einen Client einbauen

Anleitung für Client-Entwickler (z. B. home.radio). Die verbindliche Spezifikation ist
[protocol-v2.md](protocol-v2.md). Hier steht, *wie* man sie sinnvoll in eine App einbaut.

## Grundsätze

- **Keine Einstellung für das Protokoll.** Der Client sendet immer HELLO und nimmt, was der Server anbietet:
  `RTL2` → **rtl_tcp Jay extended** (v2), `RTL0` → **rtl_tcp** (v1). Das erkannte Protokoll wird nur angezeigt.
- **Keine Anmeldung.** Clients verbinden sich lokal mit dem rtltcp.server. Den Weg über das Internet übernehmen die
  rtltcp.server untereinander (rtl.over.internet), ein entfernter Stick ist für den Client ein ganz normaler Port.
- Gegenüber einem klassischen `rtl_tcp` verhält sich der Client exakt wie bisher (HELLO ist dort ein unbekannter,
  ignorierter Befehl).

## Ablauf

```text
connect()
send  F2 52 54 4C 32                         HELLO (5 Bytes, sofort, vor dem ersten Lesen)
read  4 Bytes
  "RTL0" → klassischer Server: restliche 8 Header-Bytes lesen, weiter als v1, anzeigen „rtl_tcp“
  "RTL2" → restliche 8 Bytes lesen (u16 version, u16 flags, u32 reserved), ab jetzt nur Frames,
           anzeigen „rtl_tcp Jay extended“
  EOF    → Server hat abgelehnt (belegt bei v1-Regel, Stick nicht bereit)
read Frame → server_info (Anzeigename des Sticks in device.name)
read Frames in Schleife:
  type 0x01 IQ      → Payload exakt wie bisher die rohen IQ-Bytes an die Verarbeitung geben
  type 0x02 JSON    → Status/Fehler anzeigen (Tabelle unten)
  unbekannter Typ   → Länge überspringen
Befehle senden: Frame type 0x03, Payload = der bekannte 5-Byte-rtl_tcp-Befehl
```

Frame-Aufbau: `u8 type | u32 length (big-endian) | payload`. Unbekannte JSON-Felder und -Typen ignorieren.

**Wichtig:** Im v2-Modus kommen IQ-Daten nur in Frames. Der bestehende IQ-Lesecode muss die 5 Byte Frame-Header
herausnehmen, sonst entstehen Störungen im Signal. Am saubersten ist eine eigene Lese-Schicht, die Frames zerlegt
und die IQ-Payloads in denselben Puffer bzw. Callback gibt wie bisher.

Die Gain-Liste kommt bei v2 nicht im Header, sondern in `status.tuner.gains` (Zehntel-dB), der Tuner-Typ in
`status.tuner.type` (gleiche Nummern wie im v1-Header).

## Stick anzeigen und auswählen

- **Anzeigename:** `server_info.device.name` ist der Name aus der Weboberfläche des Servers (z. B. „Dach-Antenne“
  oder „Standort1 Stick 1“). Ihn statt Adresse und Port anzeigen.
- **Herkunft** (ab rtltcp.server 1.8): `device.source` = `usb` (Stick am Server) oder `remote` (Stick an einem anderen
  Standort, dann `device.remote.site` und `device.remote.instance`), z. B. „Standort1 Stick 1 · über Funkfreund Max“.
- **Alle Sticks eines Servers** (z. B. für eine Auswahlliste): Verbindung zu irgendeinem Stick-Port, statt HELLO
  `F2 52 54 4C 49` („RTLI“) senden → Header, `server_info`, `devices` mit Port, Name, Quelle, Zustand und
  `busy_by`, danach schließt der Server. Belegt nichts. Antwortet der Server mit `RTL0`, ist es ein klassisches
  `rtl_tcp` (Verbindung schließen, nur der eingetragene Port ist bekannt).
- **Standort und Antenne** (ab 1.9): `device.location` und `device.antenna`, Freitext, auch in `devices`. Fehlen sie:
  „nicht angegeben“; bei v1 (`RTL0`): „Server unterstützt dies nicht“. Bei entfernten Sticks sind es die Werte des
  teilenden Servers.
- Fehlen die Felder (ältere Server), nur `name` anzeigen.

## Meldungen → Anzeige

| JSON                                          | Bedeutung                              | Empfehlung in der App                                  |
|-----------------------------------------------|----------------------------------------|--------------------------------------------------------|
| `status` `ready`                              | Stick bereit, IQ läuft                 | normal, ggf. Gain-Liste übernehmen                     |
| `status` `starting`                           | Stick wird geöffnet (nach Idle)        | „Empfänger startet …“                                  |
| `status` `device_not_ready`                   | kein Stick am Server bzw. entfernter Stick nicht erreichbar | `message` anzeigen, Verbindung halten |
| `status` `device_lost`                        | Stick weg, Server verbindet neu        | „Stick getrennt – verbinde neu …“, Verbindung halten   |
| `status` `device_restored`                    | Stick wieder da, Einstellungen gesetzt | Hinweis ausblenden                                     |
| `error` `BUSY` (fatal)                        | anderer Client nutzt den Stick         | „Wird bereits von … verwendet“ (`message`/`client`)    |
| `error` `KICKED` (fatal)                      | Admin hat getrennt                     | „Vom Server getrennt“, nicht automatisch neu verbinden |
| `error` `INVALID_COMMAND` (nicht fatal)       | Befehl abgelehnt (z. B. Samplerate)    | Hinweis, Verbindung bleibt                             |

Bei `device_not_ready` und `device_lost` **nicht** selbst trennen und neu verbinden: Der Server meldet sich, sobald
der Stick wieder da ist. Nach einem fatalen Fehler schließt der Server die Verbindung. Bei `BUSY` und `KICKED`
sollte die App nicht in einer Schleife neu verbinden.

## Server-Regeln, die der Client kennen sollte

- Ein Stick = ein Port = ein Client. Der erste gewinnt, auch wenn er von einem anderen Standort kommt.
- v1-Clients werden bei belegtem Stick sofort getrennt (v1 kann keine Meldung transportieren).

## Zum Testen

rtltcp.server lokal starten (`dotnet run --project src/RtlTcp.Server -- --data ./data`) und den Client mit dem
Port des Sticks verbinden.
