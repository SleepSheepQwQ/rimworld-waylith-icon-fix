#!/usr/bin/env python3
"""Strict DDS/PNG gate over control and patched [MODCHECK] output."""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path


def parse(path: Path):
    text = path.read_text(encoding="utf-8", errors="replace")
    rows = {}
    pattern = re.compile(
        r"dds leaf=(\S+) hit=(\w+) name='([^']*)' size=(\S+) fmt=(\S+)' (.*)")
    for match in pattern.finditer(text):
        leaf, hit, name, size, fmt, stats = match.groups()
        values = {}
        rgb = re.search(r"meanRGB=\(([^)]*)\)", stats)
        if rgb:
            values["meanRGB"] = tuple(float(part) for part in rgb.group(1).split(","))
        for key in ("meanA", "alpha0", "lumSd", "mips", "mip1lum"):
            found = re.search(key + r"=\(?([-\d.]+)", stats)
            if found:
                values[key] = float(found.group(1))
        rows[leaf] = {
            "hit": hit == "True",
            "name": name,
            "size": size,
            "fmt": fmt,
            "stats": stats,
            **values,
        }
    return text, rows


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("control", type=Path)
    parser.add_argument("patched", type=Path)
    parser.add_argument("--control-exit", type=int, required=True)
    parser.add_argument("--patched-exit", type=int, required=True)
    parser.add_argument("--summary", type=Path)
    args = parser.parse_args()

    checks = []

    def need(ok: bool, message: str):
        checks.append((bool(ok), message))

    try:
        a_text, a = parse(args.control)
        b_text, b = parse(args.patched)
        need(args.control_exit == 0, f"control host exit 0 (got {args.control_exit})")
        need(args.patched_exit == 0, f"patched host exit 0 (got {args.patched_exit})")
        need("BUILD 2.7.8" in b_text, "patched run loaded the current 2.7.8 DLL")

        need(a.get("Dxt3", {}).get("hit") is False, "control DXT3 reproduces the loader miss")
        d3 = b.get("Dxt3", {})
        need(d3.get("hit") is True and d3.get("size") == "8x8",
             f"patched DXT3 loads 8x8 (got {d3})")
        need(d3.get("fmt") == "RGBA32", f"patched DXT3 is RGBA32 (got {d3.get('fmt')})")
        need((d3.get("meanA") or 0) > 200, f"patched DXT3 alpha is visible (got {d3.get('meanA')})")
        need(max(d3.get("meanRGB") or (0, 0, 0)) > 5,
             f"patched DXT3 RGB is visible (got {d3.get('meanRGB')})")
        need(d3.get("meanRGB") == (0.0, 255.0, 0.0),
             f"patched DXT3 preserves the expected opaque green fixture (got {d3.get('meanRGB')})")
        need((d3.get("mip1lum") or 0) > 100,
             f"patched DXT3 mip chain retains visible green (got {d3.get('mip1lum')})")

        sh = b.get("Shadow", {})
        pg = b.get("PngOnly", {})
        need(a.get("Shadow", {}).get("hit") is False, "control bad DDS shadows the PNG")
        need(sh.get("hit") is True and sh.get("size") == "128x128",
             f"patched Shadow resolves the 128x128 fallback PNG (got {sh})")
        need(pg.get("hit") is True and pg.get("size") == "128x128",
             f"PngOnly reference is 128x128 (got {pg})")
        for field in ("meanRGB", "meanA", "alpha0", "lumSd"):
            need(sh.get(field) == pg.get(field),
                 f"Shadow {field} matches PngOnly (fallback {sh.get(field)} vs {pg.get(field)})")

        bc7 = b.get("Bc7", {})
        need(bc7.get("hit") is True and bc7.get("size") == "128x128",
             "BC7 remains a readable 128x128 texture")

        bc5a, bc5b = a.get("Bc5", {}), b.get("Bc5", {})
        need("px!" in bc5a.get("stats", "") or bc5a.get("fmt") == "BC7",
             "control BC4-behind-DX10 is mislabelled or unreadable")
        need(bc5b.get("hit") is True and bc5b.get("size") == "128x128",
             "patched BC4-behind-DX10 loads 128x128")
        need("px!" not in bc5b.get("stats", "") and bc5b.get("fmt") != "BC7",
             "patched BC4-behind-DX10 is readable and not labelled BC7")
        need((bc5b.get("lumSd") or 0) > 2, "patched BC4 pixels are non-flat")

        d1 = b.get("Dxt1Alpha", {})
        need(d1.get("alpha0") == 16, f"DXT1 punch-through keeps 16/64 transparent texels (got {d1.get('alpha0')})")
        need(d1.get("fmt") not in ("DXT1", "DXT1Crunched"),
             f"DXT1 alpha is not uploaded as opaque BC1_RGB (got {d1.get('fmt')})")

        m1 = b.get("Mip1", {})
        need(m1.get("hit") is True and (m1.get("mips") or 0) >= 2,
             f"single-mip source gains a full chain (got {m1})")
        need((m1.get("mip1lum") or 0) > 1,
             f"mip level 1 contains visible pixels (got {m1.get('mip1lum')})")
    except Exception as exc:
        need(False, f"gate error: {type(exc).__name__}: {exc}")

    failed = 0
    for ok, message in checks:
        print(("PASS " if ok else "FAIL ") + message)
        if not ok:
            failed += 1
    print(f"SUMMARY checks={len(checks)} failed={failed}")

    if args.summary:
        args.summary.parent.mkdir(parents=True, exist_ok=True)
        args.summary.write_text(
            "# Strict DDS verification\n\n" +
            "\n".join(f"- {'PASS' if ok else 'FAIL'} — {message}" for ok, message in checks) +
            "\n", encoding="utf-8")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
