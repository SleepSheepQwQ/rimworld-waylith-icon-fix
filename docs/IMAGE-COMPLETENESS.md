# Image completeness audit

This is the release gate for "an image is blank even though usable art exists". It follows the image
from the source entry to the texture cache, content key, graphic variant, icon draw, and static atlas.
A mechanism is considered covered only when it has an implementation, a falsifiable test, and an
observable result. A genuinely absent asset is listed separately instead of being disguised as a
successful repair.

## Recovery layers

| Layer | Failure recovered | Implementation |
|---|---|---|
| DDS loader | wrong DXGI mapping, unsupported blocks, hollow mip chains, loose and pack-backed files | `DdsPrefixAlt`, with `DdsFormatFix` as the loose-file fallback |
| Content key | missing inner extension, case-only spelling, unusable later-mod entry | `ContentFallbackFix`, `CaseFix` |
| File ownership | an undecodable DDS removing a working same-name image | `ShadowFix` |
| Graphic collection | `_m` masks becoming variants or being sorted first | `CollectionFix` |
| Icon draw | `BadTex`, instance/def graphic mismatch, unrenderable `CutoutComplexUI` | `IconFieldFix`, `IconFallbackFix` |
| Static atlas | a failed blit leaving a transparent tile that replaces the source | `AtlasBakeFix` |

## Known causes

| Symptom / cause | Coverage | Evidence |
|---|---|---|
| Build-menu `uiIcon` blank through `CutoutComplexUI` | implemented | 2.7.7 device confirmation; public CI |
| Draft gizmo flattened to a grey box | implemented | `UvFix`; existing shader/icon tests |
| `_m` masks leaked into `Graphic_Collection` | implemented | real host run `44669186310` |
| Double-extension content key makes `Graphic_Multi` miss | implemented | real host run `44669724455` |
| Three ordinal case mismatches | implemented | real host run `44738256380` |
| DX10 BC4/BC5/BC1/BC3 formatted as BC7 | implemented | real host run `45309425523` |
| DXT3/DXT2/DXT4 refused by the loader | implemented | real host runs `45309425523`, `45309757735` |
| One-mip block texture turns transparent when minified | implemented | ddsprefix `Mip1`/`Dxt3` fixtures |
| DXT1 punch-through alpha uploaded as opaque BC1 | implemented | ddsprefix `Dxt1Alpha` |
| Undecodable DDS shadows a working PNG | implemented | `ShadowFix`; dedicated CI workflow |
| Animal list synthesises `CutoutComplexUI` on the Thing overload | implemented | 2.7.7 device confirmation |
| Unique weapon: `Thing.def` field missed | implemented | `IconFieldFix`; Bench contract |
| Unique weapon: collection folder used as texture path | implemented | `IconVariantPath`; TexSt + uniqueicon gate |
| Unique weapon: pack DDS loaded before the prefix | implemented | `DdsPrefixAlt.SweepContent` enumerates loose and pack files |
| Later-mod `BadTex` hides an earlier-mod texture | implemented | `ContentFallbackFix` treats `BadTex` as a local miss |
| Static atlas keeps a transparent tile for a failed blit | implemented | negative control + pure decision tests; positive control under CI audit |

## Deliberate limits

1. A texture that does not exist in any active source cannot be reconstructed. The only safe outcome
   is a documented miss.
2. Eleven `ShaderDatabase` names have no GLSL source in the package. A mod cannot invent the missing
   shader authors' output; affected effects can still use the engine's fallback shader.
3. BC6H is not decoded by GlyphFix. No shipped content uses it; the repair produces a visible neutral
   texture and logs the file instead of silently claiming success.
4. `AtlasBakeFix` prefers dropping an unsafe tile and drawing the source texture. A missing atlas is a
   performance loss, never a new image-loss path.

## Release rule

2.7.8 is releasable only when the public build, DDS prefix gate, shadow gate, unique-weapon positive
   gate, and Atlas positive/negative gates all pass with the exact DLL md5 recorded in
   `docs/VERIFICATION.md`.
