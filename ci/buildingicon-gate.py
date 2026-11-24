#!/usr/bin/env python3
"""Gate the real-host Architectural-icon probe.

Inputs are A/B event streams:
  control: no GlyphFix. It must expose the CutoutComplexUI material state.
  patched: this checkout. It must install the draw fixes and remove that material.

JSONL is intentional: every observation comes from the shipped host, and a missing observation is
an error rather than a default.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


TARGETS = ("RoyalBed", "NutrientPasteDispenser")
CONTROL = "PowerConduit"


def load(path: Path) -> list[dict]:
    rows: list[dict] = []
    if not path.is_file():
        return rows
    for lineno, raw in enumerate(path.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
        if not raw.strip():
            continue
        try:
            rows.append(json.loads(raw))
        except json.JSONDecodeError as exc:
            raise SystemExit(f"{path}:{lineno}: invalid JSON: {exc}") from exc
    return rows


def one(rows: list[dict], kind: str) -> dict:
    found = [r for r in rows if r.get("type") == kind]
    if len(found) != 1:
        raise AssertionError(f"expected exactly one {kind!r} event, got {len(found)}")
    return found[0]


def defs(rows: list[dict]) -> dict[str, dict]:
    out: dict[str, dict] = {}
    for row in rows:
        if row.get("type") == "def":
            out[row.get("def", "")] = row
    return out


def finish(rows: list[dict]) -> dict:
    found = [
        r for r in rows
        if r.get("type") == "probe" and r.get("kind") == "result"
    ]
    if len(found) != 1:
        raise AssertionError(f"expected one result event, got {len(found)}")
    return found[0]


def require(condition: bool, message: str, checks: list[dict]) -> None:
    checks.append({"ok": bool(condition), "message": message})


def gate_common(label: str, rows: list[dict], checks: list[dict],
                expect_glyphfix: bool, exit_code: int | None) -> tuple[dict, dict, dict]:
    require(bool(rows), f"{label}: probe emitted events", checks)
    require(exit_code == 0, f"{label}: host exit code is 0 (got {exit_code})", checks)
    result = finish(rows)
    require(result.get("result") == "done", f"{label}: probe completed", checks)
    starts = [r for r in rows if r.get("type") == "probe" and r.get("kind") == "start"]
    require(len(starts) == 1, f"{label}: exactly one start event", checks)
    expected_mode = "patched" if expect_glyphfix else "control"
    require(bool(starts) and starts[0].get("mode") == expected_mode,
            f"{label}: probe mode is {expected_mode}", checks)
    booted_rows = [r for r in rows if r.get("type") == "probe" and r.get("kind") == "booted"]
    require(len(booted_rows) == 1, f"{label}: DefDatabase boot observation present", checks)
    assembly = one(rows, "assembly")
    startup = one(rows, "startup")
    require(bool(assembly.get("present")) is expect_glyphfix,
            f"{label}: GlyphFix assembly {'is' if expect_glyphfix else 'is not'} loaded", checks)
    require(bool(startup.get("invoked")) is expect_glyphfix,
            f"{label}: GlyphFix startup {'was' if expect_glyphfix else 'was not'} invoked", checks)
    harmony = one(rows, "harmony")
    require(harmony.get("workerPatched") == "true", f"{label}: ThingIconWorker observation hook installed", checks)
    methods = harmony.get("methods") or []
    has_thing = any(m.get("glyphfixThingIcon") for m in methods)
    has_get = any(m.get("glyphfixGetIconFor") for m in methods)
    require(bool(has_thing) == expect_glyphfix,
            f"{label}: ThingDef ThingIcon GlyphFix prefix {'is' if expect_glyphfix else 'is not'} attached", checks)
    require(bool(has_get) == expect_glyphfix,
            f"{label}: ThingDef GetIconFor GlyphFix prefix {'is' if expect_glyphfix else 'is not'} attached", checks)
    return defs(rows), booted_rows[0] if booted_rows else {}, harmony


def read_exit(path: Path, explicit: int | None) -> int | None:
    if explicit is not None:
        return explicit
    sidecar = path.parent / "host_exit.txt"
    try:
        return int(sidecar.read_text(encoding="utf-8").strip())
    except (FileNotFoundError, ValueError):
        return None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("control", type=Path)
    ap.add_argument("patched", type=Path)
    ap.add_argument("--control-exit", type=int)
    ap.add_argument("--patched-exit", type=int)
    ap.add_argument("--summary", type=Path)
    args = ap.parse_args()

    checks: list[dict] = []
    try:
        control_rows = load(args.control)
        patched_rows = load(args.patched)

        control_defs, _, _ = gate_common(
            "control", control_rows, checks, expect_glyphfix=False,
            exit_code=read_exit(args.control, args.control_exit),
        )
        patched_defs, _, _ = gate_common(
            "patched", patched_rows, checks, expect_glyphfix=True,
            exit_code=read_exit(args.patched, args.patched_exit),
        )

        for name in TARGETS:
            c = control_defs.get(name, {})
            p = patched_defs.get(name, {})
            require(bool(c.get("found")), f"control: {name} exists", checks)
            require(bool(p.get("found")), f"patched: {name} exists", checks)
            require(c.get("expectComplex") is True, f"control: {name} marked complex fixture", checks)
            require((c.get("material", {}).get("before", {}) or {}).get("complexUi") is True,
                    f"control: {name} reproduces CutoutComplexUI before GlyphFix", checks)

            before = p.get("material", {}).get("before", {}) or {}
            after_direct_icon = p.get("uiIcon", {}).get("afterDirect", {}) or {}
            after_draw_icon = p.get("uiIcon", {}).get("afterThingIcon", {}) or {}
            after_direct_mat = p.get("material", {}).get("afterDirect", {}) or {}
            after_draw_mat = p.get("material", {}).get("afterThingIcon", {}) or {}
            direct = p.get("getIconFor", {}) or {}
            worker = p.get("worker", {}) or {}
            require(before.get("complexUi") is True,
                    f"patched: {name} has the CutoutComplexUI regression state before the draw hook", checks)
            for route, icon in (("GetIconFor", after_direct_icon), ("ThingIcon", after_draw_icon)):
                require(icon.get("badTex") is False and icon.get("null") is False,
                        f"patched: {name} uiIcon is neither null nor BadTex after {route}", checks)
                require(icon.get("readable") is True,
                        f"patched: {name} uiIcon is readable after {route}", checks)
                require(float(icon.get("coverage") or 0) > 0.05,
                        f"patched: {name} uiIcon has non-trivial pixels after {route}", checks)
            require(after_direct_mat.get("complexUi") is False,
                    f"patched: {name} uiIconMaterial is cleared after GetIconFor", checks)
            require(after_draw_mat.get("complexUi") is False,
                    f"patched: {name} uiIconMaterial is cleared after ThingIcon", checks)
            require(direct.get("badTex") is False and direct.get("complexUi") is False and
                    not direct.get("error"),
                    f"patched: {name} GetIconFor(ThingDef) returns no CutoutComplexUI material", checks)
            require(worker.get("calls") == 1,
                    f"patched: {name} reaches ThingIconWorker exactly once", checks)
            require(worker.get("def") == name,
                    f"patched: {name} worker call belongs to the target def", checks)
            require(worker.get("complexUi") is False and not worker.get("error"),
                    f"patched: {name} worker receives the plain-texture path", checks)
            require(bool(worker.get("tex")) and worker.get("tex") != "<null>",
                    f"patched: {name} worker receives a texture", checks)
            require(worker.get("tex") == after_draw_icon.get("tex"),
                    f"patched: {name} worker texture matches the repaired uiIcon", checks)

        c = patched_defs.get(CONTROL, {})
        require(bool(c.get("found")), f"patched: control def {CONTROL} exists", checks)
        require(c.get("expectComplex") is False, f"patched: {CONTROL} is a non-complex control", checks)
        c_before = (c.get("material", {}).get("injected")
                    or c.get("material", {}).get("before", {}) or {})
        c_direct = c.get("material", {}).get("afterDirect", {}) or {}
        c_after = c.get("material", {}).get("afterThingIcon", {}) or {}
        c_worker = c.get("worker", {}) or {}
        require(c.get("controlMaterialInjected") is True and bool(c_before.get("name")),
                f"patched: {CONTROL} got a real non-UI material for the control", checks)
        require(c_before.get("complexUi") is False and
                c_direct.get("name") == c_before.get("name") and
                c_after.get("name") == c_before.get("name"),
                f"patched: {CONTROL} non-UI material was preserved through both routes", checks)
        require(c_worker.get("calls") == 1 and c_worker.get("def") == CONTROL,
                f"patched: {CONTROL} still reaches the worker with a texture", checks)
        require(bool(c_worker.get("tex")) and not c_worker.get("error"),
                f"patched: {CONTROL} worker receives a texture without an error", checks)
        require(c_worker.get("material") == c_before.get("name"),
                f"patched: {CONTROL} worker receives the preserved material", checks)

    except Exception as exc:
        checks.append({"ok": False, "message": f"gate error: {exc}"})

    failed = [c for c in checks if not c["ok"]]
    for check in checks:
        print(("PASS " if check["ok"] else "FAIL ") + check["message"])
    print(f"SUMMARY checks={len(checks)} failed={len(failed)}")

    if args.summary:
        args.summary.parent.mkdir(parents=True, exist_ok=True)
        lines = ["# Building icon real-host gate", ""]
        lines += [f"- {'PASS' if c['ok'] else 'FAIL'} — {c['message']}" for c in checks]
        args.summary.write_text("\n".join(lines) + "\n", encoding="utf-8")

    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
