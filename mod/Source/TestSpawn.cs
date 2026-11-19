using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// Diagnostic module: after a save is loaded, drop a fresh copy of every player pawn's primary
    /// weapon on the ground and log the two instances side by side.
    ///
    /// The defect it exists for: the same def draws a weapon icon on one instance and nothing on
    /// another, so the split is instance level. One load therefore produces comparable pairs -
    /// <c>test: origin ...</c> for the weapon the pawn carries and <c>test: spawn  ...</c> for a new
    /// instance made from the same def and stuff - in the user's own log, in their own colony.
    ///
    /// Rules, all deliberate:
    ///   * Nothing on the pawn is touched. No reroll, no replacement, no removal: the copy is added and
    ///     the original stays equipped, so the snapshot describes the save as it really is.
    ///   * Only the save-load path runs, never a freshly generated map. The gate is Root_Play's
    ///     LoadedGame callback; Map.FinalizeInit alone cannot tell the two apart.
    ///   * Every load spawns again, by design: the same save can be reloaded to collect more pairs.
    ///   * Quality and durability are carried over when they can be; when they cannot, the copy is
    ///     still made and only that field is lost.
    ///   * Everything is reflection and everything is inside a try/catch - a diagnostic must never
    ///     cost the player a load.
    /// </summary>
    internal static class TestSpawn
    {
        private const string HarmonyId = "sleepsheep.waylith.agenttouchfix.testspawn";
        private const int LineCap = TestSpawnDecision.LineCap;

        private static bool _resolved;
        private static int _warned;

        private static bool _gateOpen;     // a save was loaded
        private static bool _done;         // the snapshot for that load was taken
        private static bool _tickHooked;

        // ---- types ------------------------------------------------------------------------------
        private static Type _tThing, _tDef, _tPawn, _tFaction, _tMap, _tThingMaker, _tGenSpawn,
                            _tBaseContent, _tIntVec3;

        // ---- Verse.Thing ------------------------------------------------------------------------
        private static FieldInfo _fDef, _fId, _fFaction, _fGraphicInt, _fPawnEquipment;
        private static PropertyInfo _pId, _pLabel, _pLabelCap, _pGraphic, _pDefaultGraphic, _pRotation,
                                 _pPosition, _pPositionHeld, _pStuff, _pHitPoints, _pMaxHitPoints,
                                 _pFaction, _pPawnEquipment;

        // ---- Verse.ThingDef ---------------------------------------------------------------------
        private static FieldInfo _fDefName, _fDefLabel, _fUiIcon;
        private static PropertyInfo _pDefName, _pDefLabel, _pUiIcon;

        // ---- map / lister -----------------------------------------------------------------------
        private static FieldInfo _fListerThings, _fAllThings;
        private static PropertyInfo _pListerThings, _pAllThings;

        // ---- equipment / placement ---------------------------------------------------------------
        private static PropertyInfo _pPrimary;
        private static MethodInfo _mStandable;      // Verse.GenGrid.Standable(IntVec3, Map), else IntVec3.Standable(Map)
        private static bool _standableStatic;       // true for the GenGrid form: two arguments, null target
        private static ConstructorInfo _cCell;

        // ---- player faction ------------------------------------------------------------------------
        // RimWorld.Faction.OfPlayer is a static, get-only *property* (docs/REFLECTION-FACTS.md), and
        // the type lives in RimWorld, not Verse - both were assumed wrong in 2.6.1.
        private static Member _mOfPlayer;

        // ---- fixture sample (a def the save may not contain) ---------------------------------------
        private static Member _mGraphicDataDef, _mGraphicClass, _mTexPathOfDef, _mCenter;
        private static PropertyInfo _pAllDefs;          // DefDatabase<ThingDef>.AllDefs
        private static MethodInfo _finderGet;           // ContentFinder<Texture2D>.Get(string, bool)

        // ---- caches ------------------------------------------------------------------------------
        private static readonly Dictionary<string, FieldInfo> _fields = new Dictionary<string, FieldInfo>();
        private static readonly Dictionary<string, PropertyInfo> _props = new Dictionary<string, PropertyInfo>();
        private static readonly Dictionary<string, MethodInfo> _matAt = new Dictionary<string, MethodInfo>();

        private static object _badTex;

        // ------------------------------------------------------------------ install

        internal static void Install()
        {
            try
            {
                Resolve();

                var harmony = new Harmony(HarmonyId);
                MethodInfo loaded = LoadCallback();
                if (loaded == null)
                {
                    // Without a load-only callback there is no way to tell a loaded save from a new
                    // map, and the module refuses to guess: nothing is patched, nothing is spawned.
                    Refl.Warn("testspawn: no load callback found, module inactive");
                    return;
                }

                harmony.Patch(loaded, postfix: new HarmonyMethod(
                    typeof(TestSpawn).GetMethod("OnLoadCallback", BindingFlags.Static | BindingFlags.NonPublic)));
                Refl.Info("testspawn: gated on " + loaded.DeclaringType?.Name + "." + loaded.Name);

                _tickHooked = PatchTick(harmony);
                Refl.Info("testspawn: tick hooks=" + (_tickHooked ? 1 : 0)
                          + (_tickHooked ? "" : " (snapshot runs inside the load callback)"));
            }
            catch (Exception e) { WarnOnce("install: " + e.Message); }
        }

        /// <summary>
        /// The gate: a callback that only ever runs when a save is loaded. A freshly generated map never
        /// goes through any of these, which is exactly the distinction Map.FinalizeInit cannot make.
        /// Tried in order of specificity; the first one the port actually has is used.
        /// </summary>
        private static MethodInfo LoadCallback()
        {
            var candidates = new[]
            {
                new[] { "Verse.Root_Play", "LoadedGame" },                     // the post-load moment
                new[] { "Verse.Game", "LoadGame" },                            // the deserializer itself
                new[] { "Verse.SavedGameLoaderNow", "LoadGameFromSaveFileNow" }, // oldest of the three
            };

            foreach (string[] candidate in candidates)
            {
                Type type = Refl.T(candidate[0]);
                if (type == null) continue;
                MethodInfo m = Refl.M(type, candidate[1], -1);
                if (m != null) return m;
            }
            return null;
        }

        /// <summary>One cheap per-tick postfix, used only to run the snapshot one tick after the load
        /// callback rather than inside it.</summary>
        private static bool PatchTick(Harmony harmony)
        {
            MethodInfo mapUpdate = _tMap == null ? null
                : (Refl.M(_tMap, "MapUpdate", 0) ?? Refl.M(_tMap, "Update", 0));
            if (mapUpdate == null) return false;

            try
            {
                harmony.Patch(mapUpdate, postfix: new HarmonyMethod(
                    typeof(TestSpawn).GetMethod("TickWithMap", BindingFlags.Static | BindingFlags.NonPublic)));
                Refl.Info("testspawn: patched " + _tMap.Name + "." + mapUpdate.Name);
                return true;
            }
            catch (Exception e) { Refl.Warn("testspawn: " + mapUpdate.Name + " not patched: " + e.Message); }
            return false;
        }

        // ------------------------------------------------------------------ patch entry points

        internal static void OnLoadCallback()
        {
            try
            {
                _gateOpen = true;
                _done = false;
                Refl.Info("testspawn: save loaded, snapshot armed");

                // No tick hook: the load callback itself is after FinalizeInit and after every thing is
                // spawned, so running here is still the load path - just a moment earlier.
                if (!_tickHooked) TryRun(CurrentMap());
            }
            catch (Exception e) { WarnOnce("gate: " + e.Message); }
        }

        private static void TickWithMap(object __instance)
        {
            try { TryRun(__instance); }
            catch (Exception e) { WarnOnce("tick: " + e.Message); }
        }

        private static void TryRun(object map)
        {
            if (!_gateOpen || _done || map == null) return;
            _done = true;
            Run(map);
        }

        private static object CurrentMap()
        {
            Type find = Refl.T("Verse.Find");
            return Read(null, find == null ? null : Prop(find, "CurrentMap"), null);
        }

        // ------------------------------------------------------------------ the snapshot

        private static void Run(object map)
        {
            try
            {
                object playerFaction = PlayerFaction();
                if (playerFaction == null)
                {
                    Refl.Warn("testspawn: Faction.OfPlayer returned null ("
                              + (_mOfPlayer == null ? "member not resolved" : _mOfPlayer.Kind + " on " + _mOfPlayer.DeclaringTypeName)
                              + ") - no pawn copies, the fixture sample still runs");
                }

                int pawns = 0, withWeapon = 0, spawned = 0;

                foreach (object pawn in AllThings(map))
                {
                    if (playerFaction == null) break;
                    if (!IsPawn(pawn)) continue;
                    if (!ReferenceEquals(FactionOf(pawn), playerFaction)) continue;
                    pawns++;

                    object weapon = PrimaryOf(pawn);
                    if (weapon == null) continue;
                    withWeapon++;

                    if (spawned >= LineCap)
                    {
                        Refl.Info(TestSpawnDecision.SkipLine("line cap reached cap=" + LineCap));
                        break;
                    }
                    if (SpawnCopy(pawn, weapon, map)) spawned++;
                }

                Refl.Info("testspawn: done pawns=" + pawns + " with-primary=" + withWeapon
                          + " copies=" + spawned);

                // Always last, and always attempted: the save that could not reproduce the bug has no
                // unique weapon in it at all, so the sample has to come from the def database.
                SpawnFixture(map, playerFaction);
            }
            catch (Exception e) { WarnOnce("run: " + e.Message); }
        }

        /// <summary>
        /// Make one copy of <paramref name="weapon"/> and place it at the pawn's cell (or the nearest
        /// usable neighbour). Quality and durability are best effort; the copy is made regardless.
        /// Returns true when a thing was spawned and both lines were written.
        /// </summary>
        private static bool SpawnCopy(object pawn, object weapon, object map)
        {
            try
            {
                object def = Read(_fDef, null, weapon);
                if (def == null) { Refl.Info(TestSpawnDecision.SkipLine("no def")); return false; }

                object stuff = Read(null, _pStuff, weapon);

                object made = MakeThing(def, stuff);
                if (made == null) { Refl.Info(TestSpawnDecision.SkipLine("MakeThing returned null")); return false; }

                CopyQuality(weapon, made);
                CopyDurability(weapon, made);

                // Both lines are built before the spawn so a failure to describe a thing can never be
                // reported as a failure to spawn one (and vice versa).
                string originLine = SnapshotLine(TestSpawnDecision.OriginLine, weapon);
                string spawnLine = SnapshotLine(TestSpawnDecision.SpawnLine, made);

                object here = CellOf(weapon, pawn);
                if (here == null) { Refl.Info(TestSpawnDecision.SkipLine("no position")); return false; }

                bool hereUsable = Usable(here, map);
                object neighbour = hereUsable ? null : FindNeighbour(here, map);
                TestSpawnDecision.Placement placement = TestSpawnDecision.Choose(hereUsable, neighbour != null);
                object target = placement == TestSpawnDecision.Placement.Here ? here : neighbour;

                if (placement == TestSpawnDecision.Placement.None)
                {
                    Refl.Info(TestSpawnDecision.SkipLine("no placeable cell at " + CellText(here)));
                    DestroyQuietly(made);
                    return false;
                }

                try
                {
                    if (!SpawnThing(made, target, map)) throw new Exception("GenSpawn.Spawn not resolvable");
                }
                catch (Exception e)
                {
                    Refl.Info(TestSpawnDecision.SkipLine("spawn failed: " + e.GetType().Name + " " + e.Message));
                    DestroyQuietly(made);
                    return false;
                }

                if (placement == TestSpawnDecision.Placement.Adjacent)
                {
                    Refl.Info(TestSpawnDecision.FallbackLine(LabelOf(weapon), CellText(here), CellText(target)));
                }

                Refl.Info(originLine);
                Refl.Info(spawnLine);
                return true;
            }
            catch (Exception e)
            {
                Refl.Info(TestSpawnDecision.SkipLine(e.GetType().Name + " " + e.Message));
                return false;
            }
        }

        private delegate string LineBuilder(string defName, int id, string graphicType, string texName,
                                            int width, int height, string uiIcon, bool same);

        // ------------------------------------------------------------------ the fixture sample

        /// <summary>
        /// Spawn one sample of a def the save is unlikely to contain, so a blank-icon report can be
        /// reproduced from *any* save: the save that could not reproduce it had no unique weapon in
        /// it at all.
        ///
        /// Selection: Odyssey's unique revolver by defName first; when this install does not have it,
        /// the first def that has the shape that actually breaks - a Graphic_Random graphic whose
        /// variants carry masks (see <see cref="TestSpawnDecision.FixtureCandidate"/>), because a def
        /// without a mask is expected to draw fine and would prove nothing.
        ///
        /// Placement follows the pawn copies: the first player pawn's cell, else the map centre, else
        /// an adjacent usable cell. Every failure is one `test: skip` line - never an exception, never
        /// a load that stops.
        /// </summary>
        private static void SpawnFixture(object map, object playerFaction)
        {
            try
            {
                if (map == null) { Refl.Info(TestSpawnDecision.SkipLine("fixture: no map")); return; }

                object def = FindFixtureDef(out string how);
                if (def == null)
                {
                    Refl.Info(TestSpawnDecision.SkipLine("fixture: no def with " + TestSpawnDecision.RandomGraphicPrefix + " and a mask"));
                    return;
                }

                Refl.Info("testspawn: fixture def=" + (StringOf(def, _fDefName, _pDefName) ?? "?") + " (" + how + ")");

                object made = MakeThing(def, null);
                if (made == null) { Refl.Info(TestSpawnDecision.SkipLine("fixture: MakeThing returned null")); return; }

                object here = PawnCell(map, playerFaction) ?? CenterOf(map);
                if (!PlanCell(here, map, out object target))
                {
                    Refl.Info(TestSpawnDecision.SkipLine("fixture: no placeable cell at " + CellText(here)));
                    DestroyQuietly(made);
                    return;
                }

                try
                {
                    if (!SpawnThing(made, target, map)) throw new Exception("GenSpawn.Spawn not resolvable");
                }
                catch (Exception e)
                {
                    Refl.Info(TestSpawnDecision.SkipLine("fixture: spawn failed: " + e.GetType().Name + " " + e.Message));
                    DestroyQuietly(made);
                    return;
                }

                Refl.Info(FixtureSnapshot(made, def));
            }
            catch (Exception e)
            {
                Refl.Info(TestSpawnDecision.SkipLine("fixture: " + e.GetType().Name + " " + e.Message));
            }
        }

        /// <summary>
        /// The preferred def by name, else the first def that satisfies the shape test. One pass: the
        /// shape candidate is remembered while the rest of the database is still searched for the
        /// name, so the preferred def wins wherever it sits in the list.
        /// </summary>
        private static object FindFixtureDef(out string how)
        {
            how = null;
            object byShape = null;
            string shapeClass = null, shapePath = null;
            int scanned = 0;

            foreach (object def in AllDefs())
            {
                scanned++;

                string name = StringOf(def, _fDefName, _pDefName);
                if (name == TestSpawnDecision.FixtureDefName)
                {
                    how = "by defName, " + scanned + " defs scanned";
                    return def;
                }

                if (byShape != null) continue;   // the first shape match wins; keep looking only for the name

                string cls = GraphicClassName(def);
                if (cls == null) continue;

                string path = TexPathOf(def);
                if (!TestSpawnDecision.FixtureCandidate(cls, MaskExists(path))) continue;

                byShape = def;
                shapeClass = cls;
                shapePath = path;
            }

            if (byShape != null)
            {
                how = "by shape " + shapeClass + " + mask at " + shapePath + ", " + scanned + " defs scanned";
                return byShape;
            }
            return null;
        }

        /// <summary>
        /// Does the content index have a mask for this def's art? A Graphic_Random def builds its
        /// variants as texPath + "A".."Z" (Verse.Graphic_Random.New), and a variant's mask is either
        /// "&lt;variant&gt;_m" (Graphic_Single) or "&lt;variant&gt;_northm" (Graphic_Multi) - so those are what is
        /// probed, with the same silent ContentFinder call the graphics use themselves. The
        /// extension fallback already makes the ".png"-suffixed keys resolvable, so a mask that exists
        /// on disk is found here too. Bounded: four probes per def, and the scan stops at the first hit.
        /// </summary>
        private static bool MaskExists(string texPath)
        {
            if (string.IsNullOrEmpty(texPath) || _finderGet == null) return false;

            if (ContentTexture(texPath + "_m") != null) return true;
            for (char c = 'A'; c <= 'C'; c++)      // three variants is evidence enough
            {
                if (ContentTexture(texPath + c + "_m") != null) return true;
                if (ContentTexture(texPath + c + "_northm") != null) return true;
            }
            return false;
        }

        private static object ContentTexture(string path)
        {
            try { return _finderGet == null ? null : _finderGet.Invoke(null, new object[] { path, false }); }
            catch { return null; }
        }

        private static IEnumerable<object> AllDefs()
        {
            return Enumerate(Read(null, _pAllDefs, null));
        }

        private static string GraphicClassName(object def)
        {
            object gd = _mGraphicDataDef == null ? null : _mGraphicDataDef.Get(def);
            if (gd == null || _mGraphicClass == null) return null;
            object t = _mGraphicClass.Get(gd);
            return t is Type type ? type.Name : null;
        }

        private static string TexPathOf(object def)
        {
            object gd = _mGraphicDataDef == null ? null : _mGraphicDataDef.Get(def);
            return gd == null || _mTexPathOfDef == null ? null : _mTexPathOfDef.Get(gd) as string;
        }

        /// <summary>How many variants the graphic really has - the "5, not 10" question.</summary>
        private static int SubGraphics(object graphic)
        {
            if (graphic == null) return -1;
            Type t = graphic.GetType();
            if (!HasTypeNamed(t, "Graphic_Collection")) return -1;
            object arr = Read(Field(t, "subGraphics"), Prop(t, "subGraphics"), graphic);
            return arr is Array a ? a.Length : -1;
        }

        /// <summary>First player pawn's cell, or null when the map has none.</summary>
        private static object PawnCell(object map, object playerFaction)
        {
            if (map == null || playerFaction == null) return null;
            foreach (object thing in AllThings(map))
            {
                if (!IsPawn(thing)) continue;
                if (!ReferenceEquals(FactionOf(thing), playerFaction)) continue;
                object cell = CellOf(thing, thing);
                if (cell != null) return cell;
            }
            return null;
        }

        private static object CenterOf(object map)
        {
            return map == null || _mCenter == null ? null : _mCenter.Get(map);
        }

        /// <summary>The same cell choice the pawn copies use: here, else a neighbour, else nothing.</summary>
        private static bool PlanCell(object here, object map, out object target)
        {
            target = null;
            if (here == null || map == null) return false;

            bool hereUsable = Usable(here, map);
            object neighbour = hereUsable ? null : FindNeighbour(here, map);
            TestSpawnDecision.Placement placement = TestSpawnDecision.Choose(hereUsable, neighbour != null);
            if (placement == TestSpawnDecision.Placement.None) return false;

            target = placement == TestSpawnDecision.Placement.Here ? here : neighbour;
            return true;
        }

        private static string FixtureSnapshot(object thing, object def)
        {
            object graphic = GraphicOf(thing);
            object tex = TextureOf(graphic, thing);

            int width = 0, height = 0;
            if (tex != null)
            {
                Type tt = tex.GetType();
                width = IntOf(Prop(tt, "width"), null, tex);
                height = IntOf(Prop(tt, "height"), null, tex);
            }

            string uiIcon = UiIconName(def, out object icon);
            bool same = TestSpawnDecision.Same(tex == null, icon == null, ReferenceEquals(tex, icon));

            return TestSpawnDecision.FixtureLine(
                StringOf(def, _fDefName, _pDefName),
                IntOf(_pId, _fId, thing),
                graphic == null ? null : graphic.GetType().Name,
                NameOf(tex), width, height, uiIcon, same,
                GraphicClassName(def), TexPathOf(def), SubGraphics(graphic));
        }

        /// <summary>One line for one thing: what the draw path would actually use for it.</summary>
        private static string SnapshotLine(LineBuilder build, object thing)
        {
            object def = Read(_fDef, null, thing);
            object graphic = GraphicOf(thing);
            object tex = TextureOf(graphic, thing);

            int width = 0, height = 0;
            if (tex != null)
            {
                Type tt = tex.GetType();
                width = IntOf(Prop(tt, "width"), null, tex);
                height = IntOf(Prop(tt, "height"), null, tex);
            }

            string uiIcon = UiIconName(def, out object icon);
            bool same = TestSpawnDecision.Same(tex == null, icon == null, ReferenceEquals(tex, icon));

            return build(StringOf(def, _fDefName, _pDefName),
                         IntOf(_pId, _fId, thing),
                         graphic == null ? null : graphic.GetType().Name,
                         NameOf(tex),
                         width, height,
                         uiIcon,
                         same);
        }

        // ------------------------------------------------------------------ copies

        private static void CopyQuality(object from, object to)
        {
            try
            {
                object oldComp = QualityCompOf(from);
                object newComp = QualityCompOf(to);
                if (oldComp == null || newComp == null) return;

                object quality = Read(null, Prop(oldComp.GetType(), "Quality"), oldComp);
                if (quality == null) return;

                PropertyInfo p = Prop(newComp.GetType(), "Quality");
                if (p != null && p.CanWrite) { p.SetValue(newComp, quality, null); return; }

                // CompQuality also exposes SetQuality(QualityCategory, ArtGenerationContext)
                for (Type c = newComp.GetType(); c != null; c = c.BaseType)
                {
                    foreach (MethodInfo m in c.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                          BindingFlags.Instance))
                    {
                        if (m.Name != "SetQuality") continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length == 0 || ps[0].ParameterType != quality.GetType()) continue;
                        object[] args = Defaults(ps);
                        args[0] = quality;
                        Invoke(m, newComp, args);
                        return;
                    }
                }
            }
            catch (Exception e) { WarnOnce("quality copy: " + e.Message); }
        }

        private static void CopyDurability(object from, object to)
        {
            try
            {
                int hitPoints = IntOf(_pHitPoints, null, from);
                if (hitPoints <= 0) return;                       // the thing has no durability at all

                int max = IntOf(_pMaxHitPoints, null, to);
                if (max <= 0) return;

                PropertyInfo hp = _pHitPoints ?? Prop(to.GetType(), "HitPoints");
                if (hp == null || !hp.CanWrite) return;
                hp.SetValue(to, Math.Min(hitPoints, max), null);
            }
            catch (Exception e) { WarnOnce("durability copy: " + e.Message); }
        }

        // ------------------------------------------------------------------ placement

        private static object PrimaryOf(object pawn)
        {
            object equipment = Read(_fPawnEquipment, _pPawnEquipment, pawn);
            return equipment == null ? null : Read(null, _pPrimary, equipment);
        }

        private static object CellOf(object thing, object pawn)
        {
            return Read(null, _pPositionHeld, thing)
                ?? Read(null, _pPosition, thing)
                ?? Read(null, _pPositionHeld, pawn)
                ?? Read(null, _pPosition, pawn);
        }

        /// <summary>Can a thing stand here? Unknown resolves to "yes" - the spawn call itself is inside a
        /// try/catch, so a wrong guess costs one skip line, not a load.</summary>
        private static bool Usable(object cell, object map)
        {
            if (cell == null || map == null) return false;
            if (_mStandable == null) return true;
            try
            {
                // Verse.GenGrid.Standable(IntVec3, Map) is static: two arguments, no target.
                // IntVec3.Standable(Map) is the fallback shape: the cell is the target.
                object ok = _standableStatic
                    ? Invoke(_mStandable, null, new[] { cell, map })
                    : Invoke(_mStandable, cell, new[] { map });
                return ok is bool b && b;
            }
            catch { return true; }
        }

        /// <summary>First usable neighbour, in a fixed compass order so repeated loads agree.</summary>
        private static object FindNeighbour(object cell, object map)
        {
            if (_cCell == null || _tIntVec3 == null) return null;

            int x = IntOf(Prop(_tIntVec3, "x"), Field(_tIntVec3, "x"), cell);
            int z = IntOf(Prop(_tIntVec3, "z"), Field(_tIntVec3, "z"), cell);

            int[,] offsets = { { 0, 1 }, { 1, 0 }, { 0, -1 }, { -1, 0 }, { 1, 1 }, { 1, -1 }, { -1, 1 }, { -1, -1 } };
            for (int i = 0; i < offsets.GetLength(0); i++)
            {
                object candidate;
                try { candidate = _cCell.Invoke(new object[] { x + offsets[i, 0], 0, z + offsets[i, 1] }); }
                catch { continue; }
                if (Usable(candidate, map)) return candidate;
            }
            return null;
        }

        private static string CellText(object cell)
        {
            if (cell == null || _tIntVec3 == null) return "?";
            return IntOf(Prop(_tIntVec3, "x"), Field(_tIntVec3, "x"), cell) + ","
                 + IntOf(Prop(_tIntVec3, "z"), Field(_tIntVec3, "z"), cell);
        }

        // ------------------------------------------------------------------ thing access

        private static object GraphicOf(object thing)
        {
            object g = Read(null, _pGraphic, thing);
            if (g != null) return g;
            g = Read(_fGraphicInt, null, thing);
            if (g != null) return g;
            return Read(null, _pDefaultGraphic, thing);
        }

        /// <summary>The texture the draw path would use for this instance: MatAt(rot).mainTexture, with
        /// matSingle as the fallback for graphics that ignore rotation.</summary>
        private static object TextureOf(object graphic, object thing)
        {
            if (graphic == null) return null;

            try
            {
                MethodInfo matAt = MatAtOf(graphic.GetType());
                if (matAt != null)
                {
                    ParameterInfo[] ps = matAt.GetParameters();
                    object[] args = Defaults(ps);
                    object rot = Read(null, _pRotation, thing);
                    if (rot != null && ps.Length > 0 && ps[0].ParameterType.IsInstanceOfType(rot)) args[0] = rot;
                    object tex = MainTextureOf(Invoke(matAt, graphic, args));
                    if (tex != null) return tex;
                }
            }
            catch (Exception e) { WarnOnce("matAt: " + e.Message); }

            try
            {
                Type gt = graphic.GetType();
                object tex = MainTextureOf(Read(Field(gt, "matSingle"), Prop(gt, "MatSingle"), graphic));
                if (tex != null) return tex;
            }
            catch (Exception e) { WarnOnce("matSingle: " + e.Message); }

            return null;
        }

        private static MethodInfo MatAtOf(Type graphicType)
        {
            if (graphicType == null) return null;
            string key = graphicType.FullName ?? graphicType.Name;
            if (_matAt.TryGetValue(key, out MethodInfo cached)) return cached;

            MethodInfo found = null;
            for (Type c = graphicType; c != null && found == null; c = c.BaseType)
            {
                foreach (MethodInfo m in c.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "MatAt") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 0 || ps[0].ParameterType.Name != "Rot4") continue;
                    found = m;
                    break;
                }
            }
            _matAt[key] = found;
            return found;
        }

        private static object MainTextureOf(object material)
        {
            if (material == null) return null;
            Type t = material.GetType();
            return Read(Field(t, "mainTexture"), Prop(t, "mainTexture"), material);
        }

        private static object QualityCompOf(object thing)
        {
            foreach (object comp in Enumerate(Read(null, Prop(thing.GetType(), "AllComps"), thing)))
                if (comp != null && HasTypeNamed(comp.GetType(), "CompQuality")) return comp;
            return null;
        }

        private static bool HasTypeNamed(Type t, string name)
        {
            for (Type c = t; c != null; c = c.BaseType)
                if (c.Name == name) return true;
            return false;
        }

        private static object MakeThing(object def, object stuff)
        {
            if (_tThingMaker == null) return null;
            foreach (MethodInfo m in _tThingMaker.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "MakeThing") continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length < 1 || !ps[0].ParameterType.IsInstanceOfType(def)) continue;
                try
                {
                    object[] args = Defaults(ps);
                    args[0] = def;
                    if (ps.Length > 1) args[1] = stuff;
                    return m.Invoke(null, args);
                }
                catch (Exception e) { WarnOnce("MakeThing: " + e.Message); }
            }
            return null;
        }

        private static bool SpawnThing(object thing, object cell, object map)
        {
            if (_tGenSpawn == null || cell == null || map == null) return false;

            MethodInfo best = null;
            foreach (MethodInfo m in _tGenSpawn.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "Spawn") continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length < 3) continue;
                if (!ps[0].ParameterType.IsInstanceOfType(thing)) continue;
                if (ps[1].ParameterType != cell.GetType()) continue;
                if (!ps[2].ParameterType.IsInstanceOfType(map)) continue;
                if (best == null || ps.Length < best.GetParameters().Length) best = m;
            }
            if (best == null) return false;

            object[] args = Defaults(best.GetParameters());
            args[0] = thing;
            args[1] = cell;
            args[2] = map;
            Invoke(best, null, args);
            return true;
        }

        private static void DestroyQuietly(object thing)
        {
            try
            {
                MethodInfo m = SingleParam(thing.GetType(), "Destroy") ?? SingleParam(thing.GetType(), "DeSpawn");
                if (m == null) return;
                Type pt = m.GetParameters()[0].ParameterType;
                Invoke(m, thing, new[] { pt.IsValueType ? Activator.CreateInstance(pt) : null });
            }
            catch (Exception e) { WarnOnce("destroy: " + e.Message); }
        }

        private static MethodInfo SingleParam(Type t, string name)
        {
            for (Type c = t; c != null; c = c.BaseType)
                foreach (MethodInfo m in c.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (m.Name == name && m.GetParameters().Length == 1) return m;
            return null;
        }

        // ------------------------------------------------------------------ accessors

        private static object PlayerFaction()
        {
            return _mOfPlayer == null ? null : _mOfPlayer.Get(null);   // static property: no instance
        }

        private static IEnumerable<object> AllThings(object map)
        {
            object lister = Read(_fListerThings, _pListerThings, map);
            object all = lister == null ? null : Read(_fAllThings, _pAllThings, lister);
            return Enumerate(all);
        }

        private static IEnumerable<object> Enumerate(object collection)
        {
            if (collection == null) yield break;
            if (!(collection is IEnumerable e)) yield break;
            foreach (object o in e) yield return o;
        }

        private static object FactionOf(object thing)
        {
            object f = Read(null, _pFaction, thing);
            if (f != null) return f;
            return Read(_fFaction, null, thing);
        }

        private static bool IsPawn(object thing)
        {
            return _tPawn != null && thing != null && _tPawn.IsInstanceOfType(thing);
        }

        private static string LabelOf(object thing)
        {
            object label = Read(null, _pLabel, thing);
            if (label == null) label = Read(null, _pLabelCap, thing);
            if (label == null) label = Read(_fDefLabel, _pDefLabel, Read(_fDef, null, thing));
            return label as string;
        }

        private static string UiIconName(object def, out object icon)
        {
            icon = null;
            if (def == null) return "null";
            icon = Read(_fUiIcon, _pUiIcon, def);
            if (icon == null) return "null";
            if (_badTex != null && ReferenceEquals(icon, _badTex)) return "BadTex";
            return NameOf(icon);
        }

        private static string NameOf(object texture)
        {
            if (texture == null) return null;
            Type t = texture.GetType();
            object name = Read(Field(t, "name"), Prop(t, "name"), texture);
            return name as string ?? t.Name;
        }

        private static string StringOf(object def, FieldInfo f, PropertyInfo p)
        {
            if (def == null) return null;
            return Read(f, p, def) as string;
        }

        private static int IntOf(PropertyInfo p, FieldInfo f, object o)
        {
            object v = Read(f, p, o);
            if (v is int i) return i;
            try { if (v != null) return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch { }
            return 0;
        }

        private static object Read(FieldInfo f, PropertyInfo p, object o)
        {
            try
            {
                if (f != null) return f.GetValue(o);
                if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
            }
            catch { }
            return null;
        }

        private static object Invoke(MethodInfo m, object target, object[] args)
        {
            try { return m.Invoke(target, args); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
        }

        private static object[] Defaults(ParameterInfo[] ps)
        {
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                ParameterInfo p = ps[i];
                try
                {
                    if (p.HasDefaultValue && p.DefaultValue != null && p.DefaultValue != DBNull.Value &&
                        p.DefaultValue != Type.Missing)
                    {
                        args[i] = p.DefaultValue;
                        continue;
                    }
                }
                catch { }
                args[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
            }
            return args;
        }

        private static FieldInfo Field(Type t, string name)
        {
            if (t == null) return null;
            string key = (t.FullName ?? t.Name) + "::" + name;
            if (_fields.TryGetValue(key, out FieldInfo cached)) return cached;

            FieldInfo found = null;
            for (Type c = t; c != null && found == null; c = c.BaseType)
                found = c.GetField(name, BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static);
            _fields[key] = found;
            return found;
        }

        private static PropertyInfo Prop(Type t, string name)
        {
            if (t == null) return null;
            string key = (t.FullName ?? t.Name) + "::" + name;
            if (_props.TryGetValue(key, out PropertyInfo cached)) return cached;

            PropertyInfo found = null;
            for (Type c = t; c != null && found == null; c = c.BaseType)
            {
                PropertyInfo p = c.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic |
                                                     BindingFlags.Instance | BindingFlags.Static);
                if (p != null && p.GetIndexParameters().Length == 0) found = p;
            }
            _props[key] = found;
            return found;
        }

        // ------------------------------------------------------------------ reflection setup

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            _tThing = Refl.T("Verse.Thing");
            _tDef = Refl.T("Verse.ThingDef");
            _tPawn = Refl.T("Verse.Pawn");
            // The faction class moved to RimWorld in this build: Verse.Faction does not exist, so the
            // 2.6.1 lookup returned null before it ever asked for OfPlayer (docs/REFLECTION-FACTS.md).
            _tFaction = ResolveFactionType();
            _tMap = Refl.T("Verse.Map");
            _tThingMaker = Refl.T("Verse.ThingMaker");
            _tGenSpawn = Refl.T("Verse.GenSpawn");
            _tBaseContent = Refl.T("Verse.BaseContent");

            // OfPlayer is a static, get-only property - prefer the property, and accept a field if a
            // future build turns it back into one. Read-only use, so no writability requirement.
            _mOfPlayer = _tFaction == null ? null : Member.Resolve(_tFaction, "OfPlayer", needWrite: false, preferField: false);
            if (_mOfPlayer == null)
                Refl.Warn("testspawn: OfPlayer neither a field nor a property on "
                          + (_tFaction == null ? "Verse.Faction / RimWorld.Faction (type not found)" : _tFaction.FullName)
                          + " - the load snapshot is disabled");
            else
                Refl.Info("testspawn: OfPlayer resolved as " + _mOfPlayer.Kind + " on " + _mOfPlayer.DeclaringTypeName
                          + (_mOfPlayer.IsStatic ? " (static)" : " (instance)"));

            if (_tDef != null)
            {
                _fDefName = Field(_tDef, "defName");
                _pDefName = Prop(_tDef, "defName");
                _fDefLabel = Field(_tDef, "label");
                _pDefLabel = Prop(_tDef, "label");
                _fUiIcon = Field(_tDef, "uiIcon");
                _pUiIcon = Prop(_tDef, "uiIcon");
            }

            if (_tThing != null)
            {
                _fDef = Field(_tThing, "def");
                _fId = Field(_tThing, "thingIDNumber");
                _pId = Prop(_tThing, "thingIDNumber");
                _fFaction = Field(_tThing, "factionInt");
                _pFaction = Prop(_tThing, "Faction");
                _fGraphicInt = Field(_tThing, "graphicInt");
                _pGraphic = Prop(_tThing, "Graphic");
                _pDefaultGraphic = Prop(_tThing, "DefaultGraphic");
                _pLabel = Prop(_tThing, "Label");
                _pLabelCap = Prop(_tThing, "LabelCap");
                _pRotation = Prop(_tThing, "Rotation");
                _pPosition = Prop(_tThing, "Position");
                _pPositionHeld = Prop(_tThing, "PositionHeld");
                _pStuff = Prop(_tThing, "Stuff");
                _pHitPoints = Prop(_tThing, "HitPoints");
                _pMaxHitPoints = Prop(_tThing, "MaxHitPoints");
                _tIntVec3 = _pPosition == null ? null : _pPosition.PropertyType;
            }

            if (_tPawn != null)
            {
                _fPawnEquipment = Field(_tPawn, "equipment");
                _pPawnEquipment = Prop(_tPawn, "equipment");
                Type eq = _fPawnEquipment == null ? Refl.T("Verse.Pawn_EquipmentTracker")
                                                  : _fPawnEquipment.FieldType;
                if (eq != null)
                {
                    _pPrimary = Prop(eq, "Primary");
                }
            }

            if (_tMap != null)
            {
                _fListerThings = Field(_tMap, "listerThings");
                _pListerThings = Prop(_tMap, "listerThings");
                Type lister = _fListerThings != null ? _fListerThings.FieldType
                             : _pListerThings != null ? _pListerThings.PropertyType : null;
                if (lister != null)
                {
                    _fAllThings = Field(lister, "AllThings");
                    _pAllThings = Prop(lister, "AllThings");
                }
            }

            if (_tIntVec3 != null)
            {
                _cCell = _tIntVec3.GetConstructor(new[] { typeof(int), typeof(int), typeof(int) });
                if (_tMap != null) ResolveStandable();
            }

            if (_tBaseContent != null)
            {
                FieldInfo bad = _tBaseContent.GetField("BadTex", BindingFlags.Public | BindingFlags.Static)
                             ?? _tBaseContent.GetField("BadTex", BindingFlags.NonPublic | BindingFlags.Static);
                if (bad != null) _badTex = bad.GetValue(null);
            }

            // ---- the fixture sample: def graphic data, the whole def list, and the content lookup
            _mGraphicDataDef = _tDef == null ? null : Member.Resolve(_tDef, "graphicData", needWrite: false, preferField: true);
            Type gdType = _mGraphicDataDef == null ? null : _mGraphicDataDef.ValueType;
            if (gdType != null)
            {
                _mGraphicClass = Member.Resolve(gdType, "graphicClass", needWrite: false, preferField: true);
                _mTexPathOfDef = Member.Resolve(gdType, "texPath", needWrite: false, preferField: true);
            }
            if (_tMap != null) _mCenter = Member.Resolve(_tMap, "Center", needWrite: false, preferField: false);

            try
            {
                Type open = Refl.T("Verse.DefDatabase`1");
                if (open != null && _tDef != null) _pAllDefs = Prop(open.MakeGenericType(_tDef), "AllDefs");
            }
            catch (Exception e) { WarnOnce("DefDatabase: " + e.Message); }

            try
            {
                Type tex = Refl.T("UnityEngine.Texture2D") ?? Refl.T("Waylith.Rendering.Texture2D");
                Type open = Refl.T("Verse.ContentFinder`1");
                if (tex != null && open != null)
                {
                    _finderGet = open.MakeGenericType(tex).GetMethod("Get",
                        BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(bool) }, null);
                }
            }
            catch (Exception e) { WarnOnce("ContentFinder: " + e.Message); }

            Refl.Info("testspawn: resolved thing=" + (_tThing != null) + " def=" + (_tDef != null)
                      + " pawn=" + (_tPawn != null) + " map=" + (_tMap != null)
                      + " maker=" + (_tThingMaker != null) + " genspawn=" + (_tGenSpawn != null)
                      + " allthings=" + (_fAllThings != null || _pAllThings != null)
                      + " standable=" + (_mStandable == null ? "none" : _standableStatic ? "static" : "instance")
                      + " cell=" + (_cCell != null)
                      + " faction=" + (_tFaction == null ? "none" : _tFaction.Name)
                      + " ofplayer=" + (_mOfPlayer == null ? "none" : _mOfPlayer.Kind)
                      + " alldefs=" + (_pAllDefs != null)
                      + " maskprobe=" + (_finderGet != null)
                      + " badTex=" + (_badTex != null));
        }

        /// <summary>
        /// Verse.Faction does not exist in this build - the class is RimWorld.Faction. Tried in the
        /// order the game's own sources use, so a future build that moves it back still resolves.
        /// </summary>
        private static Type ResolveFactionType()
        {
            Type t = Refl.T("Verse.Faction") ?? Refl.T("RimWorld.Faction");
            if (t != null) Refl.Info("testspawn: faction type " + t.FullName);
            return t;
        }

        /// <summary>
        /// Standable is Verse.GenGrid.Standable(IntVec3 c, Map map) - a *static* method on a helper
        /// class, not an instance method of IntVec3. 2.6.1 searched only the instance shape, found
        /// nothing, and left the check empty, so every cell counted as usable (the log said
        /// "standable=False" and copies then landed on unwalkable cells). The instance shape is still
        /// probed as a fallback, and a total miss stays fail-open.
        /// </summary>
        private static void ResolveStandable()
        {
            MethodInfo stat = null, instance = null;
            try
            {
                Type genGrid = Refl.T("Verse.GenGrid");
                if (genGrid != null)
                {
                    foreach (MethodInfo m in genGrid.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (m.Name != "Standable") continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length != 2) continue;
                        if (ps[0].ParameterType != _tIntVec3) continue;
                        if (!ps[1].ParameterType.IsAssignableFrom(_tMap)) continue;
                        stat = m;
                        break;
                    }
                }

                if (stat == null)
                {
                    foreach (MethodInfo m in _tIntVec3.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (m.Name != "Standable") continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length != 1) continue;
                        if (!ps[0].ParameterType.IsAssignableFrom(_tMap)) continue;
                        instance = m;
                        break;
                    }
                }
            }
            catch (Exception e) { WarnOnce("standable lookup: " + e.Message); }

            int kind = MemberChoice.Standable(stat != null, instance != null);
            _standableStatic = kind == MemberChoice.StandableStatic;
            _mStandable = _standableStatic ? stat
                        : kind == MemberChoice.StandableInstance ? instance
                        : null;

            if (kind == MemberChoice.StandableStatic)
                Refl.Info("testspawn: standable resolved as static " + stat.DeclaringType.Name + ".Standable("
                          + _tIntVec3.Name + ", " + _tMap.Name + ")");
            else if (kind == MemberChoice.StandableInstance)
                Refl.Info("testspawn: standable resolved as instance " + _tIntVec3.Name + ".Standable(" + _tMap.Name + ")");
            else
                Refl.Warn("testspawn: no Standable(" + _tIntVec3.Name + ", " + _tMap.Name
                          + ") found - every cell is treated as usable");
        }

        private static void WarnOnce(string m)
        {
            if (System.Threading.Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("testspawn: " + m);
        }
    }
}
