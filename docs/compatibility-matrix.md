# Kompatibilitätsmatrix

Legende: **PASS** = real ausgeführt und bestanden · **FAIL** = real ausgeführt und
durchgefallen · **NOT_RUN** = noch nicht ausgeführt.

Ein Eintrag wird erst PASS, wenn er mit echter Apple-Hardware ausgeführt wurde. Tests
zwischen zwei selbst geschriebenen Programmen erzeugen keinen PASS.

**Stand:** 2026-09-18, 17:25.

## Senderbuilds

| Gerät | Version | Build | Erfasst am |
|---|---|---|---|
| iPhone | noch nicht erfasst | noch nicht erfasst | IP im Test: `192.168.178.128` (vom Nutzer bestätigt) |
| MacBook (Sequoia) | macOS 15 (`osxvers=24`), Modell `Mac16,12` | Build noch nicht erfasst | 2026-09-17, aus `_device-info` TXT |

Diese Felder werden vor dem ersten Kompatibilitätstest ausgefüllt und nicht geschätzt.

## P0 — Ausgangslage

| Prüfung | Ergebnis | Nachweis |
|---|---|---|
| Hardware-/Treiberstand erfasst | PASS | `evidence/runs/20260917-211916-probe/capabilities.json` |
| Diagnose-CLI vorhanden | PASS | `src/Receiver.Diagnostics`, Befehle `probe`, `mdns-watch` |
| Monitorbetrieb im Herstellertreiber | FAIL | `uOpModeCapability = 0x00000074`, kein NetMon-Bit — [Befund](BEFUND-P0-radiozugriff.md) |
| Raw RX über vorhandenen Treiber | FAIL | ebenda |
| Raw TX über vorhandenen Treiber | FAIL | ebenda |
| Kanalsteuerung über vorhandenen Treiber | FAIL | ebenda |
| mDNS-Empfangspfad des Werkzeugs | PASS | Eigene Abfrage und Fremdantworten empfangen — `evidence/runs/20260917-212329-mdns-watch/` |
| WLAN und Ethernet im selben Subnetz | PASS | WLAN `192.168.178.105`, Ethernet `192.168.178.103` |
| mDNS über den WLAN-Link empfangbar | PASS | Druckerantworten über ifIndex 6 — `evidence/runs/A1c-iphone-wlan/` |

## P1 — Transport

| Prüfung | Ergebnis | Nachweis |
|---|---|---|
| A1: iPhone sucht `_airdrop._tcp` im LAN | **PASS** | [Befund](BEFUND-P1-lan-discovery.md), `evidence/runs/A1c-iphone-wlan/` |
| A1: Mac sucht `_airdrop._tcp` im LAN (unverändert) | NOT_RUN | im selben Fenster nicht beobachtet; eigener Test nötig |
| A2: iPhone zeigt „Windows-PC“ im AirDrop-Menü (LAN) | NOT_RUN | setzt A1 voraus |
| A2: Mac zeigt „Windows-PC“ im AirDrop-Menü (LAN) | NOT_RUN | setzt A1 voraus |
| C: Alternativer Treiber meldet Monitorbetrieb | **ausgeschlossen** | WiFiCx kennt keine Tasks für NAN/Monitor/Rohrahmen — kein Treiber dieses Modells kann es anbieten |
| NAN / Wi-Fi Aware unter Windows verfügbar | **FAIL** | in SDK 26100 und 28000 nicht vorhanden |
| B: Eigener RZ608-Treiber, Raw RX über Funk | NOT_RUN | — |
| B: Eigener RZ608-Treiber, Raw TX über Funk | NOT_RUN | — |

## P2 — AWDL-Link

| Prüfung | Ergebnis |
|---|---|
| Peer-Erkennung gegen echtes Apple-Gerät | NOT_RUN |
| IPv6-Kommunikation über AWDL-Link, 10 Minuten stabil | NOT_RUN |

## P3 — Kleiner Transfer

| Prüfung | Ergebnis |
|---|---|
| Eine bekannte Datei vom iPhone vollständig empfangen | NOT_RUN |
| Eine bekannte Datei vom Sequoia-Mac vollständig empfangen | NOT_RUN |
| SHA-256 stimmt mit dem Original überein | NOT_RUN |
| iPhone-Empfang bei ausgeschaltetem Mac | NOT_RUN |

## P4 — Empfangsqualität

Die mit „eigener Stack" markierten Zeilen wurden gegen den **eigenen** Server mit einem
**eigenen** Client geprüft (`tools/selftest-upload.py`). Das belegt, dass die Teile
zusammenpassen — es ist **kein** Apple-Kompatibilitätsnachweis.

| Prüfung | Ergebnis |
|---|---|
| Upload ohne vorherige Annahme wird abgewiesen | **PASS** (eigener Stack) |
| Upload auf bereits verbrauchte Annahme wird abgewiesen | **PASS** (eigener Stack) |
| Mehrfachdateien inkl. Unterverzeichnis und leerer Datei | **PASS** (eigener Stack) |
| Atomare Veröffentlichung, kein Staging-Rest nach Erfolg | **PASS** (eigener Stack) |
| Kein Teilergebnis nach Fehler (Staging wird verworfen) | **PASS** (Codepfad + Test 6) |
| Pfadprüfung (`../`, UNC, NTFS-Streams, reservierte Namen) | **PASS** (Unit-Tests) |
| DVZip-/CPIO-Varianten gegen unabhängige Testdaten | **PASS** (Unit-Tests) |
| Ablehnung durch den Nutzer speichert keine Datei | NOT_RUN |
| Timeout speichert keine Datei | NOT_RUN |
| 100 MiB und 1 GiB mit begrenztem Speicher | NOT_RUN |
| Verhalten gegen ein echtes Apple-Archiv | NOT_RUN |

## Unabhängige Codec- und Sicherheitstests

Gegen Testdaten aus `tools/make-fixtures.py` — einer **getrennten** Implementierung
nach der Formatbeschreibung, nicht gegen den eigenen Encoder.

| Prüfung | Ergebnis |
|---|---|
| DVZip, zlib-Blöcke, SHA-256 gegen unkomprimiertes Original | **PASS** |
| DVZip, Stored-Blöcke (iOS-26-Variante) | **PASS** |
| DVZip, abgeschnittener Block wird abgewiesen | **PASS** |
| DVZip, Längenfeld über Limit wird abgewiesen | **PASS** |
| CPIO odc, Mehrfachdateien, leere Datei, Verzeichnis, 50 KB | **PASS** |
| CPIO, falsches Magic wird klar gemeldet | **PASS** |
| Pfad: `../`, absolut, Laufwerk, UNC | **PASS** |
| Pfad: NTFS-Datenstrom, reservierte Namen, Punkte am Ende | **PASS** |
| Pfad: casefold-Kollisionen | **PASS** |
| Gesamt | 33/33, `dotnet test tests/Receiver.Protocol.Tests` |

## P5 — Windows-App

| Prüfung | Ergebnis |
|---|---|
| Tray, Start/Stop, Sleep/Resume | NOT_RUN |
| Erforderliche Treiber in ladbar signierter Form | NOT_RUN |
