# GlyphFix — icon & glyph repair for RimWorld 1.7 (Waylith Android port)

**English** · [简体中文](README.zh-CN.md) · [Русский](README.ru.md)

Runtime image-recovery patches for the Waylith-based Android port: small hooks, no engine changes,
no APK rebuild.

> Formerly `AgentTouchFix`. Same code, better name.

## What it fixes

2.7.8 covers the complete known recovery chain used by the four reported surfaces:

- **build menu** — `BadTex`, unrenderable `CutoutComplexUI`, wrong collection/texture paths;
- **colonist and item tables** — Thing/def icon fallback, cases where only a material or name is bad;
- **animal icons** — `Graphic_Multi`, directional masks, double extensions, and Thing-level
  `CutoutComplexUI`;
- **Odyssey unique weapons** — `.png.dds`, DX10 BC4 masks, `Graphic_Random`, and pack-backed DDS.

The broader layers also repair DDS formats and hollow mip chains, DDS/PNG shadowing, `BadTex`
entries that hide an earlier mod's texture, and transparent static-atlas replacement tiles. The
complete symptom-to-gate matrix is in `docs/IMAGE-COMPLETENESS.md`.

**1. Blank build-menu icons.** `ThingDef.uiIcon` ends up as `BaseContent.BadTex` because the
graphic material resolves to a `CutoutComplexUI` material whose texture never becomes readable on
this port. GlyphFix loads the icon straight from the graphic's texture path at draw time and drops
the icon material that cannot render here.

**2. The empty box next to Draft.** That box is the weapon's attack gizmo. When the command is
disabled the game sets `lowLight = true`, which reaches `Widgets.ThingIcon(grayscale: true)` and
builds a material from `ShaderDatabase.GrayscaleGUI` (`Misc/GrayscaleGUI`). The port ships 262
hashed shader programs and does not register that one, so `ShaderRegistry.Resolve` falls back to the
default program and the icon draws as nothing — a flat grey slab. GlyphFix forces `grayscale =
false`; the disabled look still comes from the caller's `GUI.color` tint, so the icon dims instead
of disappearing and brightens once the pawn is drafted.

**3. Items whose own graphic has no texture.** `Widgets.ThingIcon(Rect, Thing, ...)` draws whatever
`Widgets.GetIconFor(Thing, ...)` returns and gives up when that is null or `BaseContent.BadTex`
(`Widgets.cs:489-495`). That overload is instance level — `thing.Graphic.ExtractInnerGraphicFor(thing)
.MatAt(rot).mainTexture` — so a Thing carrying a cloned or reskinned graphic (mod stuff/colour
variants, worn art) can hand back a texture-less material while the same def's `uiIcon` and `graphic`
are fine. GlyphFix postfixes both `GetIconFor` overloads and, **only when the result is unusable**,
adopts `def.uiIcon`, else `def.graphic.MatAt(def.defaultPlacingRot).mainTexture`, and nulls the
`out Material` so the tile is drawn without the material that just failed. Turn it off with
`files/game/agenttouchfix.iconfallback` containing `off`.

## Cost

| | |
|---|---|
| Steady state | **16.7 ns** per patched call (a bare `HashSet.Contains` costs 13.67 ns on the same runner) |
| Icon fallback | one null test + one reference test per `GetIconFor` call; reflection only when the result is unusable |
| Total | **< 0.1 % of one core** at ~30 k calls/s |
| Load time | unchanged — `ThingIcon` is never called before the main menu |
| Memory | ≤ 4096 cached defs + ≤ 512 material names, ~0.3 MB, cleared on play-data reload |

The verdict cache is dropped on `PlayDataLoader.ClearAllPlayData` and `HotReloadDefs`, so switching
language cannot leave icons permanently blank; a 250 ms sentinel covers the case where those hooks
are missing.

## Known issues

**Port defect: the double extension breaks every texture lookup that asks for a mask.** DLC art
ships as `UniqueRevolverA_m.png.dds` and `Lynx_northm.psd.dds`, and this port builds its content
index by stripping exactly one extension (`Verse/ModContentHolder.cs:56-63`), so the key becomes
`UniqueRevolverA_m.png` / `Lynx_northm.psd` — the inner extension stays. `Texture2D.name` goes the
same way (`Verse/ModDdsLoader.cs:19,74`). Vanilla asks for the bare name, because on desktop the
index has no extension at all:

```
Graphic_Single.cs:97   maskTex  = ContentFinder<Texture2D>.Get(path + "_m", false)
Graphic_Multi.cs:210   mask[0]  = ContentFinder<Texture2D>.Get("Lynx" + "_north" + "m", false)
```

Every mask lookup misses, the graphic is built with `maskTex = null`, the mask-aware shader samples
an empty mask and the whole tile draws blank: Odyssey's 13 unique weapons, plus every animal whose
directional masks exist (Ibex `*_eastm`, Lynx `*_eastm`) while Alpaca and Cow, which have no masks,
render fine. The same shape also puts masks into `Graphic_Collection.subGraphics`, because that
filter is a name test too (`Graphic_Collection.cs:58-66,92`, `!x.name.EndsWith("_m")`).

GlyphFix repairs the lookup rather than the naming: a postfix on `ContentFinder<Texture2D>.Get` (and
on `ModContentHolder<Texture2D>.Get` underneath it) fires **only when the lookup has already
failed** and retries the same path with `.png` / `.psd` / `.jpg` / `.dds` appended, returning the
first hit. Successful lookups are untouched, no index key is rewritten, and nothing is cached —
misses included. One line per recovered texture: `content: extension fallback '<path>' -> '<hit>'`.
`Texture2D.name` itself is deliberately left alone: the wrong-but-load-bearing value is what every
other content path on this port already resolves against, so "fixing" it globally would move the
breakage instead of removing it.

The list half needs a repair of its own, because the fallback makes the mask *load* instead of
disappearing: `Graphic_Random` draws `subGraphics[thingIDNumber % count]`, so a mask that stayed in
the array is drawn for roughly half of the items. `CollectionFix` postfixes
`Verse.Graphic_Collection.Init` and hands the variant names to `CollectionVariants.Filter`, which
drops a name only when the list is fully explained by "texture plus its own mask":

* `before == after * 2`, and every pairing prefix holds exactly two names — one texture, one mask
  (`X` + `X_m`, or a rotation pair `Lynx_north` + `Lynx_northm` / `_eastm` / `_southm` / `_westm`);
* the mask test peels every texture extension first, so `UniqueRevolverA_m.png` and
  `Lynx_northm.psd` are recognised exactly like the bare names.

Anything else — a collection with no masks, a lone mask, two textures under one prefix, a
`Graphic_Multi`-only collection — comes back as the very same list and is reported once:
`collection: variant shape unexpected, left untouched (n=<count>)` (capped at five lines). A drop is
logged as `collection: dropped N mask variant(s) at '<folder>', B -> A`. If
`Verse.Graphic_Collection` cannot be found the patch warns and skips; the rest of the mod is
unaffected.

## Install

Drop `GlyphFix` into `Mods/`, enable it, restart. Nothing else to configure.

To run the tested build/package flow from this checkout:

```bash
./repair-glyphfix.sh
```

To stage it into a user-supplied Mods directory (never a hard-coded device path):

```bash
./repair-glyphfix.sh --install /path/to/RimWorld/Mods
```

The installer refuses to overwrite an existing install unless `--force` is supplied; with
`--force`, the old folder is moved under `Mods/.glyphfix-backups/` first. It matches an existing
`glyphfix` folder case-insensitively, so it does not create a duplicate package.
It does not edit `ModsConfig.xml`; enable GlyphFix in the game's mod list and restart.

## Requirements

RimWorld 1.7 (Waylith engine). Built and tested against the Android port `com.ports1de.waylith`
on a Xiaomi 23078RKD5C, Android 14.
