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
