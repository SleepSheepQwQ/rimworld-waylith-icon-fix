#!/usr/bin/env python3
"""Gate the real-host disabled-command grayscale icon A/B probe."""

from __future__ import annotations

import argparse
import json
from pathlib import Path


def load(path: Path):
    rows = []
    if path.is_file():
        for line_number, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            if raw.strip():
                try:
                    rows.append(json.loads(raw))
                except json.JSONDecodeError as exc:
                    raise SystemExit(f"{path}:{line_number}: {exc}") from exc
    return rows


def one(rows, kind):
    matches = [row for row in rows if row.get("type") == kind]
    if len(matches) != 1:
        raise AssertionError(f"expected exactly one {kind} event, got {len(matches)}")
    return matches[0]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("control", type=Path)
    parser.add_argument("patched", type=Path)
    parser.add_argument("--control-exit", type=int)
    parser.add_argument("--patched-exit", type=int)
    args = parser.parse_args()

    checks = []

    def require(condition, message):
        checks.append((bool(condition), message))

    try:
        control = load(args.control)
        patched = load(args.patched)
        require(args.control_exit == 0, f"control host exit=0 (got {args.control_exit})")
        require(args.patched_exit == 0, f"patched host exit=0 (got {args.patched_exit})")

        for label, rows in (("control", control), ("patched", patched)):
            result = one(rows, "result")
            require(result.get("result") == "done", f"{label}: probe completed")
            require(one(rows, "hooks").get("ok") is True, f"{label}: observation hooks installed")

        control_mod = one(control, "mod")
        patched_mod = one(patched, "mod")
        require(control_mod.get("assembly") is False, "control: GlyphFix was not loaded")
        require(control_mod.get("bootstrapInvoked") is False, "control: Bootstrap was not invoked")
        require(patched_mod.get("assembly") is True, "patched: GlyphFix assembly loaded")
        require(patched_mod.get("bootstrapInvoked") is True, "patched: Bootstrap invoked")

        control_gray = one(control, "grayscale")
        patched_gray = one(patched, "grayscale")
        require(control_gray.get("grayAfterMod") is True,
                "control: disabled command still requests grayscale=true")
        require(one(patched, "harmony").get("uvFixAttached") is True,
                "patched: UvFix prefix attached to ThingIcon")
        require(patched_gray.get("grayAfterMod") is False,
                "patched: UvFix flips grayscale=true to false before the original")
        require(patched_gray.get("workerCalls", 0) >= 1,
                "patched: ThingIcon reaches ThingIconWorker")
        require(patched_gray.get("workerDef") == "Gun_Revolver_Unique",
                "patched: worker observes the target def")
        require(bool(patched_gray.get("workerTexture")) and
                patched_gray.get("workerTexture") != "BadTex",
                "patched: worker receives a real icon texture")
        require(patched_gray.get("workerMaterial") == "",
                "patched: worker uses the plain-texture path")
    except Exception as exc:
        checks.append((False, f"gate error: {exc}"))

    for ok, message in checks:
        print(("PASS " if ok else "FAIL ") + message)
    failed = sum(not ok for ok, _ in checks)
    print(f"SUMMARY checks={len(checks)} failed={failed}")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
