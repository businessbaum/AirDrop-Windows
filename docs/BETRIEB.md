# Bauen, Betreiben, Zurücknehmen

Alles, was dieses Projekt auf dem Rechner verändert hat, steht hier — zusammen mit dem
Weg zurück. Stand: 2026-09-17.

## Bauen

Voraussetzung: .NET SDK 10 (`dotnet --list-sdks` zeigt hier `10.0.300`).

```powershell
cd "<repo>"
dotnet build AirDropWindows.slnx -c Release
```

Testdaten für die Codec-Tests erzeugen (braucht Python 3):

```powershell
python tools\make-fixtures.py tests\fixtures
dotnet test tests\Receiver.Protocol.Tests
```

Erwartet: 33 bestandene Tests.

## Diagnose

```powershell
# Fähigkeiten der WLAN-Hardware, rein lesend, schreibt evidence/runs/<zeit>-probe/
.\src\Receiver.Diagnostics\bin\Release\net10.0-windows\airdrop-diag.exe probe

# mDNS mitschneiden; sendet nichts, ausser mit --query
.\src\Receiver.Diagnostics\bin\Release\net10.0-windows\airdrop-diag.exe mdns-watch --seconds 120 --all
.\src\Receiver.Diagnostics\bin\Release\net10.0-windows\airdrop-diag.exe mdns-watch --seconds 30 --query
```

`probe` verändert nichts. `mdns-watch` sendet nur mit `--query`, und dann genau eine
DNS-SD-Abfrage.

## Empfänger starten

```powershell
.\src\Receiver.Host\bin\Release\net10.0\airdrop-receiver.exe `
    --name "Windows-PC" --interface WLAN --flags 0x3fb
```

| Schalter | Bedeutung |
|---|---|
| `--name` | Angezeigter Name, Vorgabe `Windows-PC` |
| `--interface` | Nur diese Schnittstelle bedienen |
| `--port` | HTTPS-Port, Vorgabe 8771 |
| `--flags` | TXT-`flags`, Vorgabe `0x04` |
| `--out` | Zielordner, Vorgabe `%USERPROFILE%\Downloads\AirDrop` |
| `--with-ipv6` | Auch IPv6-Adressen ankündigen (Vorgabe: nur IPv4) |
| `--auto-accept` | **Nur für Selbsttests.** Nimmt jeden Transfer ohne Rückfrage an |
| `--bare-answers` | Diagnose: PTR ohne Zusatz-Records beantworten |

Beenden mit `Ctrl+C`. Der Empfänger sendet dabei Goodbye-Records (TTL 0), damit Sender
ihn sofort vergessen.

Selbsttest des eigenen Stacks (kein Apple-Kompatibilitätsnachweis):

```powershell
python tools\selftest-upload.py 192.168.178.105:8771 tests\fixtures\multi-file.dvzip "$env:TEMP\airdrop-selftest"
```

---

## Was auf diesem Rechner verändert wurde — und wie es zurückgeht

### 1. Eingehende Firewall-Regel

**Eingriff:** Nach ausdrücklicher Zustimmung am 2026-09-17 angelegt.

```
Name     : AirDrop Receiver (TCP 8771, privat)
Richtung : eingehend, Aktion Zulassen
Protokoll: TCP, lokaler Port 8771
Profil   : nur Privat
Programm : ...\src\Receiver.Host\bin\Release\net10.0\airdrop-receiver.exe
```

Öffentliche und Domänenprofile blieben unberührt, ebenso alle anderen Ports.

**Zurücknehmen** (Administrator-PowerShell):

```powershell
Remove-NetFirewallRule -DisplayName 'AirDrop Receiver (TCP 8771, privat)'
```

Windows hat beim ersten Start zusätzlich selbst zwei Regeln für dieselbe EXE im Profil
*Öffentlich* angelegt. Auch die lassen sich entfernen:

```powershell
Get-NetFirewallRule -DisplayName 'airdrop-receiver.exe' | Remove-NetFirewallRule
```

### 2. WLAN-Verbindung

**Eingriff:** Der WLAN-Adapter wurde mit dem bereits gespeicherten Profil
„FRITZ!Box 7490" verbunden. Vorher war er getrennt. Es wurde **kein** Profil angelegt,
kein Kennwort gespeichert, keine Einstellung geändert.

**Zurücknehmen:**

```powershell
netsh wlan disconnect interface="WLAN"
```

### 3. Lokaler Zustand des Programms

`.state\` im Projektordner enthält das selbstsignierte Zertifikat (`receiver.pfx`) und
die Dienstkennung (`service-id.txt`). Beides wird beim nächsten Start neu erzeugt.

```powershell
Remove-Item .state -Recurse -Force
```

Das Zertifikat wurde **nicht** in einen Windows-Zertifikatspeicher installiert. Es liegt
nur als Datei vor und wird ausschließlich von diesem Listener verwendet.

### 4. Was ausdrücklich NICHT verändert wurde

- Kein Treiber installiert, ersetzt oder aktualisiert.
- Npcap nicht neu installiert; `Dot11Support` bleibt auf `0`.
- Secure Boot, Testsignierung und Treibersignaturprüfung unangetastet.
- Keine Systemdienste angelegt, kein Autostart eingerichtet.
- Nichts am iPhone oder am MacBook konfiguriert. Die Mac-Messungen waren ausschließlich
  lesende Befehle.

### Vollständige Deinstallation

```powershell
# 1. Empfänger beenden
Stop-Process -Name airdrop-receiver -Force -ErrorAction SilentlyContinue

# 2. Firewall-Regeln entfernen (als Administrator)
Remove-NetFirewallRule -DisplayName 'AirDrop Receiver (TCP 8771, privat)' -ErrorAction SilentlyContinue
Get-NetFirewallRule -DisplayName 'airdrop-receiver.exe' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

# 3. WLAN trennen, falls gewünscht
netsh wlan disconnect interface="WLAN"

# 4. Lokalen Zustand löschen
Remove-Item .state -Recurse -Force -ErrorAction SilentlyContinue
```

Danach ist der Rechner in dem Zustand, in dem er am 2026-09-17 vor Beginn war. Der
Projektordner selbst kann gelöscht werden; er enthält keine Systembestandteile.

---

## Was der Empfänger heute leistet — und was nicht

**Funktioniert, gegen den eigenen Stack geprüft:** DNS-SD-Ankündigung (von Apples
`mDNSResponder` als korrekt bestätigt), HTTPS mit selbstsigniertem Zertifikat,
`/Discover`, `/Ask` mit Annahmedialog, `/Upload` mit DVZip- und CPIO-Dekodierung,
Pfadprüfung, atomare Ablage, Abweisung von Uploads ohne oder nach verbrauchter Annahme.

**Funktioniert nicht:** Der Empfang von einem echten iPhone oder Mac. Grund ist nicht
diese Software, sondern der fehlende AWDL-Transport —
siehe [BEFUND-P1-mac-messung.md](BEFUND-P1-mac-messung.md).

**Noch nicht gebaut:** Tray-Oberfläche, Autostart, Sleep/Resume-Behandlung,
Paketierung. Diese Arbeiten wurden bewusst zurückgestellt, weil eine Oberfläche die
offene Transportfrage nicht beantwortet.
