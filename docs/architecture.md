# Architektur

## Grundsätze

- **C# / .NET 10 (LTS, Support bis Nov. 2028)**, ein einziger Prozess für alle Sticks und die Web-GUI
  (Generic Host + ASP.NET Core/Kestrel).
- **Eigener Code, keine GPL-Abhängigkeiten.** librtlsdr, pyrtlsdr, der Linux-Kerneltreiber und LibUsbDotNet
  (LGPL-3) werden nicht genutzt. Der RTL2832U-/Tuner-Treiber wird in C# neu geschrieben, auf Basis von
  Datenblättern und Registerbeschreibungen. **Aus GPL-Projekten wird kein Code übernommen.**
- USB-Zugriff über eine **dünne eigene P/Invoke-Schicht auf libusb-1.0** (LGPL-2.1, dynamisch geladen).
  Unter Windows wird `libusb-1.0.dll` mit ausgeliefert. Unter Android läuft derselbe Treiber-Code über die
  USB-Host-API von Android.
- Hot Path ohne Allokationen: Bulk-Transfers landen in festen Puffern (`ArrayPool`, `Memory<byte>`) und
  gehen über `System.IO.Pipelines` in die Sockets.

## Projektstruktur

```text
src/
  RtlTcp.Core/              plattformneutral, keine ASP.NET-Abhängigkeit
    Usb/IUsbTransport.cs      Control-/Bulk-Transfers, Geräteliste, Hotplug
    Rtl2832/Demod.cs          Init, Samplerate, Xtal/ppm, Direct Sampling, AGC, Bias-Tee (GPIO), EEPROM
    Tuners/R82xx.cs           R820T / R820T2 / R828D (RTL-SDR Blog V3/V4)   ← Phase 1
    Tuners/E4000.cs, Fc001x.cs                                              ← später
    Device/DeviceManager.cs   Zustandsautomat je Stick, Idle-Abschaltung, Reconnect
    Protocol/V1.cs            RTL0-Header, 5-Byte-Kommandos
    Protocol/V2.cs            HELLO, Frames, JSON-Nachrichten
    Remote/                   Teilen über das Internet: QUIC, Zertifikat, Steuerkanal zu rtl.over.internet
    Server/StreamServer.cs    TCP-Listener je Gerät, Sitzungen, Eigentümer-Regel
  RtlTcp.Usb.LibUsb/        IUsbTransport über libusb (Windows, Linux, macOS)
  RtlTcp.Server/            Host: Dienst (Windows-Service/systemd), Kestrel, Web-GUI (wwwroot), REST-API, Updater
  RtlTcp.Android/           später: .NET-for-Android-App, IUsbTransport über die Android-USB-Host-API
tests/
  RtlTcp.Core.Tests/        Protokoll, Zustandsautomat, Teilen über QUIC (mit Fake-Transport, ohne Hardware)
```

## Gerätezustände (je Stick)

```text
            Client verbindet            Gerät geöffnet
  IDLE ───────────────────────► STARTING ────────────► STREAMING
   ▲                               │                     │   │
   │ idle_timeout ohne Client      │ nicht gefunden      │   │ USB-Fehler
   │ → Gerät schließen             ▼                     │   ▼
   └──────────────────────── NOT_READY ◄──── Retry ── LOST (alle 1 s per Seriennummer suchen,
                                                             dann letzte Einstellungen neu setzen)
```

- **Idle-Abschaltung** (`idleTimeoutSeconds`, 0 = nie): Nach dem letzten Client wird der Stick geschlossen.
  Tuner und Demodulator gehen in Standby. Optional wird ein Befehl ausgeführt, z. B. `uhubctl` für echtes
  Stromlosschalten des Ports (Linux, Hub mit Port-Power-Switching).
- **Reconnect:** Die Geräteidentität ist die **Seriennummer**, nicht der USB-Index. Die letzten
  Client-Einstellungen (Frequenz, Rate, Gain-Modus/Gain, ppm, AGC, Direct Sampling, Bias-Tee) werden pro
  Gerät gespeichert und nach dem Reconnect wieder gesetzt.
- **Mehrere Sticks:** Jeder Eintrag in `devices` hat Seriennummer und eigenen Port (1234, 1235 …).
  Neue Sticks erscheinen in der Web-GUI zum Übernehmen. Gleiche Seriennummern (oft `00000001`) lassen
  sich über die GUI umschreiben (EEPROM).

## Konfiguration (Entwurf, `config.json`)

```json
{
  "web": { "bind": "0.0.0.0", "port": 8080 },
  "devices": [
    { "serial": "00000001", "name": "Dach-Antenne", "bind": "0.0.0.0", "port": 1234,
      "idleTimeoutSeconds": 30, "idleCommand": null, "v1WaitForDeviceSeconds": 10,
      "defaults": { "frequency": 100000000, "sampleRate": 2048000, "gain": null, "ppm": 0, "biasTee": false } }
  ]
}
```

Web-Admins liegen getrennt in `users.json` (Passwort-Hash PBKDF2). Stream-Clients melden sich nicht an: Sie verbinden
sich lokal, den Weg über das Internet übernehmen die Server untereinander (siehe [remote-sharing.md](remote-sharing.md)).

## Web-GUI

- **Ersteinrichtung:** `admin` / `admin`, das Passwort muss beim ersten Login geändert werden. Das
  Standardpasswort gilt nur aus privaten Netzen.
- Die Web-GUI braucht **immer** einen Login (Cookie-Auth). Stream-Clients melden sich nicht an.
- Seiten: Status (je Stick: Zustand, Client, Datenrate, Trennen), Geräte (USB-Sticks und entfernte Sticks),
  Protokoll, Einstellungen (Teilen über rtl.over.internet, Update, Log, Admin-Passwort).
- Statisches HTML/JS/CSS in `wwwroot`, eingebettet ins Binary. Design und Farben wie jaycloud.at.

## Plattformen und Auslieferung

`dotnet publish` erzeugt eine **self-contained Single-File**-Datei (kein installiertes .NET nötig):

| Plattform                    | RID           | Dienst          | Treiber-Hinweis                              |
|------------------------------|---------------|-----------------|----------------------------------------------|
| Windows x64 / arm64          | `win-x64`     | Windows-Dienst  | WinUSB per Zadig                             |
| Linux x64                    | `linux-x64`   | systemd         | udev-Regel, `dvb_usb_rtl28xxu` blacklisten   |
| Raspberry Pi 3/4/5, Zero 2 W | `linux-arm64` / `linux-arm` | systemd | wie Linux                       |
| Android                      | später        | App             | USB-Host-API, gleicher Core                  |

Nicht unterstützt: Raspberry Pi 1 und Zero (1. Generation), weil .NET kein ARMv6 unterstützt.

Der Release läuft wie bei home.control: Jeder Push auf `main` bekommt eine Version aus den
Conventional Commits. Die Builds landen im Nexus-Raw-Repository
`https://repository.jaycloud.at/repository/rtltcp.server/<rid>/release/<version>/…` sowie unter
`…/latest/` mit `version.json`. Danach folgen Tag und GitHub-Release mit Links, aber ohne Dateien.
Für Linux kommt zusätzlich ein Installationsskript, das Binary, systemd-Unit und udev-Regel einrichtet.

## Risiken

1. **Tuner-Treiber** (R820T/R828D: PLL, Filterkalibrierung, Gain-Tabellen) sind der aufwendigste Teil.
   Vollständige offizielle Datenblätter gibt es nicht. Ohne einen Stick zum Testen geht hier nichts.
2. **USB-Treiber auf Windows:** Wie bei jedem rtl_tcp ist Zadig/WinUSB einmalig nötig. Später lässt sich
   das eventuell im Installer automatisieren.
