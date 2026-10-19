# Befund P1, Abschluss: Die Antwort kommt an und wird verworfen

**Erhoben:** 2026-09-18, 17:19–17:21, auf dem MacBook Air, gegen den laufenden
Windows-Responder.
**Art:** lesende Befehle plus eine TCP-Portprüfung. Nichts verändert.

Dieser Befund schließt Weg A ab. Er ersetzt die vorschnelle Fassung in
[BEFUND-P1-mac-messung.md](BEFUND-P1-mac-messung.md), deren Schlussfolgerung „AirDrop
bietet diesen Transport nicht an" zu weit ging.

## Ergebnis in einem Satz

Unser Record ist live, vollständig auflösbar und unser Port vom Apple-Gerät aus
erreichbar — und „Windows-PC" erscheint trotzdem nicht im AirDrop-Blatt.

## Die Messungen

```
$ dns-sd -B _airdrop._tcp
17:19:36.955  Add  2  11  local.  _airdrop._tcp.  bf49b82d9499

$ dns-sd -L bf49b82d9499 _airdrop._tcp local
17:20:13.745  bf49b82d9499._airdrop._tcp.local. can be reached at
              deddb511-f435-40dc-9a83-07141694f08e.local.:8770 (interface 11)
 flags=10

$ dns-sd -G v4v6 deddb511-f435-40dc-9a83-07141694f08e.local
17:20:23.759  Add  40000002  11  deddb511-...local.  192.168.178.103  4500
```

Alle vier Angleichungen an den echten Apple-Record sind angekommen: Port 8770,
UUID-Hostname, TTL 4500, `flags=10`. Der alte Name `windows-pc-airdrop.local` löst nicht
mehr auf, es liegt also kein Altbestand im Zwischenspeicher.

Und der Port ist offen:

```
$ nc -z -v -w 3 192.168.178.103 8770
Connection to 192.168.178.103 port 8770 [tcp/dpap] succeeded!

$ nc -z -v -w 3 192.168.178.103 8771          # Gegenprobe
nc: connectx to 192.168.178.103 port 8771 (tcp) failed: Operation timed out
```

Die Gegenprobe ist wichtig: Der Test unterscheidet tatsächlich, der Erfolg auf 8770 ist
kein Artefakt. Damit ist auch die Firewall-Sorge erledigt — das Profil „Öffentlich" auf
der Ethernet-Seite blockiert 8770 nicht. (ICMP blockt es sehr wohl; `ping` auf .103
verliert 100 %. Für AirDrop belanglos.)

Während all das galt, hat der Nutzer das AirDrop-Blatt mehrfach geöffnet. „Windows-PC"
erschien nicht.

## Die Frage, die damit beantwortet ist

Zwei Möglichkeiten standen zur Trennung:

| | |
|---|---|
| Antwort kommt nicht an | **ausgeschlossen** — im Zwischenspeicher, auflösbar, Port offen |
| Antwort kommt an und wird verworfen | **das ist der Fall** |

Der Bruch liegt in der Bewertung durch die AirDrop-Oberfläche. Nicht im Netz, nicht in
der Namensauflösung, nicht in der Firewall und nicht in unserem Code.

## Ein Fehlschluss auf dem Weg dorthin, und wie er entstand

Ein 94-Sekunden-Mitschnitt zeigte kein einziges Paket von uns. Daraus wurde — von der
messenden Seite und von dieser Session gleichermaßen — geschlossen, der Responder sende
nichts. Das war falsch.

Bei einer TTL von 4500 s unterdrückt ein Peer, der unseren Record im Zwischenspeicher
hält, ihn als Known Answer (RFC 6762 7.1), solange mehr als die halbe TTL übrig ist. Ein
gesunder Responder sendet in einem beliebigen 94-Sekunden-Fenster also mit hoher
Wahrscheinlichkeit **gar nichts** — und ist trotzdem vollständig funktionsfähig. Die
Zeitstempel belegen es: Der Mitschnitt endete 17:17:38, unser Log zeigt die Antwort auf
den nächsten Browse um 17:19:38.

**Lehre für künftige Messungen:** Abwesenheit im Mitschnitt ist bei mDNS kein Beleg für
Abwesenheit des Dienstes. Der Zwischenspeicher ist zuerst abzufragen (`dns-sd -B`/`-L`),
bevor aus fehlenden Paketen etwas geschlossen wird.

Die daraufhin eingebaute Wiederholung der Ankündigung alle 30 s bleibt im Code, aber als
das, was sie ist: eine Robustheits- und Beobachtbarkeitsmaßnahme, keine Fehlerbehebung.
Sie weicht bewusst von Apples Verhalten ab und lässt sich abschalten.

## Warum die Oberfläche unseren Eintrag verwirft

Die belastbarste Erklärung, gestützt auf drei unabhängige Beobachtungen:

1. Am 17.09. zeigte ein 90-Sekunden-Browse mit geöffnetem Blatt **keine einzige**
   Apple-Instanz auf Interface 17 (`awdl0`) — obwohl die Records nachweislich auf dem
   AWDL-Draht standen und `awdl0` in diesem Fenster +4.475 Pakete zählte.
2. Apples Oberfläche zieht ihre Kandidatenliste also **nicht** aus dem allgemeinen
   Bonjour-Zwischenspeicher, den wir bedienen.
3. Das einzige bekannte fremde Projekt mit einem echten iPhone-Transfer
   ([WinDrop](https://github.com/UvejsGj/WinDrop)) erreichte die Sichtbarkeit über
   **OWL auf AWDL** — Commit vom 13.09.2026: „Record the first AWDL contact: iOS 26.6
   seen by OWL on the AX211". Der Rückfall auf HTTP über TCP geschah erst **nach** der
   AWDL-Erkennung, nicht statt ihrer.

Ein Eintrag, der nur im allgemeinen Zwischenspeicher steht, ist damit sichtbar für
`dns-sd` und unsichtbar für das Blatt. Genau das messen wir.

**Folge:** Weiteres Feilen am TXT-Inhalt, am Port oder am Hostnamen ändert daran nichts.
Die Angleichung an Apples Record war trotzdem richtig — sie hat die Fehlerklasse
„unser Record ist falsch" ausgeschlossen, und ohne sie stünde diese Frage noch offen.
