# Recherche und Evidenz

Stand: **17.09.2026**. Primärquellen: Herstellerdokumentation, Originalprojekte und eigene Beobachtungen ihrer Autoren. Projektberichte sind keine unabhängigen Kompatibilitätsnachweise. Die folgenden Schlussfolgerungen unterscheiden Dokumentation, lokale Messung und offene Hypothesen.

## 1. Was für dieses Vorhaben belegt ist

| ID | Befund | Aussagekraft und Quelle |
|---|---|---|
| Q1 | Apple beschreibt BLE zur Erkennung, eine direkte WLAN-Verbindung und TLS für AirDrop. | Offizielle Beschreibung, veröffentlicht 2021; keine vollständige Spezifikation heutiger Versionen. [Apple: AirDrop security](https://support.apple.com/en-ca/guide/security/sec2261183f4/web) |
| Q2 | OpenDrop implementiert AirDrop oberhalb von AWDL; das Projekt bezeichnet sich als experimentell. Der unveränderte Empfang akzeptiert automatisch und besitzt Einschränkungen bei Authentifizierung und Verbindungszustand. | Wiederverwendbare Referenz, kein fertiger sicherer Windows-Empfänger. [SEEMOO OpenDrop](https://github.com/seemoo-lab/opendrop) |
| Q3 | OWL implementiert AWDL im Userspace, benötigt unter Linux Netlink und eine geeignete WLAN-Schnittstelle. Aktiver Monitorbetrieb und Frame-Injection sind entscheidend; gewöhnliche virtualisierte Netzwerkkarten reichen nicht. | Zeigt Anforderungen und vorhandene Protokollarbeit, keinen Windows-Port. [SEEMOO OWL](https://github.com/seemoo-lab/owl) |
| Q4 | `opendrop-rs` trennt AWDL (`filin`) und AirDrop (`luftlift`). Der Autor berichtet Empfang von iOS 18. | Gute Rust-Referenz. Ein LAN-Modus der Software beweist nicht, dass ein unverändertes iPhone dort nach AirDrop-Zielen sucht. [opendrop-rs](https://github.com/ayourtch-llm/opendrop-rs) |
| Q5 | Ein MT7921-Projekt berichtet erfolgreichen AirDrop-Empfang unter iOS 26 auf Linux. Dokumentiert sind etwa 45–67 kB/s und deutlich begrenzte Tests. | Hinweis auf prinzipielle Fähigkeiten dieser Chipfamilie; kein Nachweis für die Karte dieses PCs, Windows-Treiber oder konkrete iOS-Version. [airdrop-mt7921](https://github.com/jedbillyb/airdrop-mt7921) |
| Q6 | Linux ordnet die MediaTek-PCI-Geräte-ID `0x0608` dem MT7921-Treiber und dessen Firmware zu. | Konkrete Verbindung zwischen lokalem RZ608 und dem recherchierten Treiberzweig. [Linux: mt7921/pci.c](https://github.com/torvalds/linux/blob/master/drivers/net/wireless/mediatek/mt76/mt7921/pci.c) |
| Q7 | Npcap dokumentiert Raw-802.11-Empfang mit Radiotap und Monitorbetrieb abhängig von Hardware und Treiber. | Empfangsfähigkeit ist getrennt von Senden, Hardware-ACK und Kanalsteuerung zu prüfen. [Npcap Developer Guide](https://npcap.com/guide/npcap-devguide.html) |
| Q8 | Der Npcap-Tracker führt den Wunsch nach Raw-WLAN-Injection weiterhin offen; der ursprüngliche Bericht unterscheidet lokale Capture-Sichtbarkeit vom tatsächlichen Senden über Funk. | Starker Grund, `pcap_sendpacket` nicht als fertige AWDL-Sendefunktion einzuplanen. Ein offenes Issue allein beweist keine universelle Unmöglichkeit. [Npcap Issue 85](https://github.com/nmap/npcap/issues/85) |
| Q9 | WinDrop ist ein aktuelles C#-Windows-Projekt. Es berichtet Interoperabilität mit OpenDrop, bezeichnet die iPhone-Erreichbarkeit aber als blockiert. | Nützliche Windows-Referenz; kein erfolgreicher iPhone-Empfänger. [WinDrop](https://github.com/UvejsGj/WinDrop) |
| Q10 | Ein früherer eigener Implementierungsversuch beobachtete, dass iPhone/iPad AirDrop nicht über normale LAN-Ankündigungen nutzten. | Negative Beobachtung von 2021, kein Beweis für jede heutige Version. [AirDrop Anywhere, Originalbericht](https://bakedbean.org.uk/posts/2021-05-airdrop-anywhere-part-1/) |
| Q11 | Microsofts Wi-Fi-Direct-API implementiert Wi-Fi-P2P. | Daraus folgt keine AWDL-Kompatibilität. [Microsoft: Wi-Fi Direct](https://learn.microsoft.com/en-us/windows/win32/nativewifi/about-the-wi-fi-direct-api) |
| Q12 | Google bietet AirDrop-Interoperabilität auf ausgewählten Android-Geräten an. Die Windows-Hilfe beschreibt weiterhin Android–Windows-Transfers. | Drittanbieter-Interoperabilität ist möglich; Windows-Unterstützung lässt sich daraus nicht ableiten. [Google: Android/AirDrop](https://support.google.com/android/answer/9286773?hl=en), [Google: Windows](https://support.google.com/android/answer/13801258?hl=en) |
| Q13 | Apple beschreibt bei bestimmten Transfers mit neueren Systemen einen AirDrop-Code für nicht gespeicherte Kontakte. | Versionsabhängiges Testkriterium, keine pauschale Aussage über unsigned Drittanbieter-Empfänger. [Apple: AirDrop verwenden](https://support.apple.com/en-us/119857) |
| Q14 | Apple stellt Wi-Fi Aware für App- und Gerätekommunikation vor. | Eine verfügbare API ist kein dokumentierter Zugang zum nativen AirDrop-Menü. Die Architektur setzt keine automatische Ablösung von AWDL voraus. [Apple WWDC25: Wi-Fi Aware](https://developer.apple.com/videos/play/wwdc2025/228/) |
| Q15 | Windows-Filtertreiber sitzen zwischen Protokoll- und Miniporttreibern. | Ein Filter ersetzt keinen fehlenden Hardwarepfad im darunterliegenden WLAN-Treiber. [Microsoft: NDIS-Treibertypen](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/power-management--ndis-6-30-) |
| Q16 | Es gibt einen offenen NDIS-6-TAP-Treiber für Windows. | Kandidat für eine virtuelle Ethernet-Schnittstelle, getrennt vom physischen WLAN-Zugriff. [OpenVPN: tap-windows6](https://github.com/OpenVPN/tap-windows6) |
| Q17 | Windows hat Signierungsanforderungen für Kernel-Treiber. | Ein selbst gebautes `.sys` ist noch kein installierbarer Produktionspfad. [Microsoft: Driver Signing Policy](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/kernel-mode-code-signing-policy--windows-vista-and-later-) |
| Q18 | .NET 10 ist eine aktive LTS-Version. | Grundlage für die vorgeschlagene Windows-Anwendung. [Microsoft: .NET Support Policy](https://dotnet.microsoft.com/en-us/platform/support/policy) |

## 2. Protokolldetails und widersprüchliche Quellen

Die [Protokollnotizen von opendrop-rs](https://github.com/ayourtch-llm/opendrop-rs/blob/main/docs/airdrop-protocol.md) beschreiben Beobachtungen mit iOS 18.6.2 und macOS 15.7.3: IPv6, mDNS-Service `_airdrop._tcp`, HTTPS, `/Discover`, `/Ask`, `/Upload`, Property Lists und DVZip/CPIO. Die dort beschriebene einfache DVZip-Darstellung ist keine vollständige Spezifikation für alle Versionen.

Die [iOS-26-Patches des MT7921-Projekts](https://github.com/jedbillyb/airdrop-mt7921/blob/main/patches/README.md) ergänzen unter anderem HTTP-Chunking, einen Stored-Block-Indikator im DVZip-Längenfeld sowie Transferzustand. Sie beschreiben auch URL-Transfers ohne anschließenden Datei-Upload. Deshalb werden Codecvarianten anhand realer Testdaten ausgewählt. Die Implementierung darf nicht davon ausgehen, dass jeder DVZip-Block komprimiert ist oder jede Freigabe zwingend `/Upload` auslöst.

WinDrop äußert Zweifel an der Akzeptanz fremder Empfänger durch modernes iOS. Das MT7921-Projekt berichtet dagegen Erfolg unter iOS 26; Google dokumentiert kommerzielle Drittanbieter-Interoperabilität. Diese Befunde erlauben weder „iOS lässt keine fremden Empfänger zu“ noch „alle neuen iPhones funktionieren bereits“. Entscheidend sind genaue Builds, Transport, angebotene Fähigkeiten, Transaktion und getesteter Inhalt. [WinDrop](https://github.com/UvejsGj/WinDrop), [MT7921-Bericht](https://github.com/jedbillyb/airdrop-mt7921), [Google-Implementierung](https://blog.google/security/android-quick-share-support-for-airdrop-security/)

Auch die Behauptung, es gebe überhaupt keine fremde DVZip-Implementierung, wird nicht aus einem einzelnen README übernommen: Die beiden recherchierten Linux-Projekte enthalten entsprechende Arbeit. Ebenso sind pauschale Anweisungen fremder Projekte zum Abschalten von Windows-Schutzfunktionen kein Bestandteil dieses Bauplans.

## 3. Lokale, ausschließlich lesende Bestandsaufnahme

| Merkmal | Beobachtung |
|---|---|
| Windows | Windows 11 Pro, Version 10.0.26200 |
| WLAN | RZ608 Wi-Fi 6E 80MHz, MediaTek |
| PCI-ID | `VEN_14C3&DEV_0608`, Revision 00 |
| WLAN-Treiber | 3.3.0.467 vom 25.02.2022, NDIS 6.70 |
| Verbindung | Ethernet aktiv, WLAN zum Prüfzeitpunkt nicht verbunden |
| Npcap-DLL | Produkt-/Dateiversion 1.10.4 |
| Npcap-Dienst | Laufend |
| Npcap-Konfiguration | `Dot11Support=0` |
| `WlanHelper.exe WLAN modes` | `managed` |
| `WlanHelper.exe WLAN mode` | `managed` |
| iPhone | Nutzerangabe: neuestes installiertes iOS; genaue Version und Build noch unbekannt |
| MacBook | Nutzerangabe: macOS Sequoia; genaue 15.x-Version und Build noch unbekannt |

Verwendet wurden `Get-NetAdapter`, `Get-CimInstance Win32_OperatingSystem`, `netsh wlan show drivers`, PnP-Hardware-IDs, lesende Npcap-Registryabfragen und die beiden WlanHelper-Abfragen. Weder Monitorbetrieb noch Funk-Injection noch ein AirDrop-Transfer wurden gestartet.

**Interpretation:** Der aktuelle Softwarepfad bietet noch keinen nachgewiesenen AWDL-Zugriff. Die Kombination aus ausgeschaltetem Npcap-802.11-Support und nur gemeldetem `managed` erlaubt keine Aussage, dass der physische Chip grundsätzlich ungeeignet sei. Der Linux-Geräteeintrag ist ein gezielter Rechercheansatz für eine Windows-Treiberlösung. Er beweist keinen funktionierenden Port.

## 4. Entscheidungen für diesen Auftrag

- Hauptpfad: nativer Windows-Protokollstack plus AWDL-Radiozugriff auf den vorhandenen RZ608.
- LAN: kleiner, begrenzter Kompatibilitätsversuch; nur reale Erkennung und Übertragung durch beide Apple-Sender können ihn zum Produktpfad machen.
- Linux, WSL, Zusatzadapter und Mac-Relay: keine Laufzeitbestandteile. Linux-Quellcode dient nur als technische Referenz.
- Wi-Fi Direct, Wi-Fi Aware und BLE: keine austauschbaren Ersatznamen für AWDL. Zusätzliche Wege benötigen einen eigenen Nachweis im nativen AirDrop-Menü.
- „Nur Kontakte“ und die Übernahme von Apple-Account-Schlüsseln sind keine MVP-Anforderungen. Empfang wird lokal freigeschaltet und jeder Transfer bestätigt.
- Eine erneute GUI-Implementierung allein würde die wichtigste offene Frage nicht lösen. Der Funkpfad kommt vor der vollständigen Oberfläche.

## 5. Reproduzierbarkeit und Grenzen

Die am Recherchedatum über GitHubs API gelesenen Commitstände stehen in [research-pins.json](evidence/research-pins.json). Sie sind Quellenstände, keine getesteten oder freigegebenen Abhängigkeiten. Vor einer Übernahme werden Code, Lizenzdateien und tatsächlich ausgeführte Tests geprüft; kein fremdes Installationsskript wird ungeprüft ausgeführt.

OpenDrop, OWL und opendrop-rs kennzeichnen ihre Repositories als GPL-3.0, WinDrop als MIT. Konkrete Dateien, Änderungen, Firmware und zusammen ausgelieferte Komponenten müssen vor Übernahme in einer Komponentenliste mit ihrer jeweiligen Lizenz erfasst werden. Dieser Entwurf legt keine pauschale Lizenz für ein noch nicht existierendes Gesamtprodukt fest.

Nicht durchgeführt: Quellcode-Audit der vollständigen Referenzprojekte, Build ihrer Programme, Funkmitschnitt, Sender-Kompatibilitätstest, Treiberentwicklung oder Geschwindigkeitsmessung auf dem Ziel-PC.
