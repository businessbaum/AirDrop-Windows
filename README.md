<div align="center">

# AirDrop for Windows

**I tried to make a Windows PC appear in the normal AirDrop menu of an iPhone. The software part works. I also found out why the last step does not work with a normal Wi-Fi card.**

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![Tests](https://img.shields.io/badge/tests-57%20passing-2EA44F)
![Status](https://img.shields.io/badge/status-research%20project-E3B341)

</div>

## The goal

You choose the PC in the AirDrop menu on your iPhone, Windows asks if you want the file, and the file is saved. No extra app on the iPhone, no Linux and no second computer. Only the hardware I already had.

## What I found out

Everything above the radio works. The radio part does not.

| Question | Answer |
|---|---|
| Can my Wi-Fi driver send and receive raw Wi-Fi frames? | No, the driver does not support monitor mode. |
| Is there another way on Windows? | No, Windows has no API for the kind of Wi-Fi AirDrop uses. |
| Does the iPhone look for AirDrop on the normal network? | Yes, I could see its requests in my logger. |
| Is my announcement correct? | Yes, Apple's own tool finds the PC. |
| Does AirDrop send files over the normal network? | No, only over AWDL, Apple's own direct Wi-Fi connection. |

So the missing piece is a Wi-Fi driver that can speak AWDL. Normal Windows drivers can't do that. Everything after that point is built.

## What I built

| Part | What it does |
|---|---|
| Receiver.Mdns | Announces the PC on the network like an Apple device. |
| Receiver.Host | The web server that AirDrop talks to, plus the "accept this file?" dialog. |
| Receiver.Protocol | Unpacks the files AirDrop sends and makes sure no file can be saved outside the download folder. |
| Receiver.Diagnostics | A small tool that asks the Wi-Fi driver what it can really do. |
| tests | 57 tests, including broken archives and file names that try to escape the folder. |

## How I approached it

1. I checked the hardware first and did not trust what the internet says.
2. I tried the easy way and announced the PC on the normal network. Apple's tools saw it, but the iPhone never connected.
3. I measured on my Mac and found out that AirDrop only sends files over AWDL.
4. I wrote every measurement and decision down in the docs folder, in German.

## What could come next

- Writing my own Wi-Fi driver, based on the open source Linux driver. That would take weeks or months and only works with Secure Boot turned off.
- Waiting. Google already made Android work with AirDrop, so an official way might come at some point.
- Calling it done. The software part is finished.

## Run the tests

```powershell
dotnet test tests/Receiver.Protocol.Tests
```

The project only changes two things on Windows: one firewall rule and a Wi-Fi connection you already know. How to undo both is written in docs/BETRIEB.md.

## Documents (in German)

- [Findings about the hardware](docs/BEFUND-P0-radiozugriff.md)
- [What the iPhone does on the network](docs/BEFUND-P1-lan-discovery.md)
- [Measurements on the Mac](docs/BEFUND-P1-mac-messung.md)
- [Conclusion](docs/BEFUND-P1-abschluss.md)
- [The driver option](docs/ENTSCHEIDUNG-treiberweg.md)
- [Architecture](ARCHITEKTUR.md) and [research](RECHERCHE.md)

The raw logs are in the evidence folder. I removed the names and addresses of the devices in my home network from them.

## License

MIT, see [LICENSE](LICENSE).
