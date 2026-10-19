# Einen echten AirDrop-Transfer auf dem Mac mitlesen

Ziel: herausfinden, **was ein echtes AirDrop-Ziel ankündigt** und **über welche
Schnittstelle** der Transfer läuft. Alles hier ist reine Beobachtung — es wird nichts
verändert, keine Einstellung gesetzt, keine App installiert. Der Mac dient als
Messgerät, nicht als Laufzeitbestandteil.

Führe die Blöcke der Reihe nach aus. Schick mir jeweils die Ausgabe.

---

## Block 1 — Was kündigt ein echtes AirDrop-Ziel an? (kein sudo)

Das ist der wertvollste Teil: Wir sehen den echten TXT-Record eines Apple-Empfängers und
können unseren Feld für Feld dagegen halten.

**Vorbereitung:** Am iPhone Einstellungen → Allgemein → AirDrop → „Für alle" (oder
„Nur für Kontakte", Hauptsache empfangsbereit). Das iPhone entsperrt lassen.

```bash
# Terminal-Fenster 1 — laufen lassen
dns-sd -B _airdrop._tcp
```

Es erscheinen Zeilen mit Instanznamen (12 Hexzeichen). Notiere den, der **nicht**
`bf49b82d9499` ist — das ist das iPhone.

```bash
# Terminal-Fenster 2 — <INSTANZ> durch den Namen aus Fenster 1 ersetzen
dns-sd -L <INSTANZ> _airdrop._tcp local
```

**Das brauche ich daraus:** die komplette Ausgabe, besonders
- den Port hinter `can be reached at ...`
- den kompletten TXT-Inhalt (alles in Anführungszeichen dahinter)
- den Hostnamen

Beide Fenster mit `Ctrl+C` beenden.

---

## Block 2 — Auf welcher Schnittstelle läuft AirDrop wirklich? (sudo)

```bash
# Welche Schnittstellen gibt es, und ist awdl0 aktiv?
ifconfig awdl0
ifconfig en0 | head -5
```

Dann der eigentliche Mitschnitt. **Zwei Terminal-Fenster gleichzeitig:**

```bash
# Fenster 1 — AWDL
sudo tcpdump -i awdl0 -n -s 0 -w ~/airdrop-awdl.pcap
```

```bash
# Fenster 2 — normales WLAN, nur die interessanten Ports
sudo tcpdump -i en0 -n -s 0 -w ~/airdrop-en0.pcap \
  'port 5353 or portrange 8770-8780'
```

**Während beide laufen:** Vom iPhone eine kleine Datei per AirDrop an den Mac schicken
und am Mac annehmen. Danach beide mit `Ctrl+C` beenden.

```bash
# Kurzüberblick, ohne mir die Dateien zu schicken
echo "--- AWDL ---"; tcpdump -r ~/airdrop-awdl.pcap -n | head -60
echo "--- EN0 ---";  tcpdump -r ~/airdrop-en0.pcap  -n | head -60
echo "--- Paketzahlen ---"
tcpdump -r ~/airdrop-awdl.pcap -n 2>/dev/null | wc -l
tcpdump -r ~/airdrop-en0.pcap  -n 2>/dev/null | wc -l
```

**Die entscheidende Frage:** Läuft die TCP-Sitzung auf `awdl0` oder auf `en0`?
Das beantwortet, ob AirDrop grundsätzlich an AWDL gebunden ist.

Die beiden `.pcap`-Dateien kannst du auf den PC kopieren — ich werte sie dort aus.
Sie enthalten den TLS-verschlüsselten Dateiinhalt nicht im Klartext, aber Ports,
Reihenfolge, Paketgrößen und den gesamten mDNS-Verkehr.

---

## Block 3 — Sieht der Mac unseren PC? (kein sudo)

Während auf dem PC `airdrop-receiver` läuft:

```bash
dns-sd -L bf49b82d9499 _airdrop._tcp local
```

Kommt hier ein Ergebnis mit Port 8771, ist bewiesen, dass Apples Stack unseren Record
vollständig auflösen kann — nicht nur auflisten.

---

## Was ich damit vorhabe

| Beobachtung | Was sie entscheidet |
|---|---|
| Echter TXT-Record des iPhones | Ob unserem Record Felder fehlen, die AirDrop verlangt |
| Echter Port | Ob 8771 stimmt oder pro Gerät wechselt |
| TCP auf `awdl0` statt `en0` | Ob AirDrop fest an AWDL gebunden ist |
| mDNS auf `en0` vorhanden oder nicht | Bestätigt oder widerlegt unsere Windows-Messung von 22:12 |
| `dns-sd -L` auf unsere Instanz | Ob Apple uns nicht nur listet, sondern auch auflöst |

Ergibt Block 2, dass alles ausschließlich über `awdl0` läuft, ist der LAN-Weg
abschließend beantwortet und der Funkpfad die verbleibende Aufgabe. Ergibt sich dagegen
Verkehr auf `en0`, fehlt uns nur noch ein Detail — und dann steht es in diesen Dateien.
