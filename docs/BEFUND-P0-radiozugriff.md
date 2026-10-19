# Befund P0: Radiozugriff über den vorhandenen Windows-Treiber

**Erhoben:** 2026-09-17 auf dem Zielrechner (DESKTOP-3DNHRAM).
**Werkzeug:** `airdrop-diag probe`, Rohausgaben in `evidence/runs/20260917-211916-probe/`.
**Eingriffe:** keine. Ausschließlich lesende Abfragen.

## Ergebnis in einem Satz

Der installierte MediaTek-Treiber bietet **keinen Network-Monitor-Modus** an. Damit ist
der dokumentierte Windows-Weg zu Raw-802.11-Empfang auf diesem Treiber geschlossen.

## Der entscheidende Messwert

`OID_DOT11_OPERATION_MODE_CAPABILITY` (0x0D010307), direkt am Miniport abgefragt über
`PacketRequest` aus Npcaps `Packet.dll`:

```
uOpModeCapability = 0x00000074
                  = DOT11_OPERATION_MODE_EXTENSIBLE_STATION (0x04)
                  | DOT11_OPERATION_MODE_WFD_DEVICE         (0x10)
                  | DOT11_OPERATION_MODE_WFD_GROUP_OWNER    (0x20)
                  | DOT11_OPERATION_MODE_WFD_CLIENT         (0x40)
```

`DOT11_OPERATION_MODE_NETWORK_MONITOR` ist `0x80000000` und in dieser Maske **nicht**
gesetzt. Die Konstanten stammen aus dem lokal installierten WDK-Header
`Windows Kits\10\Include\10.0.26100.0\shared\windot11.h`, nicht aus einer Vermutung.

Dieser Wert ist bewusst eine Zahl und kein übersetzter Konsolentext: Das System läuft auf
Deutsch, und lokalisierte `netsh`-Ausgabe wäre eine unzuverlässige Entscheidungsgrundlage.

## Drei unabhängige Bestätigungen

| Quelle | Aussage | Weg zur Hardware |
|---|---|---|
| `OID_DOT11_OPERATION_MODE_CAPABILITY` | Maske `0x00000074`, kein NetMon-Bit | Npcap → NDIS → Miniport |
| `netsh wlan show wirelesscapabilities` | „Netzwerküberwachungsmodus: Nicht unterstützt“, „Promiskuitiver Modus: Nicht unterstützt“ | Native-WiFi-Stack → WDI-Miniport, **ohne** Npcap |
| `WlanHelper.exe <guid> modes` | nur `managed` | Npcap-eigene Abfrage |

Die mittlere Zeile ist wichtig, weil sie Npcap vollständig umgeht. Der Befund hängt also
nicht an Npcaps Zustand.

## Was der Befund ausdrücklich *nicht* sagt

- **Nicht**, dass Npcaps `Dot11Support=0` die Ursache wäre. Diese Einstellung steuert, ob
  Npcap überhaupt versucht, einen Adapter in den Monitorbetrieb zu schalten. Sie kann dem
  Miniport keine Betriebsart hinzufügen, die er nicht meldet. Eine Npcap-Neuinstallation
  mit aktivierter 802.11-Option würde denselben Fähigkeitswert vorfinden.
- **Nicht**, dass der MT7921-Chip die Fähigkeit nicht besitzt. Unter Linux leistet
  dieselbe Chipfamilie Monitorbetrieb und Injection. Die Begrenzung liegt im
  Windows-Treiber, nicht nachweisbar im Silizium.
- **Nicht**, dass ein NDIS-Filter oder ein virtueller Adapter helfen könnte. Ein Filter
  sitzt oberhalb des Miniports und kann keine Funkbetriebsart ergänzen (RECHERCHE Q15).

## Relevanter Nebenbefund: Firmware ist lokal vorhanden

Das signierte MediaTek-Treiberpaket
`C:\Windows\System32\DriverStore\FileRepository\mtkwl6ex.inf_amd64_b4025423aaf9200d\`
enthält:

```
WIFI_MT7961_patch_mcu_1_2_hdr.bin    92.192 Bytes
WIFI_RAM_CODE_MT7961_1.bin          764.232 Bytes
```

Das sind **dieselben Dateinamen und dieselbe Rolle** wie bei der Firmware, die der
Linux-Treiber `mt76/mt7921` lädt (MT7961 ist die Chipbezeichnung hinter der Marke RZ608).
Für einen etwaigen eigenen Windows-Treiber ist die Firmwarebeschaffung damit kein offener
Punkt mehr. Die Lizenzlage für eine Weiterverwendung ist noch **nicht** geprüft.

Weitere Eckdaten des Treibers: WDI-Modell (`WiFiCx: nicht unterstützt`),
Windows-WDI-Version 0.1.1.13, IHV-WDI-Version 0.1.1.12, Firmwarestand `20220225140545`,
IHV-Erweiterungs-DLL `mtkihvx.dll`. Der Treiber ist vom 25.02.2022, Version 3.3.0.467.

## Fähigkeitsstand nach ARCHITEKTUR 5.1

| Fähigkeit | Stand | Begründung |
|---|---|---|
| Raw RX | `unsupported` | Kein NetMon-Bit am Miniport |
| Raw TX (Action) | `unsupported` | Kein dokumentierter Windows-Userspace-Pfad ohne NetMon |
| Raw TX (Data) | `unsupported` | dito |
| Kanalsteuerung | `unsupported` | Setzt Monitorbetrieb voraus |
| Zeitbasis | `unknown` | Nicht prüfbar ohne RX-Metadaten |
| Unicast/ACK | `unknown` | Nicht prüfbar ohne TX |
| MAC-Adressierung | `unknown` | Nicht prüfbar ohne TX |
| Lebenszyklus | `unknown` | Noch nicht geprüft |

## Damit erreichter Entscheidungspunkt

ARCHITEKTUR 5.2 greift: Der vorhandene Treiberpfad bietet die Funktionen nicht an. Die
verbleibenden Wege stehen in [ENTSCHEIDUNG-transportwege.md](ENTSCHEIDUNG-transportwege.md).

---

## Nachtrag: Gibt es einen anderen dokumentierten Sendepfad?

Der Treiber meldet „Aktionsrahmen: Unterstützt". Das wirft die berechtigte Frage auf, ob
AWDL-Action-Frames auch ohne Monitorbetrieb gesendet werden könnten. Die Antwort steht in
den Windows-Headern selbst.

Alle Frame-Sende-OIDs in `windot11.h` (10.0.26100) sind **Wi-Fi-Direct-spezifisch**:

```
OID_DOT11_WFD_SEND_GO_NEGOTIATION_REQUEST / RESPONSE / CONFIRMATION
OID_DOT11_WFD_SEND_INVITATION_REQUEST / RESPONSE
OID_DOT11_WFD_SEND_PROVISION_DISCOVERY_REQUEST / RESPONSE
```

Es gibt **kein** OID zum Senden eines beliebigen Action-Frames. Die gemeldete Fähigkeit
bezieht sich auf genau diese WFD-/ANQP-Rahmen, deren Inhalt der Windows-Stack erzeugt —
nicht auf einen freien Rahmeninhalt, wie AWDL ihn benötigt.

Ebenso enthalten die Header **keine** OIDs für NAN beziehungsweise Wi-Fi Aware. Der in der
Recherche erwähnte Weg über den NAN-Standard (Q14) ist unter Windows also auch dann kein
Zugang, wenn Apple ihn senderseitig anbietet: Es fehlt die Windows-API, nicht nur die
Treiberfähigkeit.

**Folge:** Die Einstufung `rawTxAction = unsupported` beruht nicht mehr nur darauf, dass
Monitorbetrieb fehlt, sondern zusätzlich darauf, dass Windows für diesen Zweck gar keine
Schnittstelle definiert. Das ist ein Befund aus der offiziellen Dokumentation, nicht aus
einem Versuch.
