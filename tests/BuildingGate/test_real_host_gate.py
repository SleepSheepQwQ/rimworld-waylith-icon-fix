#!/usr/bin/env python3
"""Self-tests for ci/buildingicon-gate.py.

These do not boot the game. They prove that the parser and every required branch return the right
process status, so a malformed/missing real-host log cannot be mistaken for a green gate.
"""

from __future__ import annotations

import copy
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
GATE = ROOT / "ci" / "buildingicon-gate.py"


def event(kind: str, **values):
    return {"type": "probe", "kind": kind, **values}


def common(mode: str, glyphfix: bool):
    return [
        event("start", mode=mode),
        event("booted", thingDefs=2000),
        {"type": "assembly", "present": glyphfix, "name": "GlyphFix" if glyphfix else None,
         "version": "2.7.8" if glyphfix else None},
        {"type": "startup", "assembly": glyphfix, "invoked": glyphfix, "error": None},
        {"type": "harmony", "workerPatched": "true", "methods": [{
            "glyphfixThingIcon": glyphfix,
            "glyphfixGetIconFor": glyphfix,
        }]},
    ]


def target(name: str):
    return {
        "type": "def",
        "def": name,
        "found": True,
        "expectComplex": True,
        "material": {
            "before": {"complexUi": True, "name": "CutoutComplexUI_" + name},
            "afterDirect": {"complexUi": False, "name": ""},
            "afterThingIcon": {"complexUi": False, "name": ""},
        },
        "uiIcon": {
            "afterDirect": {"tex": name + "_tex", "null": False, "badTex": False,
                            "readable": True, "coverage": 0.9},
            "afterThingIcon": {"tex": name + "_tex", "null": False, "badTex": False,
                               "readable": True, "coverage": 0.9},
        },
        "getIconFor": {"tex": name + "_tex", "badTex": False, "complexUi": False, "error": None},
        "worker": {"calls": 1, "def": name, "tex": name + "_tex", "material": "",
                   "complexUi": False, "error": None},
    }


def control_def():
    material = {"name": "Cutout", "complexUi": False}
    return {
        "type": "def",
        "def": "PowerConduit",
        "found": True,
        "expectComplex": False,
        "controlMaterialInjected": True,
        "material": {
            "injected": material,
            "afterDirect": copy.deepcopy(material),
            "afterThingIcon": copy.deepcopy(material),
        },
        "worker": {"calls": 1, "def": "PowerConduit", "tex": "PowerConduit_tex",
                   "material": "Cutout", "complexUi": False, "error": None},
    }


def streams():
    control = common("control", glyphfix=False)
    control += [target("RoyalBed"), target("NutrientPasteDispenser")]
    control.append(event("result", result="done", mode="control"))

    patched = common("patched", glyphfix=True)
    patched += [target("RoyalBed"), target("NutrientPasteDispenser"), control_def()]
    patched.append(event("result", result="done", mode="patched"))
    return control, patched


def write_jsonl(path: Path, rows) -> None:
    with path.open("w", encoding="utf-8") as handle:
        for row in rows:
            handle.write(json.dumps(row) + "\n")


class GateTests(unittest.TestCase):
    def run_gate(self, control_rows, patched_rows, control_exit=0, patched_exit=0):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            control = root / "control" / "events.jsonl"
            patched = root / "patched" / "events.jsonl"
            control.parent.mkdir()
            patched.parent.mkdir()
            write_jsonl(control, control_rows)
            write_jsonl(patched, patched_rows)
            return subprocess.run(
                [
                    sys.executable, str(GATE), str(control), str(patched),
                    "--control-exit", str(control_exit),
                    "--patched-exit", str(patched_exit),
                ],
                text=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                check=False,
            )

    def test_complete_ab_stream_passes(self):
        control, patched = streams()
        result = self.run_gate(control, patched)
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn("failed=0", result.stdout)

    def test_wrong_worker_def_fails(self):
        control, patched = streams()
        patched[5]["worker"]["def"] = "SomethingElse"
        result = self.run_gate(control, patched)
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn("worker call belongs to the target def", result.stdout)

    def test_nonzero_host_exit_fails(self):
        control, patched = streams()
        result = self.run_gate(control, patched, patched_exit=7)
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn("host exit code is 0", result.stdout)


if __name__ == "__main__":
    unittest.main()
