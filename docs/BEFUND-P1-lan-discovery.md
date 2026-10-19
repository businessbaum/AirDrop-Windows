# Befund P1-A1: Das iPhone sucht AirDrop über das normale WLAN

**Erhoben:** 2026-09-17, 21:52–21:56.
**Werkzeug:** `airdrop-diag mdns-watch --all`, passiv.
**Rohdaten:** `evidence/runs/A1c-iphone-wlan/mdns-watch.txt`.
**Eingriffe:** Das WLAN dieses PCs wurde mit dem gespeicherten Profil „FRITZ!Box 7490"
verbunden. Sonst nichts. Der Mitschnitt hat nichts gesendet.

## Ergebnis

Ein unverändertes iPhone im normalen AirDrop-Ablauf sucht `_airdrop._tcp.local.` per
mDNS über die **Infrastruktur-WLAN-Verbindung**. Es bleibt nicht auf AWDL beschränkt.

## Die Beobachtung

Von `192.168.178.128` (vom Nutzer als sein iPhone bestätigt):

```
21:52:58.015  Q: _airdrop._tcp.local. PTR (QU)
21:52:59.042  Q: _airdrop._tcp.local. PTR
21:52:59.118  Q: 0eeb30de04cd._airdrop._tcp.local. SRV (QU)
              Q: 0eeb30de04cd._airdrop._tcp.local. TXT (QU)
21:53:02.024  Q: _airdrop._tcp.local. PTR
21:53:11.050  Q: _airdrop._tcp.local. PTR
```

Zwei Details machen daraus mehr als ein beiläufiges Browse:

1. **Der zeitliche Abstand** 58,0 s → 59,0 s → 02,0 s → 11,0 s ist das
   Verdopplungsmuster, das RFC 6762 für eine neu gestartete Suche vorschreibt. Die Suche
   hat also in diesem Moment begonnen — passend zum Öffnen des AirDrop-Blatts.
2. **Die Auflösung.** 76 ms nach der PTR-Frage fragt das iPhone gezielt SRV und TXT einer
   konkreten Instanz. Es hat also eine PTR-Antwort erhalten — **über irgendeine
   Schnittstelle**. Siehe dazu die Korrektur unten.

Die Antwort auf die PTR-Frage ist im Mitschnitt nicht enthalten, weil die Frage das
QU-Bit setzt und der Anbieter deshalb unicast an das iPhone antwortet. Das ist erwartetes
Verhalten und kein Widerspruch.

## Gerätezuordnung — geprüft, nicht angenommen

| IP | Gerät | Beleg |
|---|---|---|
| `192.168.178.67` | MacBook Air | antwortet mit `macbook-2.local`, `_device-info` TXT `model=Mac16,12; osxvers=24`, und mit den Records von „MacBook Air (Testgerät)" |
| `192.168.178.128` | **iPhone** | vom Nutzer bestätigt; antwortet selbst nie, sucht nur — darunter `_applicationServicePairing._tcp` und `_appSvcPrePair._tcp` |

Bemerkenswert: Der **Mac** hat in diesem Fenster **nicht** nach `_airdrop._tcp` gesucht,
das iPhone schon. Die verbreitete Erwartung — Mac über Infrastruktur erreichbar, iPhone
nicht — trifft hier also genau andersherum zu.

## Was dieser Befund *nicht* zeigt

- **Nicht**, dass wir im AirDrop-Menü erscheinen. Gesucht zu werden und angezeigt zu
  werden sind zwei verschiedene Dinge. Das iPhone kann einen über Infrastruktur
  gefundenen Anbieter prüfen und danach verwerfen.
- **Nicht**, dass ein Transfer zustande kommt. Discovery ist der erste von vier Schritten;
  `/Discover`, `/Ask` und `/Upload` sind noch völlig offen.
- **Nicht**, welche Instanz `0eeb30de04cd` gehört. Der Anbieter hat unicast geantwortet
  und ist im Mitschnitt nicht identifizierbar.

## KORREKTUR vom 22:13 — die Auflösung beweist weniger als zunächst angenommen

Ursprünglich stand hier, das iPhone „verarbeitet auch Antworten von diesem Link". Das
ist **nicht belegt** und wurde durch eine spätere Messung unwahrscheinlich gemacht.

Am 2026-09-17 um 22:12:58 lief eine aktive Abfrage nach `_airdrop._tcp.local.` über
30 Sekunden, während am Mac das AirDrop-Fenster geöffnet war
(`evidence/runs/A2-capture-apple-advert/`). Ergebnis: **kein einziges Apple-Gerät hat
`_airdrop._tcp` über das Infrastruktur-WLAN angekündigt.** Sichtbar waren ausschließlich
die eigenen Antworten dieses Rechners.

Wenn niemand über Infrastruktur ankündigt, kann das iPhone die Instanz `0eeb30de04cd`
nur über AWDL gelernt haben. Die anschließende SRV/TXT-Frage ging dann — wie bei mDNS
üblich — über *alle* Schnittstellen hinaus, auch über das WLAN. Genau das haben wir
gesehen. Die Beobachtung ist also mit „AWDL gelernt, überall aufgelöst" vollständig
erklärbar und beweist keine Infrastruktur-Akzeptanz.

**Was weiterhin unverändert belegt ist:** Das iPhone *sendet* `_airdrop._tcp`-Anfragen
über das Infrastruktur-WLAN. Das ist direkt beobachtet und bleibt PASS.

**Was nicht belegt ist:** dass es Antworten von dort auch verwertet. Die aktuelle
Evidenz spricht dagegen — siehe [BEFUND-P1-lan-responder.md](BEFUND-P1-lan-responder.md).

## Konsequenz für den Bauplan

RECHERCHE Q10 („iPhone/iPad nutzten AirDrop nicht über normale LAN-Ankündigungen",
beobachtet 2021) gilt für dieses iOS **nicht mehr**. Weg A aus
[ENTSCHEIDUNG-transportwege.md](ENTSCHEIDUNG-transportwege.md) ist damit nicht nur
zulässig, sondern der klar vorrangige Weg — vor einem eigenen Funktreiber, dessen Aufwand
in Monaten gemessen wird.

**Nächster Schritt (A2):** einen echten `_airdrop._tcp`-Responder mit HTTPS-Listener
betreiben und beobachten, ob „Windows-PC" im AirDrop-Menü des iPhones erscheint.
