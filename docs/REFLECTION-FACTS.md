# Reflection facts — what `Assembly-CSharp.dll` actually exposes

Read from the shipped assembly, not from decompiled text and not from the upstream PC sources.

| item | value |
|---|---|
| file | `Assembly-CSharp.dll` (Waylith port, RimWorld 1.7) |
| assembly version | `1.7.9795.32073` |
| size / md5 | 15,471,104 bytes / `0ea12622fadf518a83448a818abd6884` |
| MVID | `283ab371-fb7f-4be7-9330-8931b3c5cc1e` |
| type defs | 16,222 |
| read with | `System.Reflection.Metadata` (`PEReader` + `ISignatureTypeProvider`), metadata only — no code executed, no decompiler |

Reproduce:

```bash
dotnet run -c Release --  # tools/factprobe-style program, one argument: the dll path
```

## 1. `uiIcon` is a public instance **field**, declared on a base type

```
Verse.BuildableDef                              [Public, Abstract, BeforeFieldInit]  base=Verse.Def
  FIELD  uiIcon : UnityEngine.Texture2D         [Public]            (no InitOnly -> writable)
  FIELD  uiIconMaterial : UnityEngine.Material  [Public]            (no InitOnly -> writable)
  FIELD  graphic : Verse.Graphic                [Public]
Verse.ThingDef   [Public, BeforeFieldInit]      base=Verse.BuildableDef   (declares no uiIcon)

every other type with a member named uiIcon also has it as a field:
  Verse.DrawStyleDef, Verse.MechWorkModeDef, Verse.ThingStyleDef,
  RimWorld.PsychicRitualDef, RimWorld.AbilityDef, RimWorld.MonolithLevelDef
no type in the assembly has a *property* named uiIcon
```

So 2.6.1's log line `drawfix: uiIcon field not found on ThingDef` was not a "it is a property
instead" answer: the member exists, as a field, one level up on `Verse.BuildableDef`. The lookup was
`Type.GetField("uiIcon", Public|NonPublic|Instance)` walked level by level and returned null for both
levels on the device, and the code turned that single null into "the game has no such member" and
skipped every repair for the rest of the process.

`Verse.Def.defName`, `Verse.BuildableDef.graphic`, `Verse.ThingDef.graphicData`,
`Verse.GraphicData.texPath`, `Verse.Graphic.path` are public instance fields too.

## 2. `Faction.OfPlayer` is a static, get-only **property** — and the class is in `RimWorld`

```
Verse.Faction                : NOT FOUND
RimWorld.Faction             [Public, BeforeFieldInit]
  PROPERTY OfPlayer : RimWorld.Faction   get:[Public, Static] set:NONE
  METHOD   get_OfPlayer() : RimWorld.Faction   [Public, Static, SpecialName]
RimWorld.FactionManager.OfPlayer : instance property (get-only)
```

Two independent reasons the 2.6.1 lookup produced nothing:

1. `Refl.T("Verse.Faction")` returns null — the type is `RimWorld.Faction`. Nothing after that line
   ran, so the code never even reached `OfPlayer`.
2. `OfPlayer` is a **static** property with no setter. A field-first lookup that requires
   `BindingFlags.Instance`, or a read path that passes an instance, cannot use it.

`Verse.Thing.Faction` (instance property, `get:[Public, Final, Virtual]`) and the backing field
`Verse.Thing.factionInt : RimWorld.Faction [Public]` both exist, so `FactionOf(thing)` was fine.

## 3. `Widgets.GetIconFor` — three overloads, two of them matter

```
1) GetIconFor(Verse.ThingDef, Verse.ThingDef, Verse.ThingStyleDef, System.Nullable`1<int>) : UnityEngine.Texture2D
     [Public, Static]        params: thingDef, stuffDef, thingStyleDef, graphicIndexOverride
     -> no by-ref Material: the 4-argument convenience form that forwards to (2)

2) GetIconFor(Verse.ThingDef, UnityEngine.Material&, Verse.ThingDef, Verse.ThingStyleDef, System.Nullable`1<int>) : UnityEngine.Texture2D
     [Public, Static]        params: thingDef, material [Out], stuffDef, thingStyleDef, graphicIndexOverride
     -> out Material, first parameter ThingDef

3) GetIconFor(Verse.Thing, UnityEngine.Vector2, System.Nullable`1<Verse.Rot4>, bool,
              float&, float&, UnityEngine.Vector2&, UnityEngine.Color&, UnityEngine.Material&) : UnityEngine.Texture
     [Public, Static]        params: thing, size, rot, stackOfOne, scale[Out], angle[Out],
                                    iconProportions[Out], color[Out], material[Out]
     -> out Material and out Color, first parameter Thing
```

The fallback belongs on **(2)** and **(3)**: those are the ones that hand the icon material out.
(1) hands out nothing, so patching it would run the fallback twice per draw.

Why 2.6.1 logged `patched 1`: the Thing overload's colour parameter was compared as
`ps[i].ParameterType != typeof(UnityEngine.Color)`. For an `out Color` parameter the parameter type is
`UnityEngine.Color&`, which is never equal to `UnityEngine.Color`, so (3) was skipped and only (2) was
patched. The Material parameter was matched by name (`"material"`), which happens to be right in this
build but is not a fact the assembly guarantees.

Matching by shape — first parameter `Thing`/`ThingDef` plus a by-ref `Material` — accepts exactly
(2) and (3) and rejects (1): **2 of 3**.

## 4. `Standable` is a static method on `Verse.GenGrid`

```
Verse.GenGrid.Standable(Verse.IntVec3 c, Verse.Map map) : bool   [Public, Static]
Verse.GenGrid.Walkable (Verse.IntVec3 c, Verse.Map map) : bool   [Public, Static]
Verse.IntVec3 has no member named Standable at all
Verse.GridsUtility (55 methods) has none either
```

2.6.1 searched `IntVec3` for an *instance* method `Standable(Map)`, found nothing, and left the check
empty — `Usable()` then answered `true` for every cell, so the copy could be dropped on unwalkable
terrain. `Verse.IntVec3..ctor(int newX, int newY, int newZ)` does exist, so the neighbour search
itself was fine.

## 5. What the code does with these facts

| regression | 2.6.1 assumption | fact | 2.6.2 |
|---|---|---|---|
| `drawfix: uiIcon field not found` | instance field of the runtime type, or nothing | public instance field on `Verse.BuildableDef` | `Member.Resolve(type, name, needWrite, preferField)` — every binding flag per hierarchy level, plus a `GetMembers` sweep, field first then property, static or instance; writability required where the fix writes |
| `testspawn: Faction.OfPlayer not found` | `Verse.Faction` field/property, instance | `RimWorld.Faction.OfPlayer`, static get-only property | type tried as `Verse.Faction` then `RimWorld.Faction`; `OfPlayer` resolved property-first, static-aware, `Get(null)` |
| `iconfallback: patched 1` | parameter literally named `material`, colour type identity | two overloads carry `out Material`, the Thing one also `out Color` | shape match (first param `Thing`/`ThingDef` + by-ref `Material` [+ by-ref `Color` for the Thing form]) → 2 patched, 1 rejected |
| `testspawn: standable=False` | `IntVec3.Standable(Map)` instance method | `Verse.GenGrid.Standable(IntVec3, Map)` static | static form first, instance form as fallback, fail-open if neither |

The decisions themselves are pure (`MemberChoice`) and covered by truth-table tests in
`tests/TexSt/MemberChoiceTests.cs`; the resolver is covered against local stand-in types in
`tests/TexSt/MemberResolveTests.cs` (field only / property only / both / neither / static / get-only /
InitOnly / declared on a base type). Both run in CI, where no game assembly is available.

Verified against the real assembly outside CI (metadata only, nothing executed beyond reflection
lookups): `uiIcon resolved as field on Verse.BuildableDef (writable)`,
`OfPlayer resolved as property on RimWorld.Faction (static)`, `patched 2 of 3`,
`standable resolved as static GenGrid.Standable(IntVec3, Map)`.

## 6. Members the read-only walk leans on

Every one of these was re-checked against the shipped assembly for 2.6.3 (metadata only, nothing
loaded or executed), because 2.6.1 and 2.6.2 both shipped members that did not exist:

```
Verse.Find.Maps                                (static property, List<Map>)
Verse.Find.CurrentMap                          (static property, fallback when Maps is empty)
Verse.Map.mapPawns : Verse.MapPawns            [Public field]
Verse.MapPawns.AllPawnsSpawned                 (instance property, IReadOnlyList<Pawn>)
Verse.MapPawns.pawnsSpawned : List<Pawn>       [Public field]  (backing list, kept as fallback)
Verse.Map.MapUpdate()                          (instance, 0 params -> the tick hook)
Verse.Game.LoadGame()                          (instance, 0 params -> the load gate)
Verse.Root_Play.LoadedGame                     -> DOES NOT EXIST in this port
Verse.Thing.graphicInt : Verse.Graphic         [Public field]
Verse.Thing.Graphic / DefaultGraphic / Rotation / thingIDNumber / Faction
Verse.Thing.factionInt : RimWorld.Faction      [Public field]
Verse.BuildableDef.uiIcon : UnityEngine.Texture2D [Public field] (ThingDef declares none)
Verse.Graphic.MatAt(Rot4, Thing) : Material    (2 params, virtual)
Verse.Graphic.MatSingle                        (instance property; NOT a field named matSingle)
Verse.Graphic_Single.mat : Material            [Public field]
Verse.Graphic_Multi.mats : Material[]          [Public field]
Verse.Graphic_Collection.subGraphics : Graphic[] [Public field] -> the variant count
Verse.Graphic_Random                           base chain: Graphic_Random -> Graphic_Collection
Verse.Pawn.equipment : Verse.Pawn_EquipmentTracker [Public field]
Verse.Pawn_EquipmentTracker.Primary            (instance property, get/set)
Verse.BaseContent.BadTex                       [Public static InitOnly]
```

Two of these are load-bearing corrections:

* **`Verse.Root_Play` has no `LoadedGame`.** The gate falls through to `Verse.Game.LoadGame`, which
  is why the walk is bound to `Map.MapUpdate` (one tick later) whenever that hook installs: the load
  callback then only arms the flag, and nothing is read while the deserializer is still running.
* **`MatSingle` is a property, not a field.** The 2.6.2 texture read asked for a lowercase field
  `matSingle`, got null on every graphic that ignores rotation, and would have printed `tex=null`
  for anything the material accessor did not answer. The read now goes
  `MatAt(rot, thing).mainTexture` -> `MatSingle` -> `Graphic_Single.mat` / `Graphic_Multi.mats[0]`.

`subGraphics` exists only on `Graphic_Collection`, so the mod prints `subgraphics=<n>` for any type
whose chain carries that class (`Graphic_Random` does) and `-` otherwise. Nothing in this list is
written: the module is read-only, and the CI guard greps it for `MakeThing`, `ThingMaker`,
`GenSpawn`, `DeSpawn`, `Destroy`, `Remove` and `Spawn(` so a future edit cannot quietly reintroduce
a write path.

