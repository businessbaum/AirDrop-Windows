# Befund P1-A3: AirDrop existiert auf dem Infrastruktur-Netz überhaupt nicht

**Erhoben:** 2026-09-17, 22:21–22:23, auf dem MacBook Air (Darwin 24.6.0, `Mac16,12`).
**Ausgeführt von:** einer Claude-Session auf dem Mac, auf Anforderung dieser Session.
**Art:** ausschließlich lesende Befehle. Keine Einstellung geändert, nichts installiert.
**Nicht ausgeführt:** Block 2 (`tcpdump`) — verlangt ein sudo-Passwort, das dort nicht
vorlag. Block 3 — verlangt eine Person am iPhone.

## Ergebnis in einem Satz

Unser Responder ist nachweislich korrekt; AirDrop benutzt das Infrastruktur-Netz aber
weder zur Ankündigung noch zum Transfer, und im Ruhezustand existiert auf der Gegenseite
nicht einmal ein lauschender TCP-Socket.

## 1. Unser Record ist vollständig in Ordnung

```
$ dns-sd -L bf49b82d9499 _airdrop._tcp local
bf49b82d9499._airdrop._tcp.local. can be reached at windows-pc-airdrop.local.:8771 (interface 11)
 flags=1019
```

Apples eigener Resolver löst uns **vollständig** auf: Instanzname, SRV mit Port 8771,
Hostname, A-Record und TXT. Interface 11 ist `en0`. Damit ist die gesamte Fehlerklasse
„unser mDNS ist kaputt" ausgeschlossen — nicht durch Argument, sondern durch Apples
eigenes Werkzeug.

## 2. Es gibt kein einziges echtes AirDrop-Ziel auf `en0`

```
$ dns-sd -B _airdrop._tcp          # zweimal, 15 s und 25 s
Timestamp     A/R Flags if Domain  Service Type      Instance Name
22:21:34.695  Add     2 11 local.  _airdrop._tcp.    bf49b82d9499
```

Genau eine Zeile: wir. Kein iPhone, kein Mac, kein iPad.

Und das, obwohl dieser Mac empfangsbereit ist:

```
$ defaults read com.apple.sharingd DiscoverableMode   ->  Everyone
$ pgrep -l sharingd                                   ->  647 sharingd
```

**Der Mac steht auf „Für alle", der Dienst läuft — und kündigt sich trotzdem nicht an.**

## 3. Die Gegenprobe zeigt, dass Bonjour auf `en0` einwandfrei arbeitet

```
$ dns-sd -B _companion-link._tcp
22:22:49.518  Add  3   1 local.  _companion-link._tcp.  MacBook Air (Testgerät)
22:22:49.518  Add  3  11 local.  _companion-link._tcp.  MacBook Air (Testgerät)
22:22:49.518  Add  2  11 local.  _companion-link._tcp.  iPad (weiteres Gerät im Haushalt)
```

Echte Apple-Geräte, auf `en0`, mit vollständigem TXT. Der Bonjour-Weg über das WLAN
funktioniert also — nur eben nicht für `_airdrop._tcp`. Das schließt „mDNS kommt hier
generell nicht durch" als Erklärung aus.

## 4. Im Ruhezustand gibt es gar kein Ziel

```
$ lsof -nP -iTCP -sTCP:LISTEN | grep sharingd
(leer)

$ lsof -nP -i | grep sharingd
sharingd  647 user  8u  IPv6 ...  UDP *:60948
```

`sharingd` hält **keinen lauschenden TCP-Socket**. Der HTTPS-Listener für den Transfer
entsteht erst im Zuge der Erkennung. Selbst wenn ein Client den Mac über `en0` ansprechen
wollte, wäre dort nichts, was antwortet.

## 5. `awdl0` trägt ausschließlich Link-Local-IPv6

```
awdl0: flags=8863<UP,BROADCAST,SMART,RUNNING,...> mtu 1500
	ether 02:00:00:00:00:01
	inet6 fe80::1001%awdl0 prefixlen 64 scopeid 0x11
	status: active
```

Keine IPv4, keine routbare IPv6. Ein AirDrop-Transfer über `awdl0` läuft damit zwingend
über `fe80::…%awdl0`. Eine Adresse wie unsere `192.168.178.105:8771` kann in diesem
Modell gar nicht vorkommen.

Paketzähler zur Einordnung: `awdl0` 8.522 Pakete über die gesamte Laufzeit, `en0`
15,7 Millionen. Die Schnittstelle ist „active", aber im Leerlauf — was zu einem Funkweg
passt, der nur während Erkennung und Transfer hochgefahren wird.

## Was daraus folgt

Drei unabhängige Messungen zeigen in dieselbe Richtung:

| Messung | Aussage |
|---|---|
| Empfangsbereiter Mac kündigt kein `_airdrop._tcp` auf `en0` an | Es gibt keinen Infrastruktur-Ankündigungspfad |
| `sharingd` hat im Ruhezustand keinen TCP-Listener | Es gibt kein Infrastruktur-Ziel |
| `awdl0` hat nur Link-Local-IPv6 | Der Transfer kann nicht auf einer LAN-Adresse liegen |

**Weg A (AirDrop über das vorhandene LAN) ist damit beantwortet: er existiert nicht.**
Nicht, weil unsere Umsetzung mangelhaft wäre — sie ist von Apples eigenem Stack als
korrekt bestätigt —, sondern weil AirDrop diesen Transport nicht anbietet.

Das iPhone *sendet* zwar `_airdrop._tcp`-Anfragen über `en0` (Befund P1-A1, weiterhin
gültig). Das ist aber das normale Verhalten von `mDNSResponder`, der Browse-Anfragen über
alle Schnittstellen schickt. Antworten von `en0` führen zu keiner Verbindung.

## Was ausdrücklich noch nicht bewiesen ist

Dass der Transfer **zwingend** über `awdl0` läuft, ist durch diese Messungen nur stark
nahegelegt, nicht belegt. Alle Werte oben stammen aus dem Ruhezustand. Der harte Nachweis
wäre ein `tcpdump` auf `awdl0` und `en0` während eines echten Transfers (Block 2) oder
ein `dns-sd -B _airdrop._tcp`, das bei geöffnetem AirDrop-Blatt Instanzen auf
Interface 17 (`awdl0`) zeigt (Block 3). Block 3 ist die billigere Variante und braucht
kein sudo.

## Konsequenz

Der Entscheidungspunkt aus ARCHITEKTUR 5.2 ist jetzt vollständig erreicht:

- Der vorhandene Windows-Treiber bietet keinen Rohfunkzugang
  ([BEFUND-P0](BEFUND-P0-radiozugriff.md)).
- Windows definiert für diesen Zweck keine Schnittstelle (keine NAN-OIDs, nur
  Wi-Fi-Direct-Sende-OIDs).
- Der LAN-Ersatzweg existiert nicht.

Die fehlende Funktion ist damit konkret benannt: **Empfang und Senden von
802.11-Frames auf dem AWDL-Kanal, mit link-local IPv6 darüber.** Alles oberhalb davon
ist gebaut und getestet.

---

# Nachtrag: Block 3 — AWDL war aktiv, und `dns-sd` hat nichts davon gesehen

**Erhoben:** 2026-09-17, 22:28:04–22:29:34 (90 Sekunden), auf dem MacBook.
**Durchführung:** Claude-Session auf dem Mac; der Nutzer hat parallel am iPhone das
AirDrop-Blatt mehrfach geöffnet und geschlossen.

## Rohausgabe

```
$ dns-sd -B _airdrop._tcp          # 90 Sekunden
Timestamp     A/R  Flags if Domain  Service Type    Instance Name
22:28:04.129  Add      2  11 local. _airdrop._tcp.  bf49b82d9499
```

Das ist die **vollständige** Ausgabe. Eine Zeile, Interface 11 (`en0`), das sind wir.
Nichts auf Interface 17 (`awdl0`). Über die gesamten 90 Sekunden kein einziges
`Add`- oder `Rmv`-Ereignis — die Liste hat sich kein Mal verändert.

## Der Zählerstand, der das Negativergebnis belastbar macht

Ein Negativergebnis ist wertlos, wenn unklar bleibt, ob überhaupt etwas passiert ist.
Deshalb die Paketzähler von `awdl0`:

```
22:23   Ipkts   8.522   Ibytes   9.412.352   Opkts 6.426   Obytes 1.616.211
22:31   Ipkts  12.997   Ibytes  15.885.469   Opkts 6.720   Obytes 1.699.010
Delta          +4.475           +6.473.117                 +82.799
```

In diesem Fenster hat `awdl0` rund **4.500 Pakete und 6,5 MB** empfangen. Zum Vergleich:
Über die **gesamte vorherige Laufzeit** des Rechners waren es insgesamt 8.522 Pakete.
Die Schnittstelle ist also von praktischem Leerlauf auf erheblichen Verkehr gesprungen.

**AWDL war unbestreitbar aktiv und hat Erkennungsverkehr getragen — und `dns-sd` hat
davon nichts gesehen.**

## Was das über die reine Interface-Frage hinaus bedeutet

Die naheliegende Deutung wäre gewesen: „AirDrop läuft halt auf einem anderen Interface."
Die Messung sagt mehr. `dns-sd` spricht mit `mDNSResponder`, also mit derselben
Bonjour-Instanz, die auch die AWDL-Schnittstelle bedient. Trotzdem taucht dort kein
AWDL-Peer auf.

**`mDNSResponder` gibt AWDL-Peers über die öffentliche Browse-Schnittstelle nicht
heraus.** Die AirDrop-Erkennung ist dieser API nicht zugänglich.

Für einen späteren AWDL-Nachbau folgt daraus eine praktische Konsequenz: Das Vorbild,
das wir suchen — der echte `_airdrop._tcp`-TXT-Record eines Apple-Geräts — ist über
`dns-sd` **prinzipiell nicht zu bekommen**. Er ist nur über einen Mitschnitt auf `awdl0`
sichtbar (Block 2) oder aus einer externen Quelle.

## Einschränkungen, die der Messende selbst benannt hat

1. **Die beiden Browses liefen nacheinander, nicht gleichzeitig.** `_airdrop._tcp` von
   22:28:04 bis 22:29:34, `_companion-link._tcp` erst danach von 22:29:34 bis 22:31:04.
   Die Gegenprobe deckt also ein anderes Zeitfenster ab und zählt nur als schwaches
   Zusatzindiz, nicht als Beleg.
2. **Wann genau am iPhone getippt wurde, ist nicht unabhängig geprüft.** Genau deshalb
   ist der `awdl0`-Zählersprung wichtig: Er belegt unabhängig von der Bedienung, dass in
   diesem Zeitraum AWDL-Verkehr stattgefunden hat.

Diese Einschränkungen sind hier stehengelassen, nicht weggeglättet. Sie ändern das
Ergebnis nicht, aber sie gehören zu seiner Bewertung.

## Stand der Hypothese nach Block 3

Die in P1-A2 formulierte Hypothese — AirDrop verwertet nur Peers, die über `awdl0`
erscheinen — ist damit so gut gestützt, wie es ohne Mitschnitt geht:

- Kein AirDrop-Peer ist über die öffentliche Bonjour-Schnittstelle sichtbar, auf keinem
  Interface, auch nicht während nachweislich aktiver AWDL-Erkennung.
- Ein empfangsbereiter Mac kündigt auf `en0` nichts an.
- `sharingd` hält im Ruhezustand keinen TCP-Listener.
- `awdl0` trägt nur Link-Local-IPv6.

**Weiterhin nicht bewiesen** bleibt allein, dass der Dateitransfer selbst über `awdl0`
läuft. Das zeigt nur Block 2. Für die Projektentscheidung ist das jedoch nachrangig: Der
Erkennungsweg ist geklärt, und ohne Erkennung gibt es keinen Transfer.
