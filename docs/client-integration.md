# rtltcp_v2 in einen Client einbauen

Anleitung für Client-Entwickler (z. B. home.radio). Die verbindliche Spezifikation ist
[protocol-v2.md](protocol-v2.md). Hier steht, *wie* man sie sinnvoll in eine App einbaut.

## Einstellungen pro Server (manuell umschaltbar)

| Einstellung        | Werte                         | Standard | Wirkung                                                              |
|--------------------|-------------------------------|----------|----------------------------------------------------------------------|
| Protokoll          | `rtl_tcp (v1)` / `rtltcp_v2`  | v1       | v2 sendet HELLO und versteht Status- und Fehlermeldungen             |
| Anmeldung          | aus / ein                     | aus      | nur bei v2 wählbar                                                   |
| Benutzer, Passwort | Text                          | –        | nur bei eingeschalteter Anmeldung; Passwort sicher speichern         |

Es gibt bewusst keine automatische Erkennung. Der Benutzer schaltet v2 und die Anmeldung selbst ein oder aus.
Mit **v1** verhält sich der Client exakt wie bisher.

## Ablauf mit v2

```text
connect()
send  F2 52 54 4C 32                         HELLO (5 Bytes, sofort, vor dem ersten Lesen)
read  4 Bytes
  "RTL0" → alter Server: restliche 8 Header-Bytes lesen, weiter als v1 (Hinweis anzeigen: "Server kann kein v2")
  "RTL2" → restliche 8 Bytes lesen (u16 version, u16 flags, u32 reserved), ab jetzt nur Frames
  EOF    → Server hat abgelehnt (z. B. v1-Regel bei aktiver Auth)
read Frame → server_info
  auth == "required":
      Anmeldung in der App aus  → Meldung "Server verlangt Anmeldung", trennen
      Anmeldung ein             → SCRAM (siehe unten)
read Frames in Schleife:
  type 0x01 IQ      → Payload exakt wie bisher die rohen IQ-Bytes an die Verarbeitung geben
  type 0x02 JSON    → Status/Fehler anzeigen (Tabelle unten)
  unbekannter Typ   → Länge überspringen
Befehle senden: Frame type 0x03, Payload = der bekannte 5-Byte-rtl_tcp-Befehl
```

Frame-Aufbau: `u8 type | u32 length (big-endian) | payload`.

**Wichtig:** Im v2-Modus kommen IQ-Daten nur in Frames. Der bestehende IQ-Lesecode muss die 5 Byte Frame-Header
herausnehmen, sonst entstehen Störungen im Signal. Am saubersten ist eine eigene Lese-Schicht, die Frames zerlegt
und die IQ-Payloads in denselben Puffer bzw. Callback gibt wie bisher.

Die Gain-Liste kommt bei v2 nicht im Header, sondern in `status.tuner.gains` (Zehntel-dB), der Tuner-Typ in
`status.tuner.type` (gleiche Nummern wie im v1-Header).

## Meldungen → Anzeige

| JSON                                          | Bedeutung                              | Empfehlung in der App                                  |
|-----------------------------------------------|----------------------------------------|--------------------------------------------------------|
| `status` `ready`                              | Stick bereit, IQ läuft                 | normal, ggf. Gain-Liste übernehmen                     |
| `status` `starting`                           | Stick wird geöffnet (nach Idle)        | „Empfänger startet …“                                  |
| `status` `device_not_ready`                   | kein Stick am Server                   | „USB-Stick am Server nicht bereit“, Verbindung halten  |
| `status` `device_lost`                        | Stick weg, Server verbindet neu        | „USB-Stick getrennt – verbinde neu …“, Verbindung halten |
| `status` `device_restored`                    | Stick wieder da, Einstellungen gesetzt | Hinweis ausblenden                                     |
| `error` `BUSY` (fatal)                        | anderer Client nutzt den Stick         | „Server wird bereits von 192.168.1.20 verwendet“ (`message`/`client`) |
| `error` `AUTH_REQUIRED` (fatal)               | Server verlangt Anmeldung              | „Server verlangt Anmeldung“, auf Einstellungen verweisen |
| `error` `AUTH_FAILED` (fatal)                 | falscher Benutzer/Passwort/keine Freigabe | „Anmeldung fehlgeschlagen“                          |
| `error` `KICKED` (fatal)                      | Admin hat getrennt                     | „Vom Server getrennt“, nicht automatisch neu verbinden |
| `error` `INVALID_COMMAND` (nicht fatal)       | Befehl abgelehnt (z. B. Samplerate)    | Hinweis, Verbindung bleibt                             |

Bei `device_not_ready` und `device_lost` **nicht** selbst trennen und neu verbinden: Der Server meldet sich, sobald
der Stick wieder da ist. Nach einem fatalen Fehler schließt der Server die Verbindung. Bei `BUSY`, `AUTH_*` und
`KICKED` sollte die App nicht in einer Schleife neu verbinden.

## Anmeldung (SCRAM-SHA-256)

```text
C → S  {"type":"auth_start","user":"jay","cnonce":"<base64, 18 Zufallsbytes>"}
S → C  {"type":"auth_challenge","nonce":"…","salt":"…","iterations":100000}
C      prüfen: base64-dekodiertes nonce beginnt mit den cnonce-Bytes
       SaltedPassword  = PBKDF2-HMAC-SHA256(password, base64dec(salt), iterations, 32 Bytes)
       ClientKey       = HMAC-SHA256(SaltedPassword, "Client Key")
       StoredKey       = SHA256(ClientKey)
       AuthMessage     = user + "," + cnonce + "," + nonce + "," + salt + "," + iterations   (Strings wie empfangen)
       ClientProof     = ClientKey XOR HMAC-SHA256(StoredKey, AuthMessage)
C → S  {"type":"auth_proof","proof":"<base64(ClientProof)>"}
S → C  {"type":"auth_ok","signature":"…"}   → optional prüfen: HMAC-SHA256(HMAC-SHA256(SaltedPassword,"Server Key"), AuthMessage)
   oder {"type":"error","code":"AUTH_FAILED",…}
```

Testvektor zum Prüfen der Krypto (RFC 7677, gleiche Mathematik, dort anderer AuthMessage-String):
Passwort `pencil`, Salt `W22ZaJ0SNY7soEsUEjb6gQ==`, 4096 Iterationen,
AuthMessage `n=user,r=rOprNGfwEbeRWgbNEkqO,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096,c=biws,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0`
→ Proof `dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=`, Server-Signatur `6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=`.

PBKDF2 mit 100 000 Iterationen dauert auf schwachen Geräten einige 100 ms. Deshalb gehört es nicht in den UI-Thread.

## Server-Regeln, die der Client kennen sollte

- Auth am Server **aus**: v1 und v2 ohne Anmeldung.
- Auth am Server **ein**:
  - Clients aus vertrauenswürdigen Netzen (Standard: private Adressbereiche) brauchen keine Anmeldung. Dort
    funktionieren auch v1-Clients.
  - Andere Clients: v1 wird sofort getrennt, ohne Meldung, weil v1 keine Meldungen transportieren kann. v2 muss
    sich anmelden.
- Ein Stick = ein Port = ein Client. Der erste gewinnt.

## Zum Testen

rtltcp.server lokal starten (`dotnet run --project src/RtlTcp.Server -- --data ./data`), Benutzer anlegen
(`… -- --data ./data user add jay geheim`). In `data/config.json` dann `auth.enabled` auf `true` setzen und
`trustedNetworks` leeren, damit auch localhost die Anmeldung braucht. Danach den Server neu starten.
