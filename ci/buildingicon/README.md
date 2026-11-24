# Real-host building icon gate

This probe runs inside the shipped Waylith managed process after `PlayDataLoader` completes. It does
not start a map: Architect icons are a `ThingDef` path, so booting the real DefDatabase is sufficient
and much cheaper than an Android emulator.

For each of `RoyalBed` and `NutrientPasteDispenser` it records:

- `ThingDef.uiIcon` and `uiIconMaterial` before the draw;
- the result and `out Material` of `Widgets.GetIconFor(ThingDef, ...)`;
- the same fields after GlyphFix's prefix runs;
- the texture and material that actually reach `Widgets.ThingIconWorker`.

`PowerConduit` is the non-`CutoutComplexUI` control.

The workflow runs two boots with the same probe:

1. **control**: no GlyphFix. The two target defs must expose `CutoutComplexUI`.
2. **patched**: the current checkout. Both defs must end with a readable `uiIcon`, no
   `CutoutComplexUI` material, and the worker must receive the plain texture. The control def must be
   unchanged.

Each def is measured through two independent first-call routes. The probe restores the original
`uiIcon`/`uiIconMaterial` and clears GlyphFix's decision cache between them, so a successful
`GetIconFor` cannot hide a broken first `ThingIcon` call. Host exit codes are read from
`host_exit.txt`; a complete JSON stream with a crashed process still fails the gate.

`make_assets.py` generates synthetic RoyalBed, NutrientPasteDispenser and PowerConduit PNGs under the
host's texture root. This keeps the gate independent of game-art redistribution while still exercising
the real `Graphic_Multi`, `CutoutComplexUI` and normal-material paths.

Build locally when a Waylith host checkout is available:

```bash
dotnet build ci/buildingicon/GlyphFixBuildingIcon.csproj -c Release \
  -p:WaylithHost=/path/to/rimworld-waylith-re
python3 -m py_compile ci/buildingicon-gate.py
```

The GitHub workflow supplies the host closure separately; no game DLL, Def, or texture is copied into
the GlyphFix artifact.
