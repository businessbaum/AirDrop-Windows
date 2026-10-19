# AirDrop-Protokollprofil (Ausgangsstand)

**Zusammengetragen:** 2026-09-17 aus den in `evidence/research-pins.json` gepinnten
Quellen. **Nichts davon ist auf diesem Rechner gegen ein echtes Apple-Gerät geprüft.**
Jede Zeile ist Fremdbeobachtung, bis ein eigener Mitschnitt sie bestätigt.

## Erkennung

| Feld | Wert | Quelle |
|---|---|---|
| mDNS-Dienst | `_airdrop._tcp.local.` | OpenDrop `server.py:81` |
| TXT-Eintrag | genau ein Feld: `flags=<Dezimalzahl>` | OpenDrop `server.py:124` |
| HTTPS-Port | 8771 als Vorgabe | OpenDrop `config.py:60` |
| Instanzname | zufällige Dienst-ID, nicht der Anzeigename | OpenDrop `server.py:79` |

Der Port steht im SRV-Record und ist **keine Protokollkonstante** — OpenDrop erhöht ihn
bei Belegung selbst (`server.py:101`). Der Empfänger muss den tatsächlich gebundenen Port
ausrufen, nicht 8771 fest annehmen.

## `flags`-Bits

Aus `AirDropReceiverFlags` in OpenDrop `config.py`, dort angegeben als aus
`sharingd` rekonstruiert:

```
0x001  SUPPORTS_URL
0x002  SUPPORTS_DVZIP
0x004  SUPPORTS_PIPELINING
0x008  SUPPORTS_MIXED_TYPES
0x010  SUPPORTS_UNKNOWN1
0x020  SUPPORTS_UNKNOWN2
0x040  SUPPORTS_IRIS
0x080  SUPPORTS_DISCOVER_MAYBE
0x100  SUPPORTS_UNKNOWN3
0x200  SUPPORTS_ASSET_BUNDLE
```

OpenDrop nennt `0x3fb` als macOS-Vorgabe und merkt an, ein gültiger Knoten brauche
entweder `SUPPORTS_PIPELINING` oder `SUPPORTS_MIXED_TYPES`.

**Entscheidung für unseren Empfänger:** Wir bewerben nur, was wir implementiert haben.
`0x3fb` blind zu kopieren würde Fähigkeiten zusagen, die wir nicht erfüllen — etwa
`SUPPORTS_IRIS` oder `SUPPORTS_ASSET_BUNDLE`. Der konkrete Wert wird festgelegt, wenn die
Empfangsseite steht, und im Profil begründet.

## HTTP-Endpunkte

| Endpunkt | Anfrage | Antwort |
|---|---|---|
| `/Discover` | Sender-Record als Plist | `ReceiverComputerName`, `ReceiverModelName`, `ReceiverMediaCapabilities` |
| `/Ask` | Binär-Plist mit Transfermetadaten | `ReceiverComputerName`, `ReceiverModelName`; HTTP 200 bedeutet Annahme |
| `/Upload` | Archiv im Body | Statuscode |

`/Ask`-Felder laut opendrop-rs: `TransferID`, `TransferType`, `SenderID`,
`SenderComputerName`, `SenderModelName`, `BundleID`, `Files`, `Items`, `FileIcon`,
`SenderRecordData`.

**Wichtig für unsere Zustandsmaschine:** Bei AirDrop bedeutet ein HTTP-200 auf `/Ask`
bereits die Annahme. Der Annahmedialog muss also **vor** der Antwort auf `/Ask` laufen,
nicht danach. Ein sofortiges 200 mit späterer Nachfrage gibt es nicht.

## Transfer-Encoding

opendrop-rs beobachtet für iOS 18, dass `/Discover` und `/Ask` ihre Bodies mit
`Transfer-Encoding: chunked` **ohne** `Content-Length` senden. Ein Parser, der auf
`Content-Length` besteht, scheitert daran.

## Archivformat

`Content-Type: application/x-dvzip` (iOS 18) beziehungsweise `application/x-cpio`
(älter). DVZip laut opendrop-rs:

```
[4 Byte Blocklänge, big-endian][zlib-Daten (Kopf 0x78 0x9c)]  ... wiederholt
```

Die aneinandergehängten entpackten Blöcke ergeben ein CPIO-Archiv im ODC-Format
(Magic `070707`).

**Widerspruch in den Quellen, der Testdaten braucht:** Die iOS-26-Patches des
MT7921-Projekts beschreiben zusätzlich einen *Stored-Block*-Indikator im Längenfeld, also
unkomprimierte Blöcke. Ein Decoder, der jeden Block als zlib annimmt, bricht daran. Beide
Varianten gehören abgedeckt, und welche real auftritt, entscheidet ein echter Mitschnitt —
nicht diese Tabelle.

Ebenfalls dort beschrieben: URL-Freigaben, die **keinen** `/Upload` nach sich ziehen. Die
Zustandsmaschine darf nach `/Ask` also nicht zwingend einen Upload erwarten.

## TLS

opendrop-rs berichtet, dass selbstsignierte Zertifikate für den Empfang genügen und eine
Client-Zertifikatsprüfung dafür nicht nötig ist. Das ist eine Fremdbeobachtung für eine
bestimmte iOS-Version und **für unsere Zielversion noch nachzuweisen**.
