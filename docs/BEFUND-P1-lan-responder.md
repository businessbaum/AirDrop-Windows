# Befund P1-A2: Der Record ist korrekt — AirDrop benutzt ihn trotzdem nicht

**Erhoben:** 2026-09-17, 22:03–22:14.
**Werkzeuge:** `airdrop-receiver` (eigener Responder), `airdrop-diag mdns-watch`,
`dns-sd -B _airdrop._tcp` auf dem MacBook.
**Rohdaten:** `evidence/runs/20260917-22*-receiver/`, `evidence/runs/A2-capture-apple-advert/`.
**Eingriffe:** eine eingehende Firewall-Regel (TCP 8771, nur Profil „Privat", an die EXE
gebunden), nach ausdrücklicher Zustimmung. WLAN mit dem gespeicherten Profil verbunden.

## Ergebnis in einem Satz

Unsere AirDrop-Ankündigung über das normale WLAN ist nachweislich korrekt und wird von
Apples eigenem mDNS-Stack akzeptiert — aber weder iPhone noch Mac bauen daraufhin eine
Verbindung auf, und der PC erscheint nicht im AirDrop-Menü.

## Die beiden Messungen, die das trennen

### 1. Apple akzeptiert unseren Record

Auf dem MacBook, vom Nutzer ausgeführt und berichtet:

```
$ dns-sd -B _airdrop._tcp
... bf49b82d9499 ...
```

`dns-sd` spricht direkt mit `mDNSResponder`, also mit Apples eigener Implementierung.
Dass unsere Instanz dort auftaucht, schließt eine ganze Fehlerklasse aus:

- Der DNS-Wire-Aufbau stimmt (Namenskompression, PTR/SRV/TXT/A, Cache-Flush-Bits).
- Die Quelladresse der Antwort passt zum angekündigten Record.
- Multicast erreicht den Mac über dieses WLAN.
- Die Firewall steht dem mDNS-Weg nicht im Weg.

### 2. AirDrop benutzt ihn trotzdem nicht

Über zwei Läufe hinweg, mit zwei verschiedenen `flags`-Werten:

| Zeit | Ereignis |
|---|---|
| 22:05:07 | iPhone fragt `_airdrop._tcp.local. PTR (QU)` — wir antworten |
| 22:05:49 | iPhone fragt erneut — wir antworten |
| 22:05:56 | Mac fragt — wir antworten |
| 22:11:45 | iPhone fragt, jetzt mit `flags=0x3fb` in unserer Antwort — wir antworten |
| 22:12:45 | Mac fragt erneut |
| — | **keine einzige TCP-Verbindung auf Port 8771** |

`Get-NetTCPConnection -LocalPort 8771` zeigt durchgehend nur `Listen`. Der Nutzer
berichtet, dass „Windows-PC" im AirDrop-Menü des iPhones nicht erscheint.

## Was dabei widerlegt wurde

| Hypothese | Prüfung | Ergebnis |
|---|---|---|
| Unsere Unicast-Antwort hat die falsche Quelladresse | Sockets pro Schnittstelle gebunden, Antwort über den Empfangssocket | behoben — ändert nichts |
| Wir kündigen zu viele Adressen an, der Sender wählt eine tote | nur noch eine IPv4-Adresse | behoben — ändert nichts |
| `flags=0x04` ist zu wenig, `0x80` (`SUPPORTS_DISCOVER_MAYBE`) fehlt | Neustart mit `flags=0x3fb` (macOS-Vorgabe) | **widerlegt** — keine Verbindung |
| Unser DNS-Record ist fehlerhaft | `dns-sd -B` auf dem Mac | **widerlegt** — Apple listet uns |

Die ersten beiden waren echte Fehler und sind behoben; sie waren nur nicht die Ursache.

## Der zusätzliche Befund, der das Bild erklärt

Am 22:12:58 lief eine aktive Abfrage nach `_airdrop._tcp.local.` über 30 Sekunden, bei
geöffnetem AirDrop-Fenster am Mac. **Kein Apple-Gerät hat `_airdrop._tcp` über das
Infrastruktur-WLAN angekündigt.** Sichtbar war nur dieser PC.

Apple-Geräte *suchen* also über Infrastruktur, *kündigen* dort aber nicht an. Das ist
genau das Verhalten, das man erwartet, wenn `mDNSResponder` seine Browse-Anfragen wie
üblich über alle Schnittstellen schickt, während der AirDrop-Dienst darüber nur Peers
verwertet, die über `awdl0` gefunden wurden.

Das erklärt zugleich die frühere Fehldeutung: Die SRV/TXT-Auflösung von `0eeb30de04cd`
über das WLAN war die *Auflösung* einer Instanz, die das iPhone über AWDL gelernt hatte —
kein Beleg für Infrastruktur-Akzeptanz. Korrigiert in
[BEFUND-P1-lan-discovery.md](BEFUND-P1-lan-discovery.md).

## Stand der Hypothese

**Wahrscheinlichste verbleibende Erklärung:** AirDrop filtert auf die AWDL-Schnittstelle.
Der Peer muss über `awdl0` erscheinen, sonst zeigt die Oberfläche ihn nicht an. Das ist
konsistent mit allen bisherigen Messungen, aber noch **nicht bewiesen** — bewiesen ist
bisher nur, dass es nicht an unserem Record liegt.

**Noch nicht ausgeschlossen:** dass AirDrop zusätzlich eine BLE-Erkennungsphase verlangt,
bevor es einen gefundenen Peer anzeigt. Beide Erklärungen führen zum selben nächsten
Schritt, weil beide nicht über mDNS zu lösen sind.

## Was daraus folgt — und was ausdrücklich nicht

Nicht gefolgert werden darf, dass die Anwendungsschicht falsch ist. `/Discover`, `/Ask`
und `/Upload` wurden von einem echten Apple-Sender **nie erreicht** und sind damit
weiterhin `NOT_RUN`, nicht `FAIL`.

Der nächste sinnvolle Schritt ist deshalb, genau diese Schicht einmal gegen einen echten
Apple-Sender zu prüfen — siehe
[ENTSCHEIDUNG-transportwege.md](ENTSCHEIDUNG-transportwege.md), Abschnitt zum
Diagnoseversuch mit `BrowseAllInterfaces`.
