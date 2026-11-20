
## 2026-11-20 — runtime verification of the mask-variant fix (all in CI, no device)

### 1. The extension-leak layout reproduces the reported symptom exactly

`run 44665671369` (private repo, real host, real map, `Widgets.GetIconFor`). A generated mod ships
`UniqueRevolverA..E.png.png` / `A..E_m.png.png`, so the asset name keeps `.png`:

| | value |
|---|---|
| variants | **10** (5 art + 5 masks) |
| ids 0,2,4,6,8 | `UniqueRevolver*_m.png` — coverage **100 %**, meanRGB ≈ (6,0,0), edge structure 0.24–0.31 |
| ids 1,3,5,7,9 | `UniqueRevolver*.png` — coverage 12.9–15.0 %, structure 11.4–12.1 |

A mask painted as art is a nearly flat dark tile: that is the "blank" a player reports. Which items
are hit is decided by `thingIDNumber % subGraphics.Length`, so **half the items, deterministically**.

### 2. The coloured-clone path is not involved

`run 44665329801`: forcing a non-white `DrawColor` on the same unique weapon (the real
`CompUniqueWeapon` path) keeps `subGraphics = 5`, the same variant index and the same texture; the
white and tinted icons are byte-identical (`SHA256 1729e91a…`). Tint lives in `material._Color`, never
in pixels.

### 3. GlyphFix's collection filter is now proven inside the engine

`run 44669186310` — `modcheck` workflow: same host, same leak mod, one run without the mod and one
with the released `GlyphFix.zip`:

```
control (no mod): fresh variants=10 masks=5
with GlyphFix  : fresh variants=5  masks=0     names A..E, no _m
```

The gate reads `Graphic_Collection.subGraphics` from the object the engine built, so a no-op patch
would show `10/5` in both runs and fail the job.

### 4. Two harness traps worth remembering (they cost two dead ends here)

* The headless harness never runs `[StaticConstructorOnStartup]`, so a Harmony mod stays inert. The
  phone does run it (`grayfix: patched …` appears in its log). Emulate the ctor by reflection — and
  then measure a **freshly built** `Graphic`, because every `ThingDef.graphicData.Graphic` was built
  during content load, before the emulated ctor ran.
* A hand-built subset of the **Core** pack cannot be exercised: the core pack mounts at `DataPath`,
  so `Resources/Textures/...` is requested as `<DataPath>/Resources/Textures/...`, while the harness
  gives Core the content root `run/Data/Core` (no `Textures/`). `ContentFinder` then returns 0 —
  a harness artefact, not a defect. DLC packs (`Data/<Dlc>`, entries `Textures/...`) mount fine.

### 5. The animal half (`Graphic_Multi`) is proven the same way

`run 44669724455` (`modcheck`, same host, one generated mod that ships animal-style names:
`IbexLeak_south.png.png`, `IbexLeak_southm.png.png`, …):

| run | collection (weapon path) | multi (animal path) |
|---|---|---|
| control, leak layout | `fresh variants=10 masks=5` | `resolved=0/4` — every direction `<null>` |
| + GlyphFix 2.6.4 | `fresh variants=5 masks=0` | `resolved=4/4` — `IbexLeak_south/north/east/west.png` |

Two distinct failures, two distinct repairs, both measured on the objects the engine built:

* `Graphic_Collection` — the mask slips into the variant list and `thingIDNumber % n` draws it for
  half the items. Fixed by `CollectionVariants` (postfix on `Init`).
* `Graphic_Multi` — the per-direction key itself misses (`<path>_east` vs asset `…_east.png`), so the
  animal draws nothing at all. Fixed by `ContentFallbackFix` (miss-only retry with the extensions
  appended). No mask is involved in this half.

A probe for the Multi half needs no `ThingDef`: `GraphicDatabase.Get<Graphic_Multi>(path, shader,
Vector2.one, Color.white)` resolves purely from `texPath`, which makes the experiment independent of
def XML.
