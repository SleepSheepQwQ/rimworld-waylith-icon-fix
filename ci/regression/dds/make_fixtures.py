#!/usr/bin/env python3
"""Create strict DDS fixtures for the real Waylith content path.

Unlike the older ddsfix gate, Dxt3 carries visible opaque pixels and Shadow is an
unknown-format DDS with a byte-identical PNG fallback. A repaired-but-transparent
DDS or a 2x2 BadTex therefore cannot satisfy the strict gate.
"""

from __future__ import annotations

import argparse
import shutil
import struct
import sys
import zlib
from pathlib import Path


ABOUT = """<?xml version="1.0" encoding="utf-8"?>
<ModMetaData>
  <name>DdsProbe</name>
  <author>ci</author>
  <packageId>waylith.ddsprobe</packageId>
  <supportedVersions><li>1.7</li></supportedVersions>
</ModMetaData>
"""


def png(width: int, height: int, pixel) -> bytes:
    raw = bytearray()
    for y in range(height):
        raw.append(0)
        for x in range(width):
            raw += bytes(pixel(x, y))

    def chunk(tag: bytes, data: bytes) -> bytes:
        return (struct.pack(">I", len(data)) + tag + data +
                struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) +
            chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))


def ramp(x: int, y: int):
    return (x * 2 & 0xFF, y * 2 & 0xFF, (x * 7 + y * 3) & 0xFF, 255)


def dds(fourcc: str, width: int, height: int, payload: bytes) -> bytes:
    header = bytearray(128)
    struct.pack_into("<I", header, 0, 0x20534444)
    struct.pack_into("<I", header, 4, 124)
    struct.pack_into("<I", header, 8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000)
    struct.pack_into("<I", header, 12, height)
    struct.pack_into("<I", header, 16, width)
    struct.pack_into("<I", header, 20, len(payload))
    struct.pack_into("<I", header, 28, 1)
    struct.pack_into("<I", header, 76, 32)
    struct.pack_into("<I", header, 80, 0x4)
    header[84:88] = fourcc.encode("ascii")
    struct.pack_into("<I", header, 108, 0x1000)
    return bytes(header) + payload


def visible_dxt3_8x8() -> bytes:
    alpha = b"\xff" * 8
    colour = struct.pack("<HHI", 0x07E0, 0x001F, 0)
    return (alpha + colour) * 4


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--run", type=Path, required=True)
    parser.add_argument("--host", type=Path, required=True)
    args = parser.parse_args()

    mod = args.run / "Mods" / "DdsProbe"
    if mod.exists():
        shutil.rmtree(mod)
    about = mod / "About"
    textures = mod / "Textures" / "Things" / "DdsProbe"
    about.mkdir(parents=True)
    textures.mkdir(parents=True)
    (about / "About.xml").write_text(ABOUT, encoding="utf-8")

    source = args.host / "ddsfix"
    for source_name, target_name in (
        ("bc7.dds", "Bc7.dds"),
        ("bc5.dds", "Bc5.dds"),
        ("dxt1_alpha.dds", "Dxt1Alpha.dds"),
        ("mip1.dds", "Mip1.dds"),
    ):
        shutil.copy2(source / source_name, textures / target_name)

    # Visible DXT3: 8x8 opaque green, not the old all-transparent fixture.
    (textures / "Dxt3.dds").write_bytes(dds("DXT3", 8, 8, visible_dxt3_8x8()))

    # Unknown FourCC is undecodable to both the shipped loader and GlyphFix. The same-named
    # PNG must be recovered and must match PngOnly byte-for-byte.
    image = png(128, 128, ramp)
    (textures / "Shadow.png").write_bytes(image)
    (textures / "PngOnly.png").write_bytes(image)
    (textures / "Shadow.dds").write_bytes(dds("ZZZZ", 8, 8, b"\0" * 64))

    files = sorted(p.relative_to(mod).as_posix() for p in mod.rglob("*") if p.is_file())
    for relative in files:
        print(f"ddsstrict file={relative} bytes={(mod / relative).stat().st_size}")
    if len(files) != 9:
        print(f"expected 9 files, wrote {len(files)}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
