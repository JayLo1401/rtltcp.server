# rtltcp.server

Stellt RTL-SDR-Sticks, die an Home Assistant hängen, im Netzwerk bereit. Der Server ist voll kompatibel zu
`rtl_tcp`, SDR++, SDR#, GQRX und home.radio funktionieren also ohne Änderung. Dazu kommen Auto-Reconnect,
Idle-Abschaltung und das Protokoll rtltcp_v2 mit Statusmeldungen und optionaler Anmeldung.

## Einrichtung

1. Stick an den Home-Assistant-Rechner stecken (vor oder nach dem Start, beides geht).
2. Add-on starten. Beim ersten Start wird jeder gefundene Stick eingetragen: der erste auf **Port 1234**, weitere auf 1235, 1236 …
3. **Weboberfläche öffnen** (oder „rtltcp.server“ in der Seitenleiste). Die Anmeldung übernimmt Home Assistant.

Im Client (z. B. SDR++ → Quelle „RTL-TCP“) die Adresse des Home-Assistant-Rechners und den Port eintragen.

## Weboberfläche

- **Status:** Zustand jedes Sticks, verbundener Client, Frequenz, Datenrate. Clients lassen sich trennen.
- **Geräte:** Ports, Stick-Zuordnung (Seriennummer), Abschaltzeit, Standardwerte (Frequenz, Samplerate, Gain, Bias-Tee).
- **Benutzer / Einstellungen:** Stream-Benutzer und Anmeldung für Clients von außen (rtltcp_v2), vertrauenswürdige Netze.
- **Protokoll:** Logs der letzten 14 Tage (einstellbar).

Die Weboberfläche ist zusätzlich direkt unter `http://<home-assistant>:8099/` erreichbar. Dort gilt der eigene
Login: beim ersten Mal `admin` / `admin`, danach muss ein eigenes Passwort gesetzt werden.

## Hinweise

- Ein Stick kann nur von **einem** Programm benutzt werden. Läuft bereits ein anderes Add-on mit demselben Stick
  (z. B. rtl_433), braucht rtltcp.server einen eigenen Stick.
- Die Ports sind direkt am Home-Assistant-Rechner offen (Host-Netzwerk), so wie bei einem normalen `rtl_tcp`.
- Bei starkem Sender in der Nähe übersteuert die automatische Verstärkung. Dann unter **Geräte** einen festen Gain setzen.
- Alle Einstellungen liegen im Add-on-Speicher und sind in Home-Assistant-Backups enthalten.
