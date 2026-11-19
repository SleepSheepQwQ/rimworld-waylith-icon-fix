using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// Diagnostic module, strictly read-only.
    ///
    /// After a save is loaded it walks every map's <c>mapPawns.AllPawnsSpawned</c>, keeps the pawns
    /// whose faction is <c>Faction.OfPlayer</c>, and prints one <c>test: thing ...</c> line per thing:
    /// the graphic the instance really carries, the texture the draw path would pick off it, the
    /// def's <c>uiIcon</c>, whether those two are the same object, and - for a collection graphic -
    /// how many sub-graphics it has. That is the evidence an instance-level icon split needs.
    ///
    /// The module never creates, deletes, moves or edits anything in the world. Version 2.6.2 built a
    /// Thing inside the save-load window (a unique-weapon sample) and the game died on
    /// <c>libcoreclr SIGSEGV, Thread-9</c>; a diagnostic must never be able to cost a load, and the
    /// only version of that promise which can be checked is "there is no write path at all". The CI
    /// workflow greps this file and <see cref="TestSpawnDecision"/> for the forbidden members, so the
    /// constraint is enforced by the build, not by intent.
    ///
    /// Rules, all deliberate:
    ///   * Read-only: every member below is resolved for reading and invoked for reading. Nothing is
    ///     set, nothing is added, nothing is taken away.
    ///   * Gate: the save-load path only - <c>Root_Play.LoadedGame</c>, else <c>Game.LoadGame</c>,
    ///     else <c>SavedGameLoaderNow</c>. A freshly generated map never goes through any of these,
    ///     which is exactly the distinction <c>Map.FinalizeInit</c> cannot make.
    ///   * Deferred: with a tick hook available the walk runs one tick after the load callback, not
    ///     inside it, so the deserializer has finished touching the map.
    ///   * Filter: <c>p.Faction</c> against <c>Faction.OfPlayer</c> by reference. A null player faction
    ///     simply matches nothing - the walk never assumes the property is populated.
    ///   * Budget: at most <see cref="TestSpawnDecision.LineCap"/> lines per load. Zero player pawns
    ///     writes exactly one line, <c>test: skip no-player-pawns</c>.
    ///   * Every entry point is wrapped in try/catch, and the instance walk is materialised before it
    ///     is iterated so a collection changing under the reader can never throw mid-line.
    /// </summary>
    internal static class TestSpawn
    {
        private const string HarmonyId = "sleepsheep.waylith.agenttouchfix.testspawn";
        private const int LineCap = TestSpawnDecision.LineCap;

        private static bool _resolved;
        private static int _warned;

        private static bool _gateOpen;     // a save was loaded
        private static bool _done;         // the walk for that load was taken
        private static bool _tickHooked;

        // ---- types ------------------------------------------------------------------------------
        private static Type _tThing, _tDef, _tPawn, _tFaction, _tMap, _tFind, _tBaseContent;

        // ---- Verse.Thing ------------------------------------------------------------------------
        private static FieldInfo _fDef, _fId, _fFaction, _fGraphicInt, _fPawnEquipment;
        private static PropertyInfo _pId, _pGraphic, _pDefaultGraphic, _pRotation, _pFaction,
                                 _pPawnEquipment;

        // ---- Verse.ThingDef / Verse.BuildableDef ------------------------------------------------
        private static FieldInfo _fDefName, _fUiIcon;
        private static PropertyInfo _pDefName, _pUiIcon;

        // ---- maps and pawns ---------------------------------------------------------------------
        private static FieldInfo _fMapPawns, _fPawnsSpawned;
        private static PropertyInfo _pMapPawns, _pPawnsSpawned, _pMaps, _pCurrentMap;

        // ---- equipment --------------------------------------------------------------------------
        private static PropertyInfo _pPrimary;

        // ---- player faction ----------------------------------------------------------------------
        // RimWorld.Faction.OfPlayer is a static, get-only *property* (docs/REFLECTION-FACTS.md), and
        // the type lives in RimWorld, not Verse - both were assumed wrong in 2.6.1.
        private static Member _mOfPlayer;

        // ---- caches -----------------------------------------------------------------------------
        private static readonly Dictionary<string, FieldInfo> _fields = new Dictionary<string, FieldInfo>();
        private static readonly Dictionary<string, PropertyInfo> _props = new Dictionary<string, PropertyInfo>();
        private static readonly Dictionary<string, MethodInfo> _matAt = new Dictionary<string, MethodInfo>();

        private static object _badTex;

        // ------------------------------------------------------------------ install

        internal static void Install()
        {
            try
            {
                // The startup line, first and unconditional: a reader must be able to tell a
                // read-only build from 2.6.2 without reading the changelog.
                Refl.Info("testspawn: read-only mode (no objects created)");

                Resolve();

                var harmony = new Harmony(HarmonyId);
                MethodInfo loaded = LoadCallback();
                if (loaded == null)
                {
                    // Without a load-only callback there is no way to tell a loaded save from a new
                    // map, and the module refuses to guess: nothing is patched, nothing runs.
                    Refl.Warn("testspawn: no load callback found, module inactive");
                    return;
                }

                harmony.Patch(loaded, postfix: new HarmonyMethod(
                    typeof(TestSpawn).GetMethod("OnLoadCallback", BindingFlags.Static | BindingFlags.NonPublic)));
                Refl.Info("testspawn: gated on " + loaded.DeclaringType?.Name + "." + loaded.Name);

                _tickHooked = PatchTick(harmony);
                Refl.Info("testspawn: tick hooks=" + (_tickHooked ? 1 : 0)
                          + (_tickHooked ? "" : " (the walk runs inside the load callback)"));
            }
            catch (Exception e) { WarnOnce("install: " + e.Message); }
        }

        /// <summary>
        /// The gate: a callback that only ever runs when a save is loaded. A freshly generated map never
        /// goes through any of these. Tried in order of specificity; the first one the port has wins.
        /// </summary>
        private static MethodInfo LoadCallback()
        {
            var candidates = new[]
            {
                new[] { "Verse.Root_Play", "LoadedGame" },                        // the post-load moment
                new[] { "Verse.Game", "LoadGame" },                               // the deserializer itself
                new[] { "Verse.SavedGameLoaderNow", "LoadGameFromSaveFileNow" },  // oldest of the three
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

        /// <summary>One cheap per-tick postfix, used only to run the walk one tick after the load
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
                Refl.Info("testspawn: save loaded, walk armed");

                // No tick hook: the load callback is still the right moment - it runs after
                // FinalizeInit and after every thing is in place. Reading needs no later moment.
                if (!_tickHooked) TryRun();
            }
            catch (Exception e) { WarnOnce("gate: " + e.Message); }
        }

        private static void TickWithMap(object __instance)
        {
            try { TryRun(); }
            catch (Exception e) { WarnOnce("tick: " + e.Message); }
        }

        private static void TryRun()
        {
            if (!_gateOpen || _done) return;
            _done = true;
            Run();
        }

        // ------------------------------------------------------------------ the walk

        private static void Run()
        {
            try
            {
                object player = PlayerFaction();
                int maps = 0, pawns = 0, lines = 0;
                bool capped = false;

                foreach (object map in AllMaps())
                {
                    maps++;
                    foreach (object pawn in PlayerPawns(map, player))
                    {
                        pawns++;

                        if (!Log(pawn, ref lines)) continue;      // the pawn itself
                        if (!TestSpawnDecision.HasRoom(lines, LineCap)) { capped = true; break; }

                        object weapon = PrimaryOf(pawn);           // and what it is holding, if anything
                        if (weapon != null) Log(weapon, ref lines);
                        if (!TestSpawnDecision.HasRoom(lines, LineCap)) { capped = true; break; }
                    }
                    if (capped) break;
                }

                if (pawns == 0 && lines == 0)
                {
                    // Not an error and not worth a second line: this save simply has no player pawn
                    // yet (a fresh load of the main menu, an empty colony, a null player faction).
                    Refl.Info(TestSpawnDecision.SkipLine(TestSpawnDecision.NoPlayerPawns));
                    return;
                }

                if (capped) Refl.Info(TestSpawnDecision.SkipLine("line cap reached cap=" + LineCap));
                Refl.Info("testspawn: done maps=" + maps + " pawns=" + pawns + " lines=" + lines);
            }
            catch (Exception e) { WarnOnce("run: " + e.Message); }
        }

        /// <summary>
        /// One thing, one line, or nothing at all: a thing that cannot be described is skipped rather
        /// than printed half-populated, and a failure here never touches the load.
        /// </summary>
        private static bool Log(object thing, ref int lines)
        {
            try
            {
                if (thing == null) return false;
                if (!TestSpawnDecision.HasRoom(lines, LineCap)) return false;

                string line = SnapshotLine(thing);
                if (line == null) return false;

                Refl.Info(line);
                lines++;
                return true;
            }
            catch (Exception e)
            {
                WarnOnce("line: " + e.GetType().Name + " " + e.Message);
                return false;
            }
        }

        /// <summary>One line for one thing: what the draw path would actually use for it.</summary>
        private static string SnapshotLine(object thing)
        {
            object def = Read(_fDef, null, thing);
            if (def == null) return null;                 // no def, nothing to compare against

            object graphic = GraphicOf(thing);
            object tex = TextureOf(graphic, thing);

            int width = 0, height = 0;
            if (tex != null)
            {
                Type tt = tex.GetType();
                width = IntOf(Prop(tt, "width"), Field(tt, "width"), tex);
                height = IntOf(Prop(tt, "height"), Field(tt, "height"), tex);
            }

            string uiIcon = UiIconName(def, out object icon);
            bool same = TestSpawnDecision.Same(tex == null, icon == null, ReferenceEquals(tex, icon));

            return TestSpawnDecision.ThingLine(
                StringOf(def, _fDefName, _pDefName),
                IntOf(_pId, _fId, thing),
                graphic == null ? null : graphic.GetType().Name,
                NameOf(tex), width, height, uiIcon, same,
                SubGraphics(graphic));
        }

        /// <summary>
        /// How many variants the graphic really has - the "5, not 10" question. <c>subGraphics</c>
        /// exists only on <c>Graphic_Collection</c>, so the class is matched by name up the hierarchy
        /// and every other graphic passes -1, which prints "-" instead of a misleading 0.
        /// </summary>
        private static int SubGraphics(object graphic)
        {
            try
            {
                if (graphic == null) return -1;
                Type t = graphic.GetType();
                if (!HasTypeNamed(t, "Graphic_Collection")) return -1;
                object arr = Read(Field(t, "subGraphics"), Prop(t, "subGraphics"), graphic);
                return arr is Array a ? a.Length : -1;
            }
            catch { return -1; }
        }

        // ------------------------------------------------------------------ maps and pawns

        /// <summary>Every loaded map. <c>Verse.Find.Maps</c> is the whole colony; <c>CurrentMap</c> is
        /// the fallback for a build where the list is named differently.</summary>
        private static List<object> AllMaps()
        {
            var list = new List<object>();
            try
            {
                foreach (object m in Enumerate(Read(null, _pMaps, null)))
                    if (m != null) list.Add(m);

                if (list.Count == 0)
                {
                    object current = Read(null, _pCurrentMap, null);
                    if (current != null) list.Add(current);
                }
            }
            catch (Exception e) { WarnOnce("maps: " + e.Message); }
            return list;
        }

        /// <summary>
        /// <c>map.mapPawns.AllPawnsSpawned</c>, copied out first. The copy matters: the walk runs on the
        /// main thread right after a load, but a list read while the game is still filling it would
        /// throw; a snapshot cannot.
        /// </summary>
        private static List<object> PawnsOn(object map)
        {
            var list = new List<object>();
            try
            {
                object mapPawns = Read(_fMapPawns, _pMapPawns, map);
                if (mapPawns == null) return list;
                object all = Read(_fPawnsSpawned, _pPawnsSpawned, mapPawns);
                foreach (object p in Enumerate(all))
                    if (p != null) list.Add(p);
            }
            catch (Exception e) { WarnOnce("mapPawns: " + e.Message); }
            return list;
        }

        /// <summary>
        /// The player's own pawns: <c>p.Faction</c> against <c>Faction.OfPlayer</c>, by reference. When
        /// <c>OfPlayer</c> could not be read at all the comparison is against null, which matches no
        /// pawn - the walk never falls back to "everything" and never assumes the property is set.
        /// </summary>
        private static IEnumerable<object> PlayerPawns(object map, object player)
        {
            foreach (object pawn in PawnsOn(map))
            {
                if (!IsPawn(pawn)) continue;
                if (!ReferenceEquals(FactionOf(pawn), player)) continue;
                yield return pawn;
            }
        }

        private static object PrimaryOf(object pawn)
        {
            try
            {
                object equipment = Read(_fPawnEquipment, _pPawnEquipment, pawn);
                return equipment == null ? null : Read(null, _pPrimary, equipment);
            }
            catch (Exception e) { WarnOnce("equipment: " + e.Message); return null; }
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

        /// <summary>
        /// The texture the draw path would use for this instance: <c>MatAt(rot, thing).mainTexture</c>,
        /// then <c>MatSingle</c>, then the material fields the concrete graphics keep
        /// (<c>Graphic_Single.mat</c>, <c>Graphic_Multi.mats[]</c>) for a graphic that answers none of
        /// the accessors. Every step is a read; nothing is cached, set or replaced.
        /// </summary>
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
                    // MatAt(rot, thing): the thing is what the game itself passes, and a graphic that
                    // looks at it would throw on null.
                    if (ps.Length > 1 && thing != null && ps[1].ParameterType.IsInstanceOfType(thing)) args[1] = thing;
                    object tex = MainTextureOf(Invoke(matAt, graphic, args));
                    if (tex != null) return tex;
                }
            }
            catch (Exception e) { WarnOnce("matAt: " + e.Message); }

            try
            {
                // MatSingle is a property on this port (Verse.Graphic.get_MatSingle); the lower-case
                // field name is kept as the fallback for a build that stores it instead.
                Type gt = graphic.GetType();
                object tex = MainTextureOf(Read(Field(gt, "matSingle"), Prop(gt, "MatSingle"), graphic));
                if (tex != null) return tex;
            }
            catch (Exception e) { WarnOnce("matSingle: " + e.Message); }

            try
            {
                Type gt = graphic.GetType();
                object mat = Read(Field(gt, "mat"), null, graphic);       // Graphic_Single.mat
                object tex = MainTextureOf(mat);
                if (tex != null) return tex;

                object mats = Read(Field(gt, "mats"), null, graphic);     // Graphic_Multi.mats[0]
                foreach (object m in Enumerate(mats))
                {
                    tex = MainTextureOf(m);
                    if (tex != null) return tex;
                }
            }
            catch (Exception e) { WarnOnce("mat fields: " + e.Message); }

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

        private static bool HasTypeNamed(Type t, string name)
        {
            for (Type c = t; c != null; c = c.BaseType)
                if (c.Name == name) return true;
            return false;
        }

        private static bool IsPawn(object thing)
        {
            return _tPawn != null && thing != null && _tPawn.IsInstanceOfType(thing);
        }

        private static object FactionOf(object thing)
        {
            object f = Read(null, _pFaction, thing);
            if (f != null) return f;
            return Read(_fFaction, null, thing);
        }

        // ------------------------------------------------------------------ accessors

        private static object PlayerFaction()
        {
            try { return _mOfPlayer == null ? null : _mOfPlayer.Get(null); }  // static property: no instance
            catch (Exception e) { WarnOnce("OfPlayer: " + e.Message); return null; }
        }

        private static IEnumerable<object> Enumerate(object collection)
        {
            if (collection == null) yield break;
            if (!(collection is IEnumerable e)) yield break;
            foreach (object o in e) yield return o;
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
            _tMap = Refl.T("Verse.Map");
            _tFind = Refl.T("Verse.Find");
            _tBaseContent = Refl.T("Verse.BaseContent");
            // The faction class moved to RimWorld in this build: Verse.Faction does not exist, so the
            // 2.6.1 lookup returned null before it ever asked for OfPlayer (docs/REFLECTION-FACTS.md).
            _tFaction = ResolveFactionType();

            // OfPlayer is a static, get-only property - prefer the property, and accept a field if a
            // future build turns it back into one. Read-only use, so no writability requirement.
            _mOfPlayer = _tFaction == null ? null : Member.Resolve(_tFaction, "OfPlayer", needWrite: false, preferField: false);
            if (_mOfPlayer == null)
                Refl.Warn("testspawn: OfPlayer neither a field nor a property on "
                          + (_tFaction == null ? "Verse.Faction / RimWorld.Faction (type not found)" : _tFaction.FullName)
                          + " - no pawn can match the player faction");
            else
                Refl.Info("testspawn: OfPlayer resolved as " + _mOfPlayer.Kind + " on " + _mOfPlayer.DeclaringTypeName
                          + (_mOfPlayer.IsStatic ? " (static)" : " (instance)"));

            if (_tDef != null)
            {
                _fDefName = Field(_tDef, "defName");
                _pDefName = Prop(_tDef, "defName");
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
                _pRotation = Prop(_tThing, "Rotation");
            }

            if (_tPawn != null)
            {
                _fPawnEquipment = Field(_tPawn, "equipment");
                _pPawnEquipment = Prop(_tPawn, "equipment");
                Type eq = _fPawnEquipment == null ? Refl.T("Verse.Pawn_EquipmentTracker")
                                                  : _fPawnEquipment.FieldType;
                if (eq != null) _pPrimary = Prop(eq, "Primary");
            }

            if (_tMap != null)
            {
                _fMapPawns = Field(_tMap, "mapPawns");
                _pMapPawns = Prop(_tMap, "mapPawns");
                Type mp = _fMapPawns != null ? _fMapPawns.FieldType
                        : _pMapPawns != null ? _pMapPawns.PropertyType : null;
                if (mp != null)
                {
                    _fPawnsSpawned = Field(mp, "pawnsSpawned");   // the backing list, when it is a field
                    _pPawnsSpawned = Prop(mp, "AllPawnsSpawned");
                }
            }

            if (_tFind != null)
            {
                _pMaps = Prop(_tFind, "Maps");
                _pCurrentMap = Prop(_tFind, "CurrentMap");
            }

            if (_tBaseContent != null)
            {
                FieldInfo bad = _tBaseContent.GetField("BadTex", BindingFlags.Public | BindingFlags.Static)
                             ?? _tBaseContent.GetField("BadTex", BindingFlags.NonPublic | BindingFlags.Static);
                if (bad != null) _badTex = bad.GetValue(null);
            }

            Refl.Info("testspawn: resolved thing=" + (_tThing != null) + " def=" + (_tDef != null)
                      + " pawn=" + (_tPawn != null) + " map=" + (_tMap != null)
                      + " find=" + (_tFind != null)
                      + " mappawns=" + (_fMapPawns != null || _pMapPawns != null)
                      + " allpawnsspawned=" + (_fPawnsSpawned != null || _pPawnsSpawned != null)
                      + " maps=" + (_pMaps != null)
                      + " faction=" + (_tFaction == null ? "none" : _tFaction.Name)
                      + " ofplayer=" + (_mOfPlayer == null ? "none" : _mOfPlayer.Kind)
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

        private static void WarnOnce(string m)
        {
            if (System.Threading.Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("testspawn: " + m);
        }
    }
}
