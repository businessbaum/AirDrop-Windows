#!/usr/bin/env python3
"""
Generates DVZip/CPIO test fixtures for the receiver's decoders.

Deliberately written against the format description alone, with no reference to the C#
decoder. A round trip through one implementation proves only that it is self-consistent;
these fixtures are produced by a second, independent implementation so that a shared
misreading of the format shows up as a failing test instead of a passing one.

Format sources:
  CPIO odc  - POSIX.1 portable: 76-byte header, octal fields, NUL-terminated name,
              nothing padded. This is what opendrop writes (libarchive's "cpio").
  CPIO newc - SVR4: 110-byte header, hexadecimal fields, name and data each padded
              to a 4-byte boundary. This is what Apple writes.
  DVZip     - repeated [4-byte big-endian length word][block]. Bit 31 of the length
              word marks a stored (uncompressed) block; the length is the low 31 bits.
  gzip      - opendrop sends this with no Content-Encoding header at all.

Usage:  python tools/make-fixtures.py [output-dir]
"""

import gzip
import os
import struct
import sys
import zlib

S_IFREG = 0o100000
S_IFDIR = 0o040000

NUL = b"\x00"


def odc_header(name: bytes, size: int, mode: int, ino: int) -> bytes:
    """76 ASCII bytes, every numeric field octal and zero-padded."""
    def o(value: int, width: int) -> bytes:
        text = format(value, "o")
        if len(text) > width:
            raise ValueError(f"{value} does not fit in {width} octal digits")
        return text.rjust(width, "0").encode("ascii")

    return b"".join([
        b"070707",            # magic
        o(0, 6),              # dev
        o(ino, 6),            # ino
        o(mode, 6),           # mode
        o(0, 6),              # uid
        o(0, 6),              # gid
        o(1, 6),              # nlink
        o(0, 6),              # rdev
        o(0, 11),             # mtime
        o(len(name) + 1, 6),  # namesize, including the trailing NUL
        o(size, 11),          # filesize
    ])


def newc_header(name: bytes, size: int, mode: int, ino: int) -> bytes:
    """110 ASCII bytes, every numeric field 8 hex digits."""
    def h(value: int) -> bytes:
        return format(value, "08X").encode("ascii")

    return b"".join([
        b"070701",
        h(ino), h(mode), h(0), h(0), h(1), h(0), h(size),
        h(0), h(0), h(0), h(0),
        h(len(name) + 1),     # namesize, including the trailing NUL
        h(0),                 # check
    ])


def pad4(length: int) -> bytes:
    return NUL * ((4 - length % 4) % 4)


def build_odc(entries) -> bytes:
    """entries: list of (name, data_or_None). None means a directory."""
    out = bytearray()
    for ino, (name, data) in enumerate(entries, start=1):
        raw = name.encode("utf-8")
        payload = b"" if data is None else data
        mode = S_IFDIR | 0o755 if data is None else S_IFREG | 0o644
        out += odc_header(raw, len(payload), mode, ino) + raw + NUL + payload

    trailer = b"TRAILER!!!"
    out += odc_header(trailer, 0, 0, 0) + trailer + NUL
    return bytes(out)


def build_newc(entries) -> bytes:
    """newc: header+name padded to 4 bytes, data padded to 4 bytes."""
    out = bytearray()
    for ino, (name, data) in enumerate(entries, start=1):
        raw = name.encode("utf-8")
        payload = b"" if data is None else data
        mode = S_IFDIR | 0o755 if data is None else S_IFREG | 0o644
        header = newc_header(raw, len(payload), mode, ino)
        out += header + raw + NUL
        out += pad4(len(header) + len(raw) + 1)
        out += payload
        out += pad4(len(payload))

    trailer = b"TRAILER!!!"
    header = newc_header(trailer, 0, 0, 0)
    out += header + trailer + NUL
    out += pad4(len(header) + len(trailer) + 1)
    return bytes(out)


def build_dvzip(payload: bytes, block_size: int = 4096, stored: bool = False,
                set_stored_bit: bool = True) -> bytes:
    """
    Frames payload into DVZip blocks.

    stored=True writes the blocks uncompressed, and bit 31 of the length word then marks
    them -- the encoding a real iPhone transfer was observed to use. set_stored_bit=False
    produces the older shape, where a stored block is only recognisable by the absence of
    a zlib header, so the decoder's fallback path stays covered.
    """
    out = bytearray()
    for start in range(0, len(payload), block_size):
        chunk = payload[start:start + block_size]
        if stored:
            word = len(chunk) | (0x80000000 if set_stored_bit else 0)
            out += struct.pack(">I", word) + chunk
        else:
            block = zlib.compress(chunk, 6)
            out += struct.pack(">I", len(block)) + block
    return bytes(out)


def write(path: str, data: bytes, note: str) -> None:
    with open(path, "wb") as handle:
        handle.write(data)
    print(f"  {os.path.basename(path):<34} {len(data):>8} bytes   {note}")


def main() -> int:
    out_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join("tests", "fixtures")
    os.makedirs(out_dir, exist_ok=True)
    print(f"Writing fixtures to {out_dir}")

    # --- odc, the shape opendrop sends -------------------------------------------
    single = build_odc([("hello.txt", b"Hallo Welt\n")])
    write(os.path.join(out_dir, "single-file.cpio"), single, "odc, one small file")
    write(os.path.join(out_dir, "single-file.dvzip"),
          build_dvzip(single), "same, zlib blocks")
    write(os.path.join(out_dir, "single-file-stored.dvzip"),
          build_dvzip(single, stored=True), "STORED blocks, bit 31 set")
    write(os.path.join(out_dir, "single-file-stored-noflag.dvzip"),
          build_dvzip(single, stored=True, set_stored_bit=False),
          "STORED without bit 31 (fallback path)")
    write(os.path.join(out_dir, "single-file.cpio.gz"),
          gzip.compress(single), "gzipped, unsignalled")

    # --- newc, the shape Apple sends ---------------------------------------------
    single_newc = build_newc([("hello.txt", b"Hallo Welt\n")])
    write(os.path.join(out_dir, "single-file-newc.cpio"), single_newc, "newc, one file")
    write(os.path.join(out_dir, "single-file-newc.dvzip"),
          build_dvzip(single_newc), "newc inside DVZip")

    # The "." archive-root member iOS puts at the head of its archives.
    rooted = build_newc([
        (".", None),
        ("IMG_0001.HEIC", bytes(range(256)) * 4),
    ])
    write(os.path.join(out_dir, "archive-root.cpio"), rooted, "newc with leading '.' member")
    write(os.path.join(out_dir, "archive-root.dvzip"),
          build_dvzip(rooted), "same, DVZip framed")

    # --- several files, spanning multiple DVZip blocks ----------------------------
    big = bytes((i * 7 + 13) % 251 for i in range(50_000))
    members = [
        ("notes.txt", "Umlaute: aeoeue -> \u00e4\u00f6\u00fc\n".encode("utf-8")),
        ("photos", None),
        ("photos/img1.bin", big),
        ("empty.txt", b""),
    ]
    multi = build_odc(members)
    write(os.path.join(out_dir, "multi-file.cpio"), multi, "4 entries incl. empty + 50 KB")
    write(os.path.join(out_dir, "multi-file.dvzip"),
          build_dvzip(multi, block_size=4096), "same, many blocks")
    write(os.path.join(out_dir, "multi-file-newc.dvzip"),
          build_dvzip(build_newc(members), block_size=4096), "same members as newc")

    # --- hostile names -------------------------------------------------------------
    hostile_members = [
        ("../../escape.txt", b"nope\n"),
        ("/absolute.txt", b"nope\n"),
        ("C:\\drive.txt", b"nope\n"),
        ("CON", b"nope\n"),
        ("stream.txt:hidden", b"nope\n"),
        ("trailing.   ", b"nope\n"),
        ("ok.txt", b"fine\n"),
    ]
    hostile = build_odc(hostile_members)
    write(os.path.join(out_dir, "hostile-names.cpio"), hostile, "path-traversal test set")
    write(os.path.join(out_dir, "hostile-names.dvzip"),
          build_dvzip(hostile), "same, zlib blocks")
    write(os.path.join(out_dir, "hostile-names-newc.dvzip"),
          build_dvzip(build_newc(hostile_members)), "same set as newc")

    # --- deliberately broken inputs -------------------------------------------------
    write(os.path.join(out_dir, "corrupt-truncated.dvzip"),
          build_dvzip(single)[:-20], "block payload cut short")
    write(os.path.join(out_dir, "corrupt-badzlib.dvzip"),
          struct.pack(">I", 10) + b"\x78\x9c" + NUL * 8,
          "zlib header, garbage body")
    write(os.path.join(out_dir, "corrupt-hugeblock.dvzip"),
          struct.pack(">I", 0x7FFFFFFF) + b"\x78\x9c",
          "length beyond the limit")
    write(os.path.join(out_dir, "corrupt-badmagic.cpio"),
          b"079999" + b"0" * 70 + b"x" + NUL, "wrong CPIO magic")

    print("done")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
