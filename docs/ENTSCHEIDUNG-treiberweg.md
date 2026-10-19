# Bewertung des Treiberwegs (Weg B)

**Stand:** 2026-09-17, 22:30. Grundlage: [BEFUND-P0](BEFUND-P0-radiozugriff.md),
[BEFUND-P1-A2](BEFUND-P1-lan-responder.md), [BEFUND-P1-A3](BEFUND-P1-mac-messung.md).

Dieses Dokument ist eine Entscheidungsgrundlage, keine Empfehlung, den Weg zu gehen.

---

## 1. Warum es keinen kleineren Weg gibt

Vor der Aufwandsfrage steht die Frage, ob wirklich ein Treiber nötig ist. Geprüft und
beantwortet:

| Möglichkeit | Befund |
|---|---|
| Monitorbetrieb im vorhandenen Treiber | `uOpModeCapability = 0x00000074`, kein `NETWORK_MONITOR`-Bit |
| Beliebige Action-Frames aus dem Userspace senden | Es gibt kein solches OID. Alle Sende-OIDs in `windot11.h` sind Wi-Fi-Direct-spezifisch |
| NAN / Wi-Fi Aware unter Windows | **Existiert nicht** — weder in SDK 10.0.26100 noch 10.0.28000, weder user- noch kernelseitig |
| Neuerer Herstellertreiber (Weg C) | Auch das **neuere** Treibermodell WiFiCx kennt keine Tasks für NAN, Monitorbetrieb oder Rohrahmen. Ein anderer Treiber desselben Modells kann es deshalb nicht anbieten |
| NDIS-Filter | Sitzt oberhalb des Miniports, kann keine Funkbetriebsart ergänzen |
| AirDrop über das LAN | Existiert nicht: empfangsbereiter Mac kündigt nichts an, `sharingd` hat keinen TCP-Listener, `awdl0` trägt nur Link-Local-IPv6 |

**Der wichtigste Punkt ist die WiFiCx-Zeile.** Weg C ist damit nicht bloß
unwahrscheinlich, sondern ausgeschlossen: Das Windows-WLAN-Treibermodell hat für das,
was AWDL braucht, gar kein Konzept. Ein Treiber, der innerhalb dieses Modells arbeitet,
kann es nicht bereitstellen — unabhängig vom Hersteller und vom Alter.

Übrig bleibt nur ein Treiber, der das PCI-Gerät **außerhalb** des WLAN-Modells übernimmt.

---

## 2. Was günstiger ist als erwartet

**Die Firmware liegt bereits auf diesem Rechner.** Im signierten MediaTek-Paket:

```
WIFI_MT7961_patch_mcu_1_2_hdr.bin     92.192 Bytes
WIFI_RAM_CODE_MT7961_1.bin           764.232 Bytes
```

Das sind dieselben Dateien, die der Linux-Treiber `mt7921e` lädt. Die Firmwarebeschaffung
ist damit kein offener Punkt.

**Die Linux-Referenz ist permissiv lizenziert.** `mt76/mt7921/pci.c` trägt
`SPDX-License-Identifier: BSD-3-Clause-Clear`, Copyright MediaTek. BSD-3-Clause-Clear
erlaubt die Übernahme in ein nicht-offenes Windows-Projekt unter Namensnennung. Das war
die Annahme „GPL-2.0, Übernahme problematisch", die sich nicht bestätigt hat.

**Vorbehalt:** Das gilt für diese eine geprüfte Datei. `mt76` ist gemischt lizenziert;
vor der Übernahme jeder Datei ist ihr SPDX-Kopf einzeln zu prüfen, und für die
Firmwareblobs gilt MediaTeks eigene Lizenz, die noch nicht eingesehen wurde.

**Es gibt einen Präzedenzfall.** Das MT7921-Projekt (RECHERCHE Q5) berichtet
AirDrop-Empfang unter iOS 26 auf derselben Chipfamilie — unter Linux. Das beweist nichts
für Windows, zeigt aber, dass die Hardware die nötigen Funkoperationen beherrscht.

---

## 3. Was zu bauen wäre

Kein vollständiger WLAN-Treiber. Ein KMDF-Treiber, der das Gerät exklusiv übernimmt und
nur den Rohfunkpfad bereitstellt:

1. Bindung an `PCI\VEN_14C3&DEV_0608`, exklusiv — der MediaTek-Treiber muss dafür
   weichen. Solange unser Treiber läuft, gibt es auf diesem PC **kein normales WLAN**.
2. PCIe-BAR-Mapping, Interrupts (MSI), DMA-Puffer, TX-/RX-Ringverwaltung.
3. Firmware laden (Patch + RAM-Code), MCU-Kommandoprotokoll, Kalibrierungsdaten.
4. Kanalsteuerung auf die AWDL-Kanäle (6, 44, 149) unter Beachtung der Funkzulassung.
5. RX-Filter, Rahmenmetadaten, Hardwarezeitstempel.
6. TX-Deskriptoren für Action- und Datenrahmen, Sendestatus, Abbruch.
7. Userspace-Schnittstelle (IOCTL oder Shared-Memory-Ringe) für die AWDL-Engine.

Darüber dann, außerhalb des Kernels:

8. AWDL-Zustandsmaschine: Synchronisation, Verfügbarkeitsfenster, Master-Wahl,
   Kanalplan, Peer-Tabelle.
9. Virtueller Ethernet-Adapter, damit Windows IPv6/TCP/UDP darauf bereitstellt.

**Der schwierigste Teil ist nicht Punkt 1–7, sondern Punkt 8.** AWDL arbeitet mit
Verfügbarkeitsfenstern im Millisekundenbereich, und die Bestätigung empfangener
Unicast-Rahmen muss in Hardware/Firmware innerhalb von Mikrosekunden geschehen. Ein
Windows-Userspace-Timer ersetzt das nicht. Ob sich die MT7921-Firmware unter Windows so
ansteuern lässt, dass dieses Zeitverhalten trägt, ist die eigentliche offene Frage — und
sie lässt sich erst nach Punkt 1–6 beantworten.

---

## 4. Die Signierungsfrage — ehrlich

Ein selbst gebauter Kernel-Treiber lädt auf diesem System nicht.

| Weg | Voraussetzung | Eignung |
|---|---|---|
| Testsignierung | `bcdedit /set testsigning on`, **Secure Boot aus** | Nur Entwicklung. Sicherheitsrelevanter Eingriff, zustimmungspflichtig. Wasserzeichen. Laut Bauauftrag ausdrücklich **keine fertige Lösung** |
| Attestierungssignierung | EV-Code-Signing-Zertifikat plus Microsoft-Partner-Center-Konto | Der einzige Weg zu einer normal installierbaren Lösung. Laufende Kosten, Identitätsprüfung, mehrere Wochen Vorlauf |
| WHQL | zusätzlich HLK-Testlauf | Für dieses Vorhaben nicht erforderlich |

**Folge:** Selbst ein technisch gelungener Treiber wäre ohne EV-Zertifikat nur mit
abgeschaltetem Secure Boot nutzbar. Das ist laut Auftrag kein Abschluss, sondern ein
dokumentiertes Experiment.

---

## 5. Aufwand und Risiko

Die Architektur veranschlagt „Wochen bis Monate mit offenem Ausgang". Nach der jetzigen
Kenntnis ist das realistisch und eher die untere Grenze. Zur Einordnung: Der
Linux-Treiber für diese Chipfamilie umfasst mehrere tausend Zeilen, die auf
Kernel-Abstraktionen aufsetzen, die es unter Windows nicht gibt — `mac80211` müsste in
den Teilen, die AWDL braucht, mit ersetzt werden.

Risiken, nach Schwere:

1. **Zeitverhalten.** Trägt die Firmware das AWDL-Timing unter Windows? Nicht vorab
   beantwortbar. Wenn nein, endet der Weg nach dem Großteil des Aufwands.
2. **Signierung.** Ohne EV-Zertifikat kein normal installierbares Ergebnis.
3. **Exklusivität.** Während des Betriebs kein normales WLAN auf diesem PC. Für diesen
   Rechner verkraftbar, weil Ethernet vorhanden ist.
4. **Undokumentierte Hardware.** Die MCU-Kommandos sind nur über den Linux-Code
   erschlossen, nicht über ein Datenblatt.
5. **Wartung.** Jedes iOS-Update kann das Protokoll verschieben.

---

## 6. Was jetzt zu entscheiden ist

Der Auftrag sieht für diesen Punkt vor: Gibt es keinen belegbaren Weg, wird die konkret
fehlende Funktion benannt statt das Ziel zu ersetzen. Das ist hiermit geschehen — die
fehlende Funktion ist **Senden und Empfangen von 802.11-Rahmen auf den AWDL-Kanälen mit
tauglicher Zeitbasis**.

Drei mögliche Entscheidungen:

**(a) Treiberweg beginnen.** Erster Abschnitt wäre eine reine Machbarkeitsprüfung: PCI
übernehmen, Firmware laden, einen einzigen Rahmen empfangen. Das beantwortet Risiko 4
und einen Teil von Risiko 1, bevor der große Aufwand anfällt. Braucht Testsignierung,
also einen zustimmungspflichtigen Eingriff.

**(b) Mit dem Erreichten abschließen.** Die gesamte Anwendungsschicht steht und ist
getestet. Sie wäre sofort einsatzfähig, sobald ein AWDL-Transport existiert — auch ein
künftiger, den jemand anderes baut.

**(c) Weiter beobachten.** Google hat AirDrop-Interoperabilität für Android umgesetzt;
die EU verlangt von Apple die Unterstützung von Wi-Fi Aware. Beides könnte mittelfristig
einen dokumentierten Weg eröffnen. Beides ist heute keine Windows-Lösung.

Nicht empfohlen wird, den Treiberweg ohne vorherige Klärung der Signierungsfrage zu
beginnen: Ein Ergebnis, das nur mit abgeschaltetem Secure Boot läuft, erfüllt die
Abnahme nicht.

---

## Nachtrag 2026-09-18: Welche Messung ein Treiberprojekt tatsächlich bräuchte

Während der Messreihe entstand auf dem MacBook ein 18,9-MB-Mitschnitt eines echten
AirDrop-Transfers auf `awdl0`. Er wurde inzwischen gelöscht. Das ist weniger schlimm als
zunächst angenommen, und der Grund ist für die Planung wichtig.

**`awdl0` ist eine Netzwerkschnittstelle, kein Funkmitschnitt.** Ein `tcpdump` darauf
sieht, was der IP-Stack sieht — mDNS, TCP, QUIC:

```
IP6 fe80::1002.49157 > fe80::1003.59473: Flags [SEW]
IP6 fe80::1002.53476 > fe80::1003.60810: UDP, length 1200
```

Die 802.11-Action-Frames, in denen AWDLs Synchronisation, Master-Wahl,
Verfügbarkeitsfenster und Kanalplan stecken, liegen **darunter** und tauchen dort nicht
auf. Genau diese Frames sind der schwierige Teil eines Treibers.

**Was ein Treiberprojekt bräuchte**, ist deshalb ein anderer Messaufbau: ein
**Monitor-Mode-Mitschnitt der 802.11-Rahmen** auf den AWDL-Kanälen 6, 44 und 149. Auf
einem Mac ist das über Wireless Diagnostics oder `tcpdump -I` auf `en0` im Monitorbetrieb
möglich — der Mac kann also als Messgerät dienen, was dieser Windows-PC nicht kann.

Aus dem gelöschten Mitschnitt ist alles Relevante gesichert und in
[BEFUND-P1-mac-messung.md](BEFUND-P1-mac-messung.md) dokumentiert: beide Records
zeichengenau, Port 8770, UUID-Hostnamen, link-local-AAAA, die Aufteilung von 8.550
QUIC-Paketen gegen 165 TCP-Pakete, `_asquic._udp` auf Port 60810. Verloren sind nur die
Rohbytes, also das, was man erst durch erneutes Hineinsehen fände — und das wäre, siehe
oben, die falsche Schicht gewesen.

**Zusätzlicher Planungspunkt, der daraus folgt:** Die Nutzlast eines aktuellen
Mac↔iPhone-Transfers lief über QUIC auf UDP, nicht über HTTPS auf TCP. Ein Treiberprojekt
müsste damit rechnen, dass ein moderner Sender gegen einen Empfänger mit hohen
`flags`-Werten QUIC erwartet. Unser Empfänger bewirbt deshalb `0x0A`, wogegen iOS 26.6
nach fremdem Bericht auf HTTP über TCP zurückfällt. Ob das auch nach einer
AWDL-Erkennung gilt, ist unbewiesen und wäre einer der ersten Punkte, die ein
Treiberprojekt klären müsste.
