<h1 align="center">rtltcp.server</h1>

<p align="center">
  <b>rtl_tcp-kompatibler Server für RTL-SDR-Sticks, mit Auto-Reconnect, Idle-Abschaltung und Protokoll rtltcp_v2</b><br>
  <a href="https://jaycloud.at">Powered by Jay</a>
</p>

> **Status: in Entwicklung (0.1.0).** Der Stream-Server läuft und ist mit einem NooElec NESDR SMArt v5 getestet.
> Web-GUI, Installer und fertige Downloads folgen.

Ein Server, der einen oder mehrere RTL-SDR-Sticks im Netzwerk bereitstellt. Er verhält sich wie das bekannte
`rtl_tcp`, deshalb funktionieren SDR++, SDR#, GQRX, home.radio usw. ohne Änderung. Dazu kommt, was `rtl_tcp` fehlt:
Er schaltet den Stick ab, wenn niemand zuhört, und holt ihn zurück, wenn er kurz weg war. Außerdem sagt er dem Client,
was los ist.

## Funktionen

### Wie rtl_tcp, nur robuster

- **Drop-in-Ersatz:** gleicher Port (1234), gleiches Protokoll, gleiche Befehle.
- **Auto-Reconnect:** Stick abgesteckt oder kurz weg? Der Server findet ihn über die Seriennummer wieder, setzt
  Frequenz, Samplerate, Gain usw. neu und streamt weiter. Die Verbindung zum Client bleibt dabei bestehen.
- **Idle-Abschaltung:** Ohne Client wird der Stick nach 30 s (einstellbar, 0 = immer an) geschlossen. Tuner und
  Demodulator gehen in Standby. Der nächste Client weckt ihn in rund einer Sekunde.
- **Mehrere Sticks:** Jeder Stick bekommt einen eigenen Port (1234, 1235 …).
- **Klare Regeln:** Der erste Client gewinnt. Ein zweiter wird sofort getrennt, statt wie bei `rtl_tcp` ewig zu hängen.

### Protokoll rtltcp_v2

Eine abwärtskompatible Erweiterung. Neue Clients melden sich mit einem *Hello*, alte merken davon nichts.

- **Klare Meldungen:** *USB device not ready*, *Server already in use by 192.168.1.20*, *USB device lost, reconnecting*,
  *USB device restored* …
- **Optionale Anmeldung** mit Benutzern, die nur bestimmte Sticks nutzen dürfen. Das Verfahren ist SCRAM-SHA-256:
  Das Passwort geht nie über das Netz.
- **Internes Netz ohne Anmeldung:** Clients aus vertrauenswürdigen Netzen (Standard: 192.168.x.x, 10.x.x.x, 172.16–31.x.x,
  IPv6 ULA/link-local) brauchen keine Anmeldung, auch nicht mit alten v1-Clients. Zugriffe von außen müssen sich anmelden.
- **Beide Richtungen kompatibel:** v1-Clients am neuen Server und v2-Clients an einem alten `rtl_tcp` funktionieren.

Details: [docs/protocol-v2.md](docs/protocol-v2.md)

## Unterstützte Hardware

| Tuner              | Status                                              | Beispiele                                  |
|--------------------|-----------------------------------------------------|--------------------------------------------|
| R820T / R820T2     | ✅ getestet                                         | NooElec NESDR SMArt v5, RTL-SDR Blog V3    |
| R828D              | ⚠️ implementiert, ungetestet                        | RTL-SDR Blog V4                            |
| E4000, FC0012/13   | ❌ noch nicht (Stick streamt, Tuning fehlt)         | ältere DVB-T-Sticks                        |

## Voraussetzungen

- **Windows:** Für den Stick einmalig den WinUSB-Treiber mit [Zadig](https://zadig.akeo.ie/) installieren
  (*Options → List All Devices*, „Bulk-In, Interface (Interface 0)“ bzw. „RTL2832U“, Treiber *WinUSB*).
- **Linux / Raspberry Pi:** `libusb-1.0` installieren (`sudo apt install libusb-1.0-0`). Den DVB-Kerneltreiber
  sperren, damit er den Stick nicht belegt:
  `echo 'blacklist dvb_usb_rtl28xxu' | sudo tee /etc/modprobe.d/rtlsdr-blacklist.conf`.
  Dazu eine udev-Regel für den Zugriff ohne root (kommt mit dem Installer).

## Installation

Fertige Pakete für Windows, Linux und Raspberry Pi folgen über `repository.jaycloud.at`. Bis dahin aus dem Quellcode
(benötigt das [.NET 10 SDK](https://dotnet.microsoft.com/download)):

```bash
dotnet run --project src/RtlTcp.Server -- --data ./data
```

Beim ersten Start legt der Server `data/config.json` an und trägt jeden gefundenen Stick ein.

## Konfiguration

`config.json` im Datenverzeichnis. Standardpfad: Windows `%ProgramData%\rtltcp.server`, Linux `/var/lib/rtltcp-server`,
änderbar mit `--data <ordner>` oder der Umgebungsvariable `RTLTCP_DATA`.

```json
{
  "auth": { "enabled": false, "trustedNetworks": ["127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "::1/128", "fc00::/7", "fe80::/10"] },
  "devices": [
    {
      "serial": "77771111153705700", "name": "Dach-Antenne", "enabled": true,
      "bind": "0.0.0.0", "port": 1234,
      "idleTimeoutSeconds": 30, "v1WaitForDeviceSeconds": 10,
      "defaults": { "frequency": 100000000, "sampleRate": 2048000, "gain": null, "ppm": 0, "biasTee": false }
    }
  ]
}
```

| Feld                    | Bedeutung                                                                          |
|-------------------------|------------------------------------------------------------------------------------|
| `serial`                | Seriennummer des Sticks, leer = erster freier Stick                                |
| `idleTimeoutSeconds`    | Stick so viele Sekunden nach dem letzten Client schließen, `0` = immer offen       |
| `v1WaitForDeviceSeconds`| So lange wartet ein v1-Client, wenn der Stick gerade nicht da ist                  |
| `defaults`              | Werte bei jeder neuen Verbindung. `gain` in Zehntel-dB, `null` = automatisch       |
| `auth.enabled`          | Anmeldung für Clients außerhalb der `trustedNetworks` verlangen                    |

Tipp: Bei einem starken Sender in der Nähe übersteuert die automatische Verstärkung. Dann einen festen `gain` setzen,
z. B. `144` für 14,4 dB.

Stream-Benutzer für die v2-Anmeldung, bis die Web-GUI kommt:

```bash
rtltcp-server user add <name> <passwort>
```

## So funktioniert es

- **Eigener Treiber:** Der RTL2832U und die R82xx-Tuner werden direkt über libusb angesprochen. Es gibt keine
  Abhängigkeit von librtlsdr und keinen GPL-Code.
- **Pro Stick ein Zustandsautomat:** Leerlauf → Starten → Bereit, mit „Nicht bereit“ und „Verloren“ samt Wiederholung
  jede Sekunde. Alle Änderungen laufen nacheinander auf einem eigenen Ablauf, es gibt keine Wettläufe zwischen USB,
  Clients und Timern.
- **Langsame Clients bremsen den Stick nicht:** Kommt ein Client nicht mit, werden alte Daten verworfen, statt den
  USB-Empfang zu blockieren.

## Bekannte Grenzen

- Noch keine Web-GUI. Konfiguration über `config.json`, Benutzer über die Kommandozeile.
- Ohne TLS ist die v2-Anmeldung sicher, die IQ-Daten selbst sind aber unverschlüsselt.
- Unter Windows lässt sich ein USB-Port nicht stromlos schalten. Die Idle-Abschaltung legt den Stick schlafen,
  er bleibt aber am Strom.
- Offset-Tuning, IF-Gain und Xtal-Befehle (nur für E4000 relevant) werden ignoriert.

## Entwicklung

```bash
dotnet build
dotnet test                                                     # Protokoll, Auth, Server mit simuliertem Stick
dotnet run --project tools/RtlTcp.Probe -- rate 2400000 5       # Durchsatz am echten Stick
dotnet run --project tools/RtlTcp.Probe -- fm 92600000 8 144    # 8 s UKW aufnehmen → fm.wav
```

### Projektstruktur

```text
src/RtlTcp.Core/          Protokoll v1/v2, Auth, RTL2832U- und Tuner-Treiber, DeviceManager, StreamServer
src/RtlTcp.Usb.LibUsb/    libusb-Anbindung (P/Invoke), libusb-1.0.dll für Windows
src/RtlTcp.Server/        Programm rtltcp-server (Dienst, Konfiguration, Benutzer)
tools/RtlTcp.Probe/       Hardwaretests
tests/RtlTcp.Core.Tests/  Unit- und Integrationstests
docs/                     Protokollspezifikation, Architektur
```

## Versionen

Versionsnummern nach [Semantic Versioning](https://semver.org/lang/de/). Die Basisversion steht in
[Directory.Build.props](Directory.Build.props), Änderungen im [CHANGELOG](CHANGELOG.md).
`rtltcp-server --version` zeigt die laufende Version.

## Lizenz

[PolyForm Noncommercial 1.0.0](LICENSE.md): frei für private und nicht-kommerzielle Nutzung, kommerzielle Nutzung
nicht erlaubt. Mitgelieferte Fremdkomponenten (libusb, LGPL-2.1) stehen unter ihren eigenen Lizenzen, siehe
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
