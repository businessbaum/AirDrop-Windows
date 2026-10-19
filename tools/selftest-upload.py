#!/usr/bin/env python3
"""
End-to-end self test of the receiver's own stack: /Ask -> acceptance -> /Upload ->
DVZip -> CPIO -> files on disk, plus the negative cases around the acceptance binding.

This is explicitly NOT an Apple compatibility test. It drives our own server with our
own client, which proves the pieces fit together and nothing more. Apple interop stays
NOT_RUN until a real iPhone and a real Mac have sent something.

Usage:
  python tools/selftest-upload.py <host:port> <fixture.dvzip> <destination-dir>
"""

import hashlib
import http.client
import os
import plistlib
import ssl
import sys


def post(host, port, path, body, content_type="application/octet-stream"):
    ctx = ssl.create_default_context()
    # The receiver serves a self-signed certificate on purpose; this client is only
    # checking our own endpoints, so certificate validation is not the subject here.
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE

    conn = http.client.HTTPSConnection(host, port, context=ctx, timeout=30)
    conn.request("POST", path, body=body, headers={
        "Content-Type": content_type,
        "Content-Length": str(len(body)),
    })
    response = conn.getresponse()
    data = response.read()
    conn.close()
    return response.status, data


def ask_plist(sender="Selbsttest-iPhone", files=("hello.txt",)):
    return plistlib.dumps({
        "TransferID": "11111111-2222-3333-4444-555555555555",
        "SenderID": "selftest",
        "SenderComputerName": sender,
        "SenderModelName": "iPhone17,1",
        "BundleID": "com.apple.finder",
        "Files": [
            {"FileName": name, "FileType": "public.data", "FileBundleName": name}
            for name in files
        ],
    }, fmt=plistlib.FMT_BINARY)


def main():
    if len(sys.argv) < 4:
        print(__doc__)
        return 2

    host, _, port = sys.argv[1].partition(":")
    port = int(port or 8771)
    fixture = sys.argv[2]
    destination = sys.argv[3]

    payload = open(fixture, "rb").read()
    failures = []

    def check(name, ok, detail=""):
        print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  -- {detail}" if detail else ""))
        if not ok:
            failures.append(name)

    print("1) Upload ohne vorherige Annahme muss abgewiesen werden")
    status, _ = post(host, port, "/Upload", payload, "application/x-dvzip")
    check("Upload ohne Ask -> 403", status == 403, f"HTTP {status}")

    print("2) Discover")
    status, data = post(host, port, "/Discover", b"")
    ok = status == 200 and data.startswith(b"bplist00")
    name = ""
    if ok:
        parsed = plistlib.loads(data)
        name = parsed.get("ReceiverComputerName", "")
    check("Discover -> 200 + Binaerplist", ok, f"HTTP {status}, Name '{name}'")

    print("3) Ask (Empfaenger laeuft mit --auto-accept)")
    status, data = post(host, port, "/Ask", ask_plist())
    check("Ask -> 200", status == 200, f"HTTP {status}")

    print("4) Upload mit gueltiger Annahme")
    before = set(os.listdir(destination)) if os.path.isdir(destination) else set()
    status, _ = post(host, port, "/Upload", payload, "application/x-dvzip")
    check("Upload -> 200", status == 200, f"HTTP {status}")

    after = set(os.listdir(destination)) if os.path.isdir(destination) else set()
    new_dirs = [d for d in (after - before) if not d.startswith(".")]
    check("genau ein neuer Transferordner", len(new_dirs) == 1, str(sorted(new_dirs)))

    if len(new_dirs) == 1:
        folder = os.path.join(destination, new_dirs[0])
        found = []
        for root, _, names in os.walk(folder):
            for n in names:
                full = os.path.join(root, n)
                digest = hashlib.sha256(open(full, "rb").read()).hexdigest()[:16]
                found.append((os.path.relpath(full, folder), os.path.getsize(full), digest))
        for rel, size, digest in sorted(found):
            print(f"       {rel:<28} {size:>8} B  sha256:{digest}")
        check("mindestens eine Datei gespeichert", len(found) > 0, f"{len(found)} Dateien")

    print("5) Zweiter Upload auf dieselbe Annahme muss abgewiesen werden")
    status, _ = post(host, port, "/Upload", payload, "application/x-dvzip")
    check("Wiederverwendung -> 403", status == 403, f"HTTP {status}")

    print("6) Kein Staging-Rest")
    staging = os.path.join(destination, ".incoming")
    leftovers = os.listdir(staging) if os.path.isdir(staging) else []
    check("Staging leer", len(leftovers) == 0, str(leftovers))

    print()
    if failures:
        print(f"FEHLGESCHLAGEN: {len(failures)} -> {', '.join(failures)}")
        return 1
    print("Alle Selbsttests bestanden.")
    print("HINWEIS: Das ist ein Test des eigenen Stacks, KEIN Apple-Kompatibilitaetsnachweis.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
