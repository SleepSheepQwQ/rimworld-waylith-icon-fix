#!/usr/bin/env python3
"""Gate the current GlyphFix animal-icon A/B run on the real Waylith host."""

from __future__ import annotations

import argparse
import json
from pathlib import Path


TARGETS = ("Ibex", "Boomalope")
CONTROLS = ("Squirrel", "Cow")


def load(path: Path):
    rows = []
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        if line.strip():
            rows.append(json.loads(line))
    return rows


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("events", type=Path)
    parser.add_argument("--host-exit", type=int, required=True)
    parser.add_argument("--summary", type=Path)
    args = parser.parse_args()

    checks = []
    def need(ok: bool, msg: str):
        checks.append((bool(ok), msg))

    try:
        rows = load(args.events)
        need(args.host_exit == 0, f"host exit is 0 (got {args.host_exit})")
        result = [r for r in rows if r.get("type") == "result"]
        need(len(result) == 1 and result[0].get("result") == "done", "probe completed")
        mod = [r for r in rows if r.get("type") == "mod"]
        need(len(mod) == 1, "exactly one mod record")
        if mod:
            need(mod[0].get("assembly") is True, "GlyphFix assembly loaded")
            need(mod[0].get("bootstrapInvoked") is True, "GlyphFix Bootstrap invoked")
            need(not mod[0].get("bootstrapError"), "GlyphFix Bootstrap has no error")
        worker_patch = [r for r in rows if r.get("type") == "worker_patch"]
        need(len(worker_patch) == 1 and worker_patch[0].get("ok") is True,
             "ThingIconWorker observation hook installed")
        animals = {r.get("species"): r for r in rows if r.get("type") == "animal"}

        for species in TARGETS:
            row = animals.get(species, {})
            need(row.get("defFound") is True, f"{species} def found")
            need(not row.get("error"), f"{species} probe has no error")
            need(row.get("badTex") is False, f"{species} icon is not BadTex")
            need(row.get("iconSize") == "128x128", f"{species} icon is 128x128")
            need(row.get("iconComplexUi") is False,
                 f"{species} GetIconFor material is not CutoutComplexUI")
            need(row.get("workerCalls") == 1 and row.get("workerDef") == species,
                 f"{species} reaches ThingIconWorker for its own def")
            need(row.get("workerTexture") == row.get("iconTexture"),
                 f"{species} worker texture matches GetIconFor")
            need(row.get("workerComplexUi") is False,
                 f"{species} worker receives the plain-texture path")
            need(not row.get("drawError"), f"{species} ThingIcon draw has no error")

        for species in CONTROLS:
            row = animals.get(species, {})
            need(row.get("defFound") is True, f"{species} control def found")
            need(not row.get("error"), f"{species} control probe has no error")
            need(row.get("badTex") is False, f"{species} control icon is not BadTex")
            need(row.get("iconSize") == "128x128", f"{species} control icon is 128x128")
            need(row.get("workerCalls") == 1 and row.get("workerDef") == species,
                 f"{species} control reaches ThingIconWorker")
            need(not row.get("drawError"), f"{species} control draw has no error")
    except Exception as e:
        need(False, f"gate error: {type(e).__name__}: {e}")

    failed = 0
    for ok, msg in checks:
        print(("PASS " if ok else "FAIL ") + msg)
        if not ok:
            failed += 1
    print(f"SUMMARY checks={len(checks)} failed={failed}")

    if args.summary:
        args.summary.parent.mkdir(parents=True, exist_ok=True)
        args.summary.write_text(
            "# Animal icon verification\n\n" +
            "\n".join(f"- {'PASS' if ok else 'FAIL'} — {msg}" for ok, msg in checks) + "\n",
            encoding="utf-8",
        )
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
