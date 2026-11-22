
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

### 8. How far the case bug reaches: exactly three defs in the shipped content

Cross-checking every `texPath` in `Data/*/Defs` (2709 entries, including `<bodyGraphicData>` and
`<dessicatedBodyGraphicData>`) against the 3418 texture keys of the six packs, case-sensitively, leaves
three keys that exist only under a different spelling:

| def asks for | the pack has | what the player sees |
|---|---|---|
| `Things/Pawn/Animal/Ibex/IbexMale_east` | `IbexMALE_east` | the male ibex draws nothing |
| `Things/Item/Equipment/WeaponMelee/LongSword` | `Longsword` | the long sword has no texture |
| `Things/Projectile/Bullet_big` | `Bullet_Big` | that projectile has no texture |

No other mismatch exists in Core or the five DLCs, which is exactly why the symptom reads as "a few
animals and items", not as a broken install - and why Windows, whose file system ignores case, never
showed it. `CaseFix` (2.6.5) repairs all three through the same miss-only fallback.

### 9. 11 of the 63 shaders `ShaderDatabase` asks for are not shipped (corrected)

Comparing every `LoadShader("…")` name in `Verse.ShaderDatabase` (63) against every `.glsl` under
`Data/Core/Shaders/**` **by file name, in any subdirectory**, 11 names have no source and no
`.spv-cache` entry:

```
Map/CutoutFlying01   Map/CutoutOverlay    Map/IndoorMaskMasked   Map/MoteGlowDistorted
Map/RitualStencil    Map/SolidColorBehind
World/WorldOverlayAdditive            World/WorldOverlayAdditiveTwoSided
World/WorldOverlayTransparent         World/WorldOverlayTransparentLit
World/WorldOverlayTransparentLitPollution
```

**Correction of an earlier note in this file:** `Misc/GrayscaleGUI`, `Misc/Invisible`,
`Misc/Metalblood` and `Misc/Silhouette` are *not* missing - they ship as `Map/GrayscaleGUI.frag.glsl`
and friends, and `ShaderDatabase.TryLoadShader` maps a path to a `ShaderDef` name before
`Shader.CreateLateBound`, so the `Misc/` prefix is not what breaks them. The disabled-command box was
therefore never a "missing shader": the grayscale branch builds its own `MaterialRequest`, and when
the material comes back null `Widgets.ThingIcon` substitutes `Texture2D.redTexture` as the mask, which
flattens the sprite to a single colour - the grey box with the black bar. GlyphFix bypasses the branch
(`grayscale: false`), which is why it disappears.

`Map/MoteGlowDistorted` is referenced by 4 `<shaderType>` entries in the shipped XML and does fall
back to the default shader.

### 10. Two more engine-side ways to lose an image (run `44746260732`, real Vulkan/llvmpipe)

**Mip chains are allocated but not written for block formats.** `Texture2D.NewFileTexture`
(`dc2/Waylith.Rendering/Texture2D.cs:767`) promotes a 1-mip file to `CalculateMipCount(w,h)` levels
(256² -> 9), `UploadLevels` allocates `max(MipCount, uploads.Length)` and writes only the uploaded
levels, and `GpuTextureService.GenerateMipmaps` is gated by `!IsBlockCompressed(format)`. Measured on a
real Vulkan device in CI:

| fixture | non-zero texels in mip 2 |
|---|---|
| `bc1_mip0_only` | **0 / 2048** (transparent) |
| `bc1_all_mips` | 1024 / 2048 |
| `rgba8_mip0_only` | 0 / 16384 |
| `rgba8_all_mips` | 16384 / 16384 |

RGBA32 survives because the GPU is allowed to generate the missing levels; BC does not. So a mod's
1-mip DDS turns transparent as soon as it is drawn smaller than its native size - an icon at 34 px from
a 128 px source, or a map zoomed out.

**The atlas registers a tile before it draws it.** `StaticTextureAtlas.Bake` fills `tiles`
(`Verse/StaticTextureAtlas.cs:164-171`) and then blits each texture into a page that was cleared to
transparent (`:474`, `:492`). A failed blit only calls `StubWarner.LogStub`
(`UnityStubs/Graphics.cs:560-596`) - no exception, no error. `Graphic.TryGetTextureAtlasReplacementInfo`
then happily swaps the source texture for that transparent tile, and the source is never used again.
The failure is reachable during `PlayDataLoader.DoPlayLoad`, because `BakeStaticAtlases` runs inside the
deferred-loading window where textures are metadata-only.

### 11. What mod content actually looks like (sampled 758 public mod repos)

* 364 repos ship `Textures/`; only **19** contain any `.dds` (5.2 %).
* **All 19** also ship a same-named `.png` (707 pairs, 673 of them in-game textures), so
  `ShadowedByDds` silently deleting the PNG is the normal case for DDS mods, not an edge case - it only
  bites when the DDS is unsupported, corrupt, or older than the PNG.
* Of 712 sampled `.dds` headers: 74 DXT1, 638 DX10 - and **all 638 DX10 are BC7 (dxgi 98/99)**. No
  DXT3/BC4/BC5/BC6H in the wild, so the format-mapping bugs are traps rather than everyday breakage.
* `LoadFolders.xml` truncation of the implicit root/`Common` scan: 0 hits in 41 sampled manifests.
* Case-only mismatches in 15 small repos: 0 - the three live mismatches are in the shipped Core data.

### 12. The atlas hole, proven one way only (branch `atlasfix`, run 44756452119)

`StaticTextureAtlas.Bake` registers a tile before it blits, and a failed blit only calls
`StubWarner.LogStub`. A GPU-free probe on the shipped assemblies measures both halves:

| | textures | tiles | dead texture in `tiles` | `TryGetTextureAtlasReplacementInfo` |
|---|---|---|---|---|
| unpatched | 2 | 2 | **true** | **true**, mainTexture = `TextureAtlas_Misc_False_-4` |
| patched | 0 | 0 | false | false (source texture kept) |

The patch is a `Priority.Low` postfix on `Bake(bool)` that re-checks the same predicate the blit gates
on (`EnsureGpuResident()`, with one `FinishPendingBuild()` retry), unregisters the tiles that fail,
nulls the page cache keys and clears `Graphic.replacementInfoCache`.

**What is not proven:** the positive control - that healthy textures still end up in the atlas. The GPU
job reaches `Glfw.Init()` but dies in `Brutal.VulkanApi.Vulkan`'s type initializer because the port's
`libvulkanshim.so` is arm64-only. Until a device run shows healthy tiles surviving, the module stays on
branch `atlasfix` and out of the released zip; its failure mode is safe (dropping a tile only costs
draw calls) but unmeasured, and this mod does not ship unmeasured performance changes.

### 13. Why the CutoutComplexUI material draws nothing (chain walk, static)

The draw path is `Widgets.DrawTextureFitted` -> `GenUI.DrawTextureWithMaterial` -> `Graphics.DrawTexture`
-> `ImGuiGuiBackend.DrawTextureWithMaterial` -> snapshot -> `ImGuiVulkanRenderer.RecordDraws` ->
`ImGuiMaterialRenderer.TryRecord` -> pipeline/descriptor -> `vkCmdDrawIndexed`.

Only two of those stages can produce *zero pixels* for a material draw:

* `TryResolveTexture` (backend :1416) - but it logs once per texture ("cannot draw texture ... reason"),
  and it also serves the plain-texture path, which works for Cutout animals, so it is not selective.
* the pipeline/build stages fail *softly*: `RecordDraws:514` swaps in `FallbackTextureId` and draws the
  plain texture instead. `Program == null` is unreachable in this build (the registry always yields a
  program; the fallback is `SceneDefault.frag`, which itself discards on `a <= 0.001`).

What remains is the material *shader maths*: the material branch does not flip V and the GLSL computes
`uv = vUv * _MainTex_ST.zw + _MainTex_ST.xy`. Measured on the real shader: identity/raw/bundle-order =
15660 px, engine-swapped order = 0 px, a constant-w bound = 16384 px (a full opaque square). So a
`_MainTex_ST` mismatch is the only mechanism that yields exactly "no pixels" while every Vulkan gate
passes - or the shader silently lands on `SceneDefault.frag`, whose `discard` does the same.

`ShaderDatabase.TryLoadShader` only falls back to bundle shaders when the `ShaderDef` is *absent*, and
`CutoutComplexUI` has one (`Shaders.Map.xml`), so the GLSL path (`scaleFirst = false`) is the one in
use; a mismatch would require the ShaderDef and the compiled program to come from different sources.

GlyphFix does not try to repair this: it drops the material and draws the texture, which is the path
plain `Cutout` species already use successfully (see the species/shader correlation in VERIFICATION).

### 14. "CaseFix was blocked by ContentFallbackFix" - refuted with the run's own log

Run 45059535245 (`b.log`) shows every module arming and working:

```
casefix: case-insensitive miss fallback armed (gated on anyReloaded)
ddsfix: ... entered from CaseFix's ContentFinder postfix, native bc4=yes bc5=yes
patchreport: Widgets.GetIconFor -> (5p:Material&)=pre0/post1/fin0
patchreport: Graphic_Collection.Init -> post1
ddsfix: 'Bc5' dxgi=80 mips=8 loader=BC7 -> BC4 (native)
casefix: 'IbexMale_east' -> 'IbexMALE_east'
shadowfix: rescued 'Shadow' from 'Shadow.png'
```

Two consequences: the second `ref T __result` postfix does install, and mip-complete BC4/BC5 content is
uploaded **natively** - only DXT3/one-mip sources are decoded to RGBA32, so the "0.5-2 GB of extra
textures" estimate does not apply to the shipped packs.

### 15. Two more hypotheses tested and killed (runs 45064595078, 45065614287)

**RenderTexture.active diversion - refuted with a positive control.** A recording backend injected into
the headless host shows `active == null` at every icon draw, and the material call arriving exactly once:

```
Ibex      active.before=null  draw=material:1  [CutoutComplexUI_IbexMale_east]
Squirrel  active.before=null  draw=texcoords:1 [material=null]
forced active=RT -> Ibex material calls = 0 (the probe does detect diversion), Squirrel still texcoords:1
```

An IL sweep of all 109 shipped assemblies finds only seven callers that write `RenderTexture.active`,
all of them atlas bake / debug export / gravship screenshots, all paired; and `FrameStart.Run` clears it
at the top of every frame. So the material draw is not being diverted offscreen.

**The MaterialPool `_MainTex_ST` hole is real but unused by shipped code.** Executing the shipped
`MatFrom`: two requests with the same eight key fields but different `_MainTex_ST` return the *same*
material, and the last write wins (1 material where 2 are needed). However a scan of all 91k methods
finds **zero** call sites that write `_MainTex_ST` on a pooled material (atlas bake clones first,
`MaterialDef` builds one per def, panners use their own overlay material). The hole is a trap for mods -
including our own dead `TexStMath` helper - not the cause here.

Also measured: a destroyed `mainTexture` makes `MatFrom` return `BaseContent.BadMat` (safe), but the old
entry is never evicted, so the pool grows without bound across reloads.
