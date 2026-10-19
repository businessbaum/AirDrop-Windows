# Measurement runs

Raw output of the diagnostics CLI (`airdrop-diag probe`, `mdns-watch`) and the receiver host,
one folder per run, named by timestamp or test id.

Two kinds of data were changed before publishing:

- **Raw mDNS captures (`mdns-watch.txt`) are not included.** They list every device on the home
  network that was announcing itself during the test. The findings in [`docs/`](../../docs/) quote
  the lines that matter.
- **Identifiers are redacted** in the remaining logs: MAC addresses (`02:00:00:00:00:xx`), link-local
  and public IPv6 addresses (`fe80::1xxx`, `2001:db8::1`), user paths and device names. Private
  IPv4 addresses (`192.168.178.x`) are left as they were, since they identify nothing.
