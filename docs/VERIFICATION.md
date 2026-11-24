# Verification ledger

Every claim this work rests on, with how it was checked. `re-run` = recomputed on 2026-11-20 from the
installed data; `CI` = an A/B run of the real host on GitHub Actions; `code` = read from the shipped
IL/decompiled source but not executed; `open` = not verified yet.

## A. Re-verified just now against the installed content

| # | Claim | Check | Result |
|---|---|---|---|
| 1 | The shipped data has exactly three case-only mismatches | 2709 `texPath` (incl. `bodyGraphicData`) vs 3418 pack keys, case-sensitive, then case-insensitive retry | 3: `Ibex/IbexMale_east` vs `IbexMALE_east`, `WeaponMelee/LongSword` vs `Longsword`, `Projectile/Bullet_big` vs `Bullet_Big` |
| 2 | 11 of the 63 `ShaderDatabase` shaders have no source | every `.glsl` under `Data/Core/Shaders/**` indexed by file name, any directory | 11 absent; the 4 previously called missing (`GrayscaleGUI`, `Invisible`, `Metalblood`, `Silhouette`) ship as `Map/*.frag.glsl` |
| 3 | Official texture formats are uniform | DDS header parse over all six packs | 8308 BC7, 398 BC5, 16 RGBA8; no exotic formats |
| 4 | `Mote_FoodBitMeat` has no texture | every pack's entry list searched for `FoodBit` | only `FoodBitVegetarian` exists |
| 5 | Core and DLC packs name entries differently | entry listing | Core `X.psd.dds.split` / `X.png.dds.split`; DLC `X.png.dds` |

## B. Verified by running the real host in CI (A/B)

| # | Claim | Run | A (no mod) -> B (with mod) |
|---|---|---|---|
| 6 | Leaked `_m` names enter `subGraphics`, so `thingIDNumber % n` draws a mask for half the items | 44757029214 | `variants=10 masks=5` -> `5/0` |
| 7 | `Graphic_Multi` asks `<path>_east`; a leaked key misses and the animal draws nothing | 44757029214 | `resolved=0/4` -> `4/4` |
| 8 | A DX10 header is treated as BC7 whatever it holds, so BC5/BC4 masks are garbage | 44757029214 | `Bc5`: BC7 + `IndexOutOfRangeException` -> BC4, readable `(8.8,0,0)` |
| 9 | `ToTextureFormat` knows only DXT1/DXT5/BC7; DXT3 throws and the entry is lost | 44757029214 | `Dxt3 hit=False` -> `hit=True 8x8` |
| 10 | `ShadowedByDds` deletes the same-named `.png`; when the DDS fails there is no fallback | 44754310223 | `Shadow hit=False` -> `hit=True`, mip-0 hash identical to the png-only fixture |
| 11 | Lookups are ordinal; the three mismatches above resolve to nothing | 44757029214 | `IbexMale_east hit=False` -> `hit=True name='IbexMALE_east'` |
| 12 | 1-mip block-compressed textures get hollow mip chains and turn transparent when minified | 44746260732 (real Vulkan/llvmpipe) | `bc1_mip0_only` non-zero texels in mip 2: 0/2048 (all-mips control 1024/2048); after the fix `mip1lum 52.7` |

## C. Read from the shipped code, not executed end-to-end

| # | Claim | Evidence | Caveat |
|---|---|---|---|
| 13 | DXT1 punch-through blocks are uploaded as `BC1_RGB`, which is opaque by spec | `TexturePixelData.cs:694-695`; CPU decoder honours it at `BcBlockDecoder.cs:625`; unit test keeps `alpha0=16/64` | the "renders opaque black" half is Vulkan spec, not sampled on a device |
| 14 | The atlas registers a tile before blitting and keeps it when the blit fails | 44756452119: control `tiles=2, deadInTiles=true, replacement=true`; patched `tiles=0, replacement=false` | the positive control (healthy texture still baked) needs a device - the GPU job dies on the arm64-only `libvulkanshim.so` |
| 15 | `ContentFinder` returns the last non-null entry, and a later `BadTex` counts as a hit | `ContentFinder.cs:25-40` | not turned into a run |
| 16 | Textures are read before any mod code runs, so loader-side prefixes miss the first pass | 44753371622: `ShadowedByDds` prefix alone left `Shadow hit=False` | on device the sweep may run before atlas baking; only the harness order was observed |
| 17 | Harmony refuses a second `ref T __result` postfix on the same method | ExtFallback run log: "existing result contract requires an assignable type" | single observation |

## D. Guesses this work refuted (keep them dead)

| Claim | Why it died |
|---|---|
| `Misc/GrayscaleGUI` is missing, which is why the Draft box is grey | it ships as `Map/GrayscaleGUI.frag.glsl`; the grey box is the grayscale branch substituting `Texture2D.redTexture` as the mask when the material comes back null (`Widgets.cs:507-523`) |
| `GraphicRequest`'s cache key omits `maskPath`/`colorTwo`/`shaderParameters` | all three are in `GetHashCode`/`Equals` |
| Packed textures are invisible to `ContentFinder` | the Odyssey pack probe resolves pack-only entries: `hit=True 128x128` |
| The four "missing" shaders are absent | they exist under `Map/` |

## E. Still open

1. Device confirmation of 2.7.0 on the reported saves (male ibex, the modified weapon).
2. The atlas fix's positive control (needs a device), so the module stays on `atlasfix`.
3. The 11 genuinely missing shaders: no GLSL sources to ship.

## F. Device confirmation (2026-11-23, user-reported)

With GlyphFix **2.7.7** installed (`GlyphFix.dll` md5 `17202065d0050aaa6382460622242f2f`, About 2.7.7,
enabled in ModsConfig) and the imported save `Деградация.rws` (15,305,813 B, md5 `f0a21e585ace4d32`),
the animal-list icons render correctly.

This closes the chain: the blank icons were the `CutoutComplexUI` material synthesised inside
`Widgets.GetIconFor(Thing)`, which this port cannot draw; dropping that material and carrying its
`_Color` into the icon restores the plain-texture path that `Cutout` species always used.

Note: this confirmation is the user's own observation. No device log was read, and none was requested.

## G. 2.7.8 release-candidate gates (2026-11-24)

| Gate | Run | Result |
|---|---|---|
| public build, all unit suites, repair-script check, benchmark, package | `45314800616` | pass |
| DDS loader prefix: BC4, DX10 BC3, DXT3, one-mip, DXT1 alpha, old 5/0 and 4/4 gates | `45309425523` | pass |
| DdsFormatFix + DdsPrefixAlt enabled together | `45309757735` | pass |
| AtlasBakeFix negative + sentinel-live positive | `45312477319` | 13/13 assertions; control 2/2, patched live kept/dead removed 1/1 |
| unique-weapon positive, all four layouts 5/0 and DRAW | `45314135780` | pass; real, plain, `.png.dds`, and DX10 BC4 layouts all 5/0 with 0 blank picks |
| prebuilt collection scan proof | `45312806973` | all four layouts shortened to 5/0; the run's stale direct-read assertion was replaced |
| isolated ShadowFix PNG fallback | `45314136663` | pass; Shadow missed in control and returned the exact 128x128 PNG pixels with GlyphFix; decodable DDS still won |
| real `Assets.zip` integration and unique weapon | `45314966990` | packed-only A/B fixture; final result pending |

The first uniqueicon attempt exposed a test-design error: it read the pre-existing `uiIcon`
directly and bypassed `Widgets.ThingIcon`, where `IconFieldFix` runs. A second issue was found in
the DDS sweep: a BC4 texture mis-created as BC7 is non-null, so "non-null" was not evidence of a
healthy load. The final gate invokes the real draw path and checks the DDS header before deciding
whether to reload. The shadow gate disables the higher-priority DDS repairs so it tests the fallback
in isolation.

The player-facing build remains conditional on every final row passing against the same DLL hash.
