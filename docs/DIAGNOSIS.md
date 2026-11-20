
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

### 6. Three more root causes found by probing the real content path (`run 44736860452`)

The probe ships loose fixtures through a mod and asks `ContentFinder<Texture2D>` for each, exactly the
call `Graphic_*` makes. Results, all reproduced in one run:

| fixture | result | cause |
|---|---|---|
| `Bc7.dds` | `hit=True 128x128 meanRGB=(12.2,12.2,12.2)` | normal |
| `Bc5.dds` (DX10 header, DXGI 80) | `hit=True`, but `GetPixels32` throws `IndexOutOfRangeException` | `DdsPixelFormat.IsBc7` tests only `FourCC == 'DX10'` (808540228), so **every** DX10 file is uploaded as BC7 whatever it really holds; a BC5 mask becomes garbage |
| `Dxt3.dds` | `hit=False` | `ToTextureFormat` knows only DXT1 / DXT5 / BC7 and throws for everything else; the loader then substitutes `BaseContent.BadTex` (or drops the entry) |
| `Shadow.png` + `Shadow.dds` | `hit=False` | `ModContentLoader.ShadowedByDds` skips the PNG when a same-named `.dds` exists, so when the DDS cannot be decoded there is no fallback left |
| `IbexMALE_east.png` vs key `IbexMale_east` | `True` / `False` | **the lookup is ordinal.** Core ships `IbexMALE_east.psd.dds.split` while `Races_Animal_*` asks for `<texPath>…/IbexMale</texPath>`; on Windows the file system hides this, on Android/Linux the male ibex loses its main texture and draws nothing, while `IbexFemale_*` (spelled consistently) renders |

The engine itself supports BC4/BC5 - `Waylith.Graphics.Textures/DdsFile.cs` accepts "BC7, BC1, BC4,
BC5" and `GpuTextureService` has `VkFormat.Bc5UNormBlock` - so #2/#3 are managed-layer mapping bugs,
not renderer limits.

Fixed in 2.6.5: the case-sensitive lookup (`CaseFix`, postfix on `ContentFinder<Texture2D>.Get` that
retries misses through a lower-cased index). Not yet fixed: the DX10 format mapping and the
png/dds shadowing - both need a decoder for the unstupported formats or a shadowing exception.

### 7. 2.6.5 verified in-engine (`run 44738256380`)

Same `modcheck` run, the case fixture only:

```
control (no mod) : IbexMale_east hit=False
with GlyphFix    : IbexMale_east hit=True name='IbexMALE_east'  meanRGB=(13.0,12.9,13.0)
```

The returned texture is the file that actually exists, with the same pixels as the correctly-spelled
sibling — i.e. the miss is repaired by the lower-cased fallback, not by a placeholder. In the same run
the two earlier gates stayed green (`fresh variants 10/5 -> 5/0`, `multi 0/4 -> 4/4`).
