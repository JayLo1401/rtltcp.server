# rtltcp.server in Home Assistant

Es gibt zwei Wege. Welcher passt, hängt davon ab, wie Home Assistant installiert ist
(**Einstellungen → System → Info → Installationsart**).

| Installationsart                      | Weg                                     |
|---------------------------------------|-----------------------------------------|
| Home Assistant OS (übliches Pi-Image) | **Add-on** (unten)                      |
| Supervised                            | **Add-on** (unten)                      |
| Container (Docker) oder Core          | **Direkt installieren** wie unter Linux |

## Als Add-on (Home Assistant OS / Supervised)

1. In Home Assistant **Einstellungen → Add-ons → Add-on Store** öffnen.
2. Oben rechts **⋮ → Repositories** und diese Adresse hinzufügen:

   ```text
   https://github.com/JayLo1401/rtltcp.server
   ```

3. Den Store neu laden (⋮ → **Nach Updates suchen**). Unten erscheint **rtltcp.server (jaycloud)**.
4. **rtltcp.server** öffnen → **Installieren**. Home Assistant baut das Add-on dabei auf dem Gerät. Es lädt
   das fertige Programm von `repository.jaycloud.at`, das dauert ein bis zwei Minuten.
5. Optional **In Seitenleiste anzeigen** einschalten, dann **Starten**.
6. **Weboberfläche öffnen**. Die Anmeldung übernimmt Home Assistant. Unter **Status** sollte der Stick
   erscheinen, der erste auf Port **1234**.

In SDR++, SDR#, home.radio usw. als Server die Adresse des Home-Assistant-Rechners (z. B. `192.168.1.10`) und
Port `1234` eintragen.

**Updates:** Nach jedem neuen Release zeigt Home Assistant beim Add-on **Aktualisieren** an.

**Hinweise**

- Ein Stick kann nur von einem Programm benutzt werden. Nutzt schon ein anderes Add-on den Stick
  (rtl_433, RTLSDR-Airband …), braucht rtltcp.server einen eigenen.
- Die Weboberfläche ist zusätzlich unter `http://<home-assistant>:8099/` erreichbar. Dort gilt der eigene
  Login (`admin` / `admin`, danach eigenes Passwort). Den Port ändert man im Reiter **Konfiguration** des Add-ons
  (`web_port`). Leer bedeutet: nur über Home Assistant. Der Zugang über die Seitenleiste nutzt einen Port, den
  Home Assistant selbst frei wählt, und kollidiert daher nicht mit anderen Add-ons.
- `stream_port` (Standard 1234) legt den Port des ersten Sticks beim allerersten Start fest. Später werden die
  Ports in der Weboberfläche unter **Geräte** geändert.
- Die Einstellungen sind Teil der Home-Assistant-Backups.

## Direkt installieren (Container / Core auf Raspberry Pi OS, Debian, Ubuntu)

Auf dem Rechner, an dem der Stick hängt:

```bash
curl -fsSL https://repository.jaycloud.at/repository/rtltcp.server/linux/release/latest/install.sh | bash
```

Das Skript fragt nach den Ports (Weboberfläche 8080, erster Stick 1234), prüft, ob sie frei sind, und richtet
einen systemd-Dienst ein. Danach `http://<rechner>:8080/` öffnen, Anmeldung `admin` / `admin`.

## Fehlersuche

| Problem                                   | Lösung                                                                                                                                       |
|-------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------|
| Status „Stick nicht bereit“               | Stick neu anstecken. Prüfen, ob ein anderes Add-on ihn belegt.                                                                               |
| Port belegt (Hinweis im Add-on-Protokoll) | Web-Port: im Reiter **Konfiguration** einen anderen `web_port` wählen oder leeren. Stick-Port: in der Weboberfläche unter **Geräte** ändern. |
| Client bekommt „Server already in use“    | Ein anderer Client ist verbunden. Unter **Status** trennen.                                                                                  |
| Kein Ton / Rauschen bei starkem Sender    | Unter **Geräte** einen festen Gain einstellen (z. B. 14,4 dB).                                                                               |
