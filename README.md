# GlyphFix — icon & glyph repair for RimWorld 1.7 (Waylith Android port)

**English** · [简体中文](README.zh-CN.md) · [Русский](README.ru.md)

Two rendering bugs in the Waylith-based Android port, two hooks, no engine changes.

> Formerly `AgentTouchFix`. Same code, better name.

## What it fixes

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

## Cost

| | |
|---|---|
| Steady state | **16.7 ns** per patched call (a bare `HashSet.Contains` costs 13.67 ns on the same runner) |
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

## Install

Drop `GlyphFix` into `Mods/`, enable it, restart. Nothing else to configure.

## Requirements

RimWorld 1.7 (Waylith engine). Built and tested against the Android port `com.ports1de.waylith`
on a Xiaomi 23078RKD5C, Android 14.
