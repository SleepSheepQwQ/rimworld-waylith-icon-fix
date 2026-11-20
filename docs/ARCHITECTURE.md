# Waylith port: rendering architecture as verified

Everything below was verified by executing the shipped assemblies (CI `iconexec`, `headless`)
or by reading their metadata — not by inference. Each section names its evidence.

## 1. Def → texture

```
ThingDef.graphicData (GraphicData)
  └─ GraphicData.Init  ─────────────►  GraphicDatabase.Get(graphicClass, texPath, shader, …)
       (GraphicData.cs, GraphicDatabase.cs)
        │
        ├─ Graphic_Single   : main = ContentFinder.Get(path); mask = Get(path + "_m")
        ├─ Graphic_Multi    : main[d] = Get(path + "_" + dir); mask[d] = Get(path + "_" + dir + "m")
        ├─ Graphic_Random   : Graphic_Collection → GetAllInFolder(path) → filter → group
        └─ Graphic_Appearances: folder + appearance name suffix
```

`ContentFinder<T>` walks every mod's `ModContentHolder<T>.contentList`, then the official roots.
Verified: `ContentFinder.Get` returns null unless `ModContentHolder.anyReloaded` is true.

## 2. Icon path (what the UI actually calls)

```
Widgets.ThingIcon(Rect, Thing, …)                     [Widgets.cs:481]
  └─ Widgets.GetIconFor(Thing, Vector2, Rot4?, bool, out …, out Material)   [9-arg overload]
       └─ thing.Graphic.ExtractInnerGraphicFor(thing).MatAt(rot)
            └─ Graphic_Random.MatAt(rot, thing=null)  →  MatSingle
                 └─ subGraphics[ Rand.Range(0, subGraphics.Length) ]
```

Two consequences, both measured:

1. **The UI picks a random variant on every call.** With ten variants (mask leak), 1000 of 2000
   `GetIconFor` calls returned a mask texture (`iconexec`, run 44626907232).
2. **No `Rand.PushState` anywhere in the icon path** (`Widgets`, `ITab_Pawn_Gear`,
   `Dialog_InfoCard`, `Command_VerbTarget`, `GraphicUtility` all contain zero calls), so drawing an
   icon advances the game's global RNG state. The port makes UI redraw rate part of gameplay
   determinism.

## 3. Material layer

```
MaterialPool.MatFrom(MaterialRequest)
  key = shader + mainTex + color32 + colorTwo32 + maskTex + renderQueue + secondaryTex + shaderParameters(by reference)
```

* Colour is quantised to 8 bits per channel before hashing (`matpool`, run 44561808570).
* `shaderParameters` compares by reference; a freshly built list never hits the cache.
* 5 variants × 3 colours × 2 masks produced 30 materials, 66.7 % of them redundant against a
  draw-time tint. The pool has no eviction, so this only grows.
* `Widgets` swaps the map shader for a UI shader via `ShaderDatabase.TryGetUIShader` +
  `MaterialPool.TryGetRequestForMat`; when either step fails, the widget falls back to drawing the
  raw texture.

## 4. Texture residency (Waylith.Rendering)

```
Texture2D.EnsureResident()
  GpuResource != null                        → true
  CpuData == null || PendingBuild != null    → false
  CpuData.HasData                            → UploadCpuData()
  else                                       → TryUploadFromCache / TryUploadFromSource
```

48-state truth table (`residency`, run 44559298798): 19 states draw nothing; 7 are permanent
(`CpuData` null, released-without-source, released+source+cache-miss which throws). `PendingBuild`
costs exactly one frame.

## 5. Atlas

Static atlases bake at start and evict their source textures (`EvictSourceTextures`). World drawing
then samples the atlas page; UI drawing still samples the source `Texture2D` and must re-upload it.
The fallback texture for a failed resolve is a 1×1 constant (white/black), never grey — a grey box is
`DrawPlaceholderPanel` (15 % fill + 1 px border, `ImGuiGuiBackend.cs:1631`).

## 6. Port-specific deviations found this far

| Area | Deviation | Effect |
|---|---|---|
| Content naming | asset names keep the inner extension when the pack is loose (`X.png`), clean when packed | with the leak, `_m` masks become variants (10 vs 5) |
| Mip chains | 968/3418 Core textures ship mip0 only | completed at upload by `CompletedChainTextureSource` |
| Shader registry | only registered names resolve; unknown names fall back to the default shader | `Misc/GrayscaleGUI` is unregistered, which is why disabled icons need the grayscale bypass |
| Diagnostics | CoreCLR diagnostics IPC never starts on this runtime | no `dotnet-dump`; only `gcore` + a glibc host |
| RNG | UI icon drawing advances the global `Rand` | icon flicker; UI rate feeds gameplay RNG |

## 7. What is still unmapped

* `Graphic_Appearances` sub-graphic selection for stuff variants (only partly read).
* The atlas packing policy per `TextureAtlasGroup` (page size vs residency budget).
* Whether `Thing.Graphic`'s `styleGraphicInt` path can produce a graphic whose sub-graphics were
  built from a different path than the def's own `graphicData`.
