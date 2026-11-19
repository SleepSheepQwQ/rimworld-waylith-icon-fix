# Blank icon: what is proven, what is excluded

All numbers below come from CI harnesses that run **without the game**: real shipped DLLs,
real DDS assets, the real GLSL shaders, and a CPU/Vulkan rasteriser. Every claim has a run id.

## 1. Excluded (measured, not reasoned)

| Hypothesis | Verdict | Evidence |
|---|---|---|
| Names/keys keep the inner extension (`UniqueRevolverA.png`) | **false on this build** | the shipped `Graphic_Collection` predicate run on the real DLL: 5 variants kept, 5 masks filtered |
| Masks leak into the variant list | **false on this build** | same predicate + `iconflow` 44554868087 / `iconflow-b` 44556920217: 5 variants, 0/1000 mask hits |
| A missing mask makes the icon blank | **false** | `iconpipe` 44553128606 and `iconpipe-cpu` 44552170772: null mask is byte-identical to passthrough (max channel delta 0) |
| Missing mips make it blank | **false** | Odyssey ships 128/128 complete chains; Core's 968 mip0-only textures are completed at upload by `CompletedChainTextureSource` |
| Texture eviction produces a grey box | **false** | 48-state truth table (`residency` 44559298798): eviction is not a state; grey placeholder needs a failed `TryResolveTexture`, whose fallback is white/black, not grey |
| `uiIcon` is a property | **false** | `uiIcon` is a public instance field on `Verse.BuildableDef`; the earlier "not found" was a lookup bug in our own probe |
| Carried-item rendering path | out of scope | the reported case is a UI icon, not the world sprite |

## 2. Mechanisms that DO produce a blank icon (each with a fingerprint)

| # | Mechanism | Fingerprint | Harness |
|---|---|---|---|
A | **Extension leak build**: names keep `.png`, masks become variants | 10 variants, 50% of thing ids draw a BC4 mask: 100% coverage, mean RGB (6.4,0,0), luma 1.37/255 | `iconflow*`, `iconpipe*` |
B | Same leak, animal case: direction group key loses the extension | a `Graphic_Multi` pointing at a path that does not exist; 50% of ids get a missing texture, not a mask | `iconflow-b` |
C | Residency dead-ends: `CpuData == null`, or CPU data released with no source, or released+source+cache miss | never draws; 7 of 48 states are permanent (one of them throws `InvalidOperationException`) | `residency` |
D | `PendingBuild` on a frame that cannot flush | blank for exactly one frame, recovers on the next | `residency` |
E | `GetIconFor` returns null / `BadTex` | `Widgets.ThingIcon` returns before drawing anything | metadata + `docs/REFLECTION-FACTS.md` |
F | Material key bloat: `shaderParameters` compared by reference | not a rendering bug: 30 materials for a 5x3x2 matrix, 66.7% redundant, and 60 if each request carries a fresh list | `matpool` 44561808570 |

## 3. Decision procedure for a new report

1. Ask for the **port build string** (or `startup-report.txt`). If it is older than the build that
   strips the inner extension, mechanisms A/B apply and no amount of UI-side patching can fix them —
   the content index itself has to be fixed.
2. If the build is current, mechanisms A/B cannot fire. Then the remaining candidates are C/D/E,
   and they are distinguishable from the log alone:
   - `icon fallback: <def> via uiIcon|graphic` present  -> E was hit (our fallback did the work)
   - `ImGui: cannot draw texture '<name>' (WxH, source ...): reason` -> C or D, the reason names the state
   - neither line, and the icon is still empty -> the draw call never happened; look at the widget
     path, not the texture
3. Only if all three are silent is a device-side capture worth the cost (root `gcore` + analysis on a
   glibc host, since this runtime has no diagnostics IPC).

## 4. What this means for GlyphFix

The three shipped fixes (icon field repair, grayscale bypass, def-level icon fallback) all act on
their stated causes and are harmless when those causes are absent. They cannot repair mechanism A/B,
because that failure happens before any icon is requested: the texture path itself is wrong.
