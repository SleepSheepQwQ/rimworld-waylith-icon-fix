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

**3. Blank icon on a modified weapon (Odyssey).** The def icon is not the problem there: with
`Gun_Revolver` + attachments the tile is empty because `Widgets.ThingIcon(Rect, Thing, ...)` draws
the *instance* graphic's `mainTexture` (`Widgets.GetIconFor(Thing ...)`), and the material built per
instance from the def's `graphicData` has no usable `_MainTex` / texture on this port. GlyphFix
classifies that instance graphic in the `ThingIcon` prefix and substitutes `def.uiIcon`, falling back
to `def.graphic.MatAt(defaultPlacingRot).mainTexture` when the def icon is broken too, dropping the
material argument that painted nothing. One log line per def: `icon fallback: <defName> via
<source>`. The draw call itself is untouched.

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

## Install

Drop `GlyphFix` into `Mods/`, enable it, restart. Nothing else to configure.

## Requirements

RimWorld 1.7 (Waylith engine). Built and tested against the Android port `com.ports1de.waylith`
on a Xiaomi 23078RKD5C, Android 14.
