# Why this port loses sprites: one thesis, four assumptions

Every symptom collected in `DIAGNOSIS.md` reduces to a single design decision:

> The port re-implemented Unity's asset pipeline on Linux/Android, but kept the assumptions of the
> Windows build and of the editor's content baking, and it degrades **silently** whenever one of those
> assumptions is violated - a placeholder, a no-op, or nothing at all.

Because nothing throws where a player can see it, one broken assumption is indistinguishable from
another: they all look like "this sprite is blank". The table below separates them.

## Symptom -> mechanism -> evidence

| Player-visible symptom | Mechanism | Evidence | Fixed |
|---|---|---|---|
| Build-menu icons blank (68 defs) | `uiIconPath` empty + `shaderType=CutoutComplex` ⇒ `uiIconMaterial=CutoutComplexUI`, a material the port cannot render | `BuildableDef.ResolveIcon`; DG-SCAN-04/05 | 2.4.0 |
| Grey box with a black bar beside Draft | grayscale branch builds its own `MaterialRequest`; when the material is null `ThingIcon` substitutes `Texture2D.redTexture` as the mask, flattening the sprite to one colour | `Widgets.cs:507-523` | 2.5.x |
| Half of a weapon's icons are dark tiles | masks enter `Graphic_Collection.subGraphics`; `thingIDNumber % n` picks one | `10/5 -> 5/0`, run 44669186310 | 2.6.4 |
| A whole animal is missing (mod content) | `Graphic_Multi` asks `<path>_east`; the leaked key is `…_east.png` | `0/4 -> 4/4`, run 44738256380 | 2.6.4 |
| Male ibex / longsword / bullet have no texture | lookups are **ordinal**; the shipped data spells `IbexMALE`, `Longsword`, `Bullet_Big` | 2709 texPaths vs 3418 keys, run 44738256380 | 2.6.5 |
| A mod's weapon art is blank | a same-named `.dds` makes `ShadowedByDds` drop the `.png`, and the DDS then fails | `Shadow hit=False`, run 44738256380 | no |
| A BC5 mask tints the wrong area | every DX10 header is treated as BC7; BC5 payload read as BC7 | `IndexOutOfRangeException`, run 44738256380 | no |
| Anything drawn small turns transparent | 1-mip BC files get a 9-level image with only level 0 written; BC cannot mip-generate | `bc1_mip0_only 0/2048`, run 44746260732 | no |
| A building is permanently invisible | atlas registers the tile before blitting; a failed blit leaves a transparent tile that replaces the source | `StaticTextureAtlas.cs:164-171` vs `:492` | no |
| Weird flat/grey shading in UI | unregistered shader ⇒ `Default` (0 properties) ⇒ `GenUI` halves the colour and drops the mask | `ShaderUtility.cs:9-20`, `GenUI.cs:200-211` | partial |
| Motes/effects with wrong look | 11 of 63 `ShaderDatabase` shaders are absent and fall back to `Default` | `docs/DIAGNOSIS.md` §9 | no |

## The four assumptions

**A. The file system is case-insensitive.** Windows (and the editor) hide three spelling mismatches in
the shipped data; the port's `contentList`/`ContentFinder` are ordinal. **Fixed** by `CaseFix`.

**B. A file's extension tells you its format.** `ModContentLoader.LoadTexture` branches on
`.dds`; `DdsPixelFormat.IsBc7` branches on `FourCC == "DX10"`. So a valid BC1 named `.png` becomes a
2x2 magenta placeholder, a PNG named `.dds` becomes `BadTex`, and a BC5 mask becomes garbage BC7.
The engine's own pack reader (`DdsFile.cs`) is stricter and correct - the two paths disagree.

**C. Every texture has a full mip chain and is GPU-resident when drawn.** `NewFileTexture` promotes a
1-mip file to a 9-level allocation, fills only level 0, and BC formats cannot generate the rest;
`StaticTextureAtlas.Bake` draws into a cleared page inside the deferred-loading window and keeps the
tile even when the draw fails. Both substitute "empty" for "missing".

**D. There is one source of truth for a texture.** `ShadowedByDds` prefers `.dds` with no fallback;
`ContentFinder` returns the **last** mod's entry even when that entry is `BaseTex.BadTex`; the pack's
virtual names strip extensions while other consumers append them; `-premul.dds` variants are invisible
to every `premultiplied: false` call site. Each rule is individually defensible; together they mean a
correct copy of an image can be present and still unreachable.

## Why it stayed invisible for so long

Every failure lands on a quiet branch: `BadTex` (a valid 2x2 texture), `DecodeFailurePlaceholder`
(magenta), `LogStub` (no error), `GenUI`'s 0.5 dim, `ShaderRegistry`'s one-time "not registered"
warning, or simply `return`. A player sees "blank"; the log says nothing that names the asset. That is
also why the earlier fixes looked successful without being verified: the code path that mattered never
reported failure.

## What a mod can reach, and at what cost

Everything below is reachable with Harmony; the differences are how invasive the patch is and whether
the asset it needs actually exists.

| Mechanism | Patch target | Cost / risk | Status |
|---|---|---|---|
| DDS format mapping (DX10 -> BC7, DXT3/BC4/BC5) | generic postfix on `ContentFinder<Texture2D>.Get`, or `ModDdsLoader.TryLoadDds` | low - decoders are self-contained | in progress (`DdsFormatFix`) |
| Hollow mip chains | decode to RGBA32 and upload a CPU-built chain, or fix up after `NewFileTexture` | medium - CPU mip generation, or block compression | planned |
| `.dds` shadowing a `.png` | prefix `ModContentLoader.ShadowedByDds`: do not shadow when the DDS cannot be decoded | low, but needs a header cache to avoid extra IO on 2000+ files | planned |
| Atlas tile registered before it is drawn | prefix `StaticTextureAtlas.Bake` with `EnsureGpuResident()`, dropping tiles that fail | high - engine internals, runs on the boot path | not attempted |
| 11 missing shaders | ship `Shaders/*.glsl`, or map the name onto an existing shader | **the sources do not exist in the package** - this one is missing assets, not a missing hook | not possible as-is |
| sRGB / VkFormat table | static pure functions in `TexturePixelData` | very wide blast radius for an unclear gain | not attempted |

So "a mod cannot fix it" was the wrong framing for four of the six: those are *patching depth* problems,
not capability problems. Only the missing shaders are blocked by absent material.

## Suggested upstream fixes, cheapest first

1. `DdsPixelFormat`: read the DXGI word for DX10 headers; map BC1/BC4/BC5/BC7; keep the DXT1
   punch-through alpha path (the CPU decoder already has it - the *upload* format drops it).
2. `ModContentLoader.ShadowedByDds`: skip the `.dds` only when it actually decodes; otherwise keep the
   `.png`.
3. `Texture2D.NewFileTexture`: do not promote mip counts for block formats, or fill the chain on the
   CPU before upload.
4. `StaticTextureAtlas.Bake`: `EnsureGpuResident()` before each blit, and drop tiles that failed so the
   source texture stays addressable.
5. `ContentFinder`: prefer a valid entry over a later `BadTex` instead of "last non-null wins".
6. Ship the eleven missing shaders, or map them onto existing ones.
7. Normalise case once, at content load, instead of per-lookup.
