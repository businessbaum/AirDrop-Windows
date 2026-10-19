# Verbleibende Transportwege nach dem P0-Befund

Ausgangslage: [BEFUND-P0-radiozugriff.md](BEFUND-P0-radiozugriff.md). Der installierte
Treiber meldet keinen Monitorbetrieb. Damit entfällt der in ARCHITEKTUR 5.1 zuerst
geprüfte Weg.

Die folgenden Wege sind nach **Kosten je gewonnener Erkenntnis** sortiert, nicht nach
Erfolgswahrscheinlichkeit. Ein billiges Experiment, das eine teure Frage beantwortet,
kommt zuerst.

---

## Weg A — AirDrop über das vorhandene LAN (Infrastrukturpfad)

**Was geprüft wird:** Sucht ein unverändertes iPhone im AirDrop-Menü überhaupt über das
normale Heimnetz nach `_airdrop._tcp.local.`?

**Warum zuerst:** Die Frage ist in Minuten beantwortbar und entscheidet über Wochen
Aufwand. Fragt das iPhone auf diesem Link nie, kann kein Responder auf diesem Link je
gefunden werden — unabhängig davon, wie gut er implementiert ist.

**Zweistufig, damit die Aussage sauber bleibt:**

1. **A1 — Passive Beobachtung.** `airdrop-diag mdns-watch` protokolliert mit, während am
   iPhone das AirDrop-Blatt geöffnet wird. Ergebnis: sichtbare `_airdrop._tcp`-Anfragen
   vom iPhone, oder keine.
2. **A2 — Responder.** Nur falls A1 Anfragen zeigt: vollständiger mDNS-/HTTPS-Responder,
   und der echte Sendeversuch entscheidet.

**Vorbehalt aus der Recherche:** Q10 berichtet 2021, dass iPhone/iPad AirDrop nicht über
gewöhnliche LAN-Ankündigungen nutzten. Das ist eine alte Beobachtung und kein Beleg für
iOS von heute, aber es ist die wahrscheinlichste Erwartung. Der Mac ist ein Sonderfall: Er
besitzt mit `BrowseAllInterfaces` eine dokumentierte Einstellung, die den Finder über alle
Schnittstellen suchen lässt. Ein Erfolg allein auf dieser Basis erfüllt die Abnahme
**nicht**, weil er eine Senderänderung voraussetzt.

**Status:** A1 vorbereitet, noch `NOT_RUN` — braucht das iPhone.

---

## Weg B — Eigener Windows-Treiber für den RZ608

**Was entsteht:** Ein KMDF-Treiber, der das PCI-Gerät `14C3:0608` exklusiv übernimmt und
den Rohfunkpfad bereitstellt, den der Herstellertreiber nicht anbietet.

**Was dafür spricht:**

- Die Firmware liegt bereits auf diesem Rechner (`WIFI_RAM_CODE_MT7961_1.bin`,
  `WIFI_MT7961_patch_mcu_1_2_hdr.bin`) und ist dieselbe, die Linux verwendet.
- Mit `mt76/mt7921` existiert eine vollständige, lesbare Referenzimplementierung des
  Geräteverhaltens.
- Q5 belegt, dass diese Chipfamilie AirDrop-Empfang tatsächlich leisten kann — unter
  Linux, auf derselben Hardwaregeneration.

**Was dagegen spricht — ungeschönt:**

- Der Umfang ist nicht „ein paar IOCTLs“, sondern PCIe-Ressourcen, Interrupts, DMA-Ringe,
  MCU-Kommandoprotokoll, Kalibrierung, Kanalsteuerung, RX-Filter, TX-Deskriptoren. Die
  Architektur veranschlagt dafür Wochen bis Monate mit offenem Ausgang, und das ist
  realistisch.
- Der schwierigste Teil ist nicht der Empfang, sondern AWDLs Zeitverhalten:
  Verfügbarkeitsfenster im Bereich weniger Millisekunden und MAC-ACK für Unicast gehören
  in Firmware/Hardware. Ein Windows-Userspace-Timer ersetzt das nicht.
- **Signierung:** Ein selbst gebauter Kernel-Treiber lädt auf diesem System nicht. Der
  Produktionsweg verlangt ein EV-Zertifikat und Microsoft-Attestierung. Der
  Entwicklungsweg verlangt Testsignierung, und die setzt auf diesem Rechner das
  Abschalten von Secure Boot voraus. Beides ist ein Eingriff, der vorab besprochen wird;
  ein nicht signierbarer Treiber ist laut Bauauftrag ohnehin keine fertige Lösung.
- Lizenzfrage offen: `mt76` steht unter einer dualen Lizenz, die Firmwareblobs unter einer
  eigenen MediaTek-Lizenz. Eine Übernahme in ein Windows-Treiberprojekt ist vor dem ersten
  kopierten Zeilenblock zu klären, nicht danach.

**Status:** Voraussetzung (Firmware) erfüllt, Machbarkeit unbewiesen, `NOT_RUN`.

---

## Weg C — Anderer Windows-Treiber für dieselbe Hardware

**Was geprüft wird:** Ob ein neuerer oder alternativer signierter Treiber für
`14C3:0608` den Monitorbetrieb meldet. Der installierte Stand ist vom Februar 2022.

**Erwartung:** gering. Windows-WDI-Treiber bieten Monitorbetrieb praktisch nie an; er ist
für den Netzwerkbetrieb nicht erforderlich und wird deshalb selten implementiert. Die
Prüfung ist trotzdem billig genug, um sie zu machen, bevor Weg B begonnen wird.

**Eingriff:** Ein Treiberwechsel ist zustimmungspflichtig und wird mit dokumentiertem
Rückweg durchgeführt.

**Status:** `NOT_RUN`.

---

## Ausdrücklich verworfen

| Weg | Warum nicht |
|---|---|
| Npcap mit `Dot11Support=1` neu installieren | Ändert nichts an der Fähigkeitsmaske des Miniports. Die Einstellung steuert Npcaps Verhalten, nicht die des Treibers. |
| NDIS-Filtertreiber | Sitzt oberhalb des Miniports und kann keine Funkbetriebsart ergänzen (Q15). |
| Wi-Fi Direct | Der Treiber kann es, aber Apple-Geräte sprechen es nicht. Kein AWDL-Ersatz (Q11). |
| Virtueller Adapter (TAP/Wintun) allein | Stellt IP-Anbindung bereit, aber keinen Funkzugang (Q16). Wird in Weg A/B als Baustein gebraucht, ist aber kein Transport. |
| Linux, WSL, Zusatzadapter, Mac-Relay | Durch den Auftrag ausgeschlossen. |

---

## Nächster Schritt

Weg A1 ausführen. Das Ergebnis bestimmt, ob als Nächstes ein Responder (A2) oder die
Machbarkeitsanalyse für Weg B/C ansteht.
