using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// v41: repair the two fields the build-menu icon draw depends on, at the moment the icon is drawn.
    ///
    /// Verse.Widgets.ThingIcon(Rect, ThingDef, ...) draws whatever Verse.Widgets.GetIconFor() returns:
    ///
    ///     result   = thingDef.GetUIIconForStuff(stuffDef);      // == thingDef.uiIcon
    ///     material = thingDef.uiIconMaterial;                   // when set
    ///     if (result == null || result == BaseContent.BadTex) return;   // nothing drawn at all
    ///
    /// Both fields are filled once, in BuildableDef.ResolveIcon():
    ///
    ///     uiIconMaterial = MaterialPool.MatFrom(request);
    ///     uiIcon = (Texture2D)val.mainTexture;                  // BuildableDef.cs:333
    ///
    /// On this port Material.mainTexture comes back null, so uiIcon stays null (or BadTex) and the tile
    /// is an empty frame forever; and when the UI material IS created, CutoutComplexUI can discard the
    /// whole tile silently (verified on CI: a non-default _MainTex_ST renders 0 of 16384 pixels).
    ///
    /// This prefix fixes both at draw time - no reliance on ResolveIcon running, on patch ordering, or
    /// on the material path at all:
    ///   1. uiIcon null / BadTex  -> load the graphic's texture from its path and store it on the def
    ///   2. uiIconMaterial is a CutoutComplexUI material -> drop it, so GetIconFor takes the plain path
    ///
    /// Cost. ThingIcon runs for every visible tile every frame, so the prefix must be free once a def
    /// is healthy:
    ///   - an argument that came out drawable (def, or the Thing that owns it) is remembered in
    ///     <see cref="_done"/> by reference identity, so every later call for it is a single hash
    ///     lookup plus a return;
    ///   - the "def" property walk (Thing overloads) runs only when the argument is not a ThingDef;
    ///   - material names are memoised (Material.name is a reflected property walk otherwise).
    /// Every step still fails open: an exception leaves the draw completely untouched.
    /// </summary>
    internal static class IconFieldFix
    {
        // Bounded caches: a def list is a few thousand entries at most. At the cap the whole set is
        // dropped and rebuilt lazily - cheaper and simpler than an LRU, and nothing leaks.
        private const int DoneCap = 4096;
        private const int NameCap = 512;

        private static int _filled, _cleared, _warned;
        // Field or property, instance or static - resolved once per def type by Member.Resolve, which
        // is what 2.6.1 got wrong: it asked for an instance field of *this* type only and treated a
        // null answer as "the game has no such member". docs/REFLECTION-FACTS.md has the metadata.
        private static Member _mIcon, _mMat, _mGraphic, _mGraphicData, _mTexPath, _mPath, _mDefName;
        private static Member _mSubGraphics;
        private static Type _subGraphicsType;
        private static Member _mThingDef;
        private static Type _thingArgType;
        private static Type _defType, _badTexType;
        private static object _badTex;
        private static MethodInfo _finder;
        private static string _mode = IconPath.Plain;

        // defs whose icon/material were left in a drawable state - the steady-state fast path.
        private static readonly HashSet<object> _done = new HashSet<object>(ReferenceEqualityComparer.Instance);
        // material -> Material.name (null is a valid cached answer)
        private static readonly Dictionary<object, string> _names = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        // material type -> its "name" property, so the walk happens once per type
        private static readonly Dictionary<Type, PropertyInfo> _nameProp = new Dictionary<Type, PropertyInfo>();

        // Invalidation. The caches above claim "this def is drawable", but the game rewrites uiIcon
        // back to BadTex whenever play data is rebuilt: LanguageDatabase.SelectLanguage ->
        // PlayDataLoader.ClearAllPlayData -> BuildableDef.PostLoad re-runs ResolveIcon, and dev
        // hot-reload does the same. Without this, every icon cached before a language switch stays
        // blank until the process restarts, and the old defs stay pinned through def->graphic->
        // Material->Texture2D, which is exactly what PlayDataLoader's final GC is trying to collect.
        private static int _nextProbe;                 // Environment.TickCount gate, ~1 ns per call
        private static bool _probeOff, _probeSeen;
        private static object _sentinel;

        internal static void Init(string mode)
        {
            _mode = string.IsNullOrEmpty(mode) ? IconPath.Plain : mode.Trim().ToLowerInvariant();
        }

        [HarmonyPriority(Priority.Low)]   // other mods get the first word on ThingIcon
        internal static void Prefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 2) return;
                object arg = __args[1];
                if (arg == null) return;

                if (unchecked(Environment.TickCount - _nextProbe) >= 0) Probe();   // int compare, ~1 ns

                if (_done.Contains(arg)) return;   // steady state: one hash lookup, nothing else

                object def = arg;
                // Widgets.ThingIcon(Rect, Thing, ...) passes a Thing: repair the ThingDef behind it.
                // Only worth a reflected property walk when the argument is not already a ThingDef -
                // the hot overload then never pays for it.
                if (_defType == null || !_defType.IsInstanceOfType(arg))
                {
                    Type argType = arg.GetType();
                    if (argType != _thingArgType)
                    {
                        _thingArgType = argType;
                        _mThingDef = Member.Resolve(argType, "def", needWrite: false, preferField: true);
                    }
                    object innerDef = _mThingDef == null ? null : _mThingDef.Get(arg);
                    if (innerDef != null && innerDef != arg) def = innerDef;
                    if (def != arg && _done.Contains(def)) { Remember(arg); return; }
                }

                if (_defType != def.GetType()) Resolve(def.GetType());
                if (_mIcon == null) return;   // Resolve logged why; the draw stays untouched

                object icon = _mIcon.Get(def);
                bool ok = icon != null && !IsBad(icon);
                if (!ok) ok = Fill(def);

                if (_mMat != null)
                {
                    object mat = _mMat.Get(def);
                    if (mat != null)
                    {
                        string name = NameOf(mat);
                        if (IconPath.UsePlain(name, _mode))
                        {
                            if (_mMat.Set(def, null))
                            {
                                int c = Interlocked.Increment(ref _cleared);
                                if (c <= 5) Refl.Info("drawfix: dropped icon material '" + name + "' on " + DefName(def));
                            }
                        }
                    }
                }

                // Only something that came out drawable is remembered; a def (or Thing) that could not
                // be repaired is retried on the next frame, the graphic may load later.
                if (ok)
                {
                    Remember(def);
                    if (!ReferenceEquals(def, arg)) Remember(arg);   // Thing.def never changes
                }
            }
            catch (Exception e) { WarnOnce(e.Message); }
        }

        private static void Remember(object def)
        {
            if (_done.Count >= DoneCap) _done.Clear();   // bounded: rebuild instead of growing
            _done.Add(def);
        }

        /// <summary>Load the graphic's texture into uiIcon. True when the value is drawable afterwards.</summary>
        private static bool Fill(object def)
        {
            object graphic = _mGraphic == null ? null : _mGraphic.Get(def);
            string path = null;
            string fallback = null;
            if (graphic != null && _mPath != null)
            {
                fallback = _mPath.Get(graphic) as string;
                path = VariantPath(graphic, fallback);
            }
            if (string.IsNullOrEmpty(path) && _mGraphicData != null && _mTexPath != null)
            {
                object gd = _mGraphicData.Get(def);
                if (gd != null) path = _mTexPath.Get(gd) as string;
            }
            if (string.IsNullOrEmpty(path)) return false;

            object tex = Load(path);
            if (tex == null) tex = Load(path + "_south");
            if (tex == null) return false;

            if (!_mIcon.Set(def, tex))
            {
                WarnOnce("uiIcon is not writable (" + _mIcon.Kind + " without a setter) on " + _defType.Name);
                return false;
            }
            int n = Interlocked.Increment(ref _filled);
            if (n <= 5) Refl.Info("drawfix: uiIcon <- " + path + " on " + DefName(def));
            else if (n % 100 == 0) Refl.Info("drawfix: filled " + n + " icons at draw time");
            return true;
        }

        /// <summary>
        /// Prefer the concrete path of the same variant ResolveIcon uses (subGraphics[0] after
        /// CollectionFix removed masks), then fall back to the graphic's own path.
        /// </summary>
        private static string VariantPath(object graphic, string fallback)
        {
            Type graphicType = graphic.GetType();
            if (graphicType != _subGraphicsType)
            {
                _subGraphicsType = graphicType;
                _mSubGraphics = Member.Resolve(graphicType, "subGraphics", needWrite: false, preferField: true);
            }
            object value = _mSubGraphics == null ? null : _mSubGraphics.Get(graphic);
            Array array = value as Array;
            if (array == null || array.Length == 0 || _mPath == null) return fallback;

            var paths = new List<string>(array.Length);
            for (int i = 0; i < array.Length; i++)
            {
                object sub = array.GetValue(i);
                if (sub == null) continue;
                paths.Add(_mPath.Get(sub) as string);
            }
            return IconVariantPath.Choose(paths, fallback);
        }

        private static object Load(string path)
        {
            try { return _finder == null ? null : _finder.Invoke(null, new object[] { path, false }); }
            catch { return null; }
        }

        private static bool IsBad(object tex)
        {
            if (_badTex == null) return false;
            return ReferenceEquals(tex, _badTex);
        }

        private static string DefName(object def)
        {
            return _mDefName == null ? "?" : (_mDefName.Get(def) as string) ?? "?";
        }

        /// <summary>Material.name, memoised per material object: the uncached form walks GetProperties().</summary>
        private static string NameOf(object mat)
        {
            string name;
            if (_names.TryGetValue(mat, out name)) return name;

            name = ReadName(mat);
            if (_names.Count >= NameCap) _names.Clear();   // bounded: rebuild instead of growing
            _names[mat] = name;
            return name;
        }

        private static string ReadName(object mat)
        {
            try
            {
                Type t = mat.GetType();
                PropertyInfo p;
                if (!_nameProp.TryGetValue(t, out p))
                {
                    foreach (PropertyInfo c in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        if (c.Name == "name" && c.GetIndexParameters().Length == 0) { p = c; break; }
                    _nameProp[t] = p;
                }
                return p == null ? null : p.GetValue(mat, null) as string;
            }
            catch { return null; }
        }

        private static void Resolve(Type defType)
        {
            _defType = defType;

            // Field first: on this port every one of these is a public instance field (declared on
            // Verse.BuildableDef / Verse.Def, i.e. on a base type of the runtime type). A property is
            // accepted when a future build moves one, and a writable member is required where the fix
            // writes (uiIcon, uiIconMaterial).
            _mIcon = Member.Resolve(defType, "uiIcon", needWrite: true, preferField: true);
            _mMat = Member.Resolve(defType, "uiIconMaterial", needWrite: true, preferField: true);
            _mGraphic = Member.Resolve(defType, "graphic", needWrite: false, preferField: true);
            _mGraphicData = Member.Resolve(defType, "graphicData", needWrite: false, preferField: true);
            _mDefName = Member.Resolve(defType, "defName", needWrite: false, preferField: true);

            if (_mIcon == null)
                Refl.Warn("drawfix: uiIcon neither a writable field nor a writable property on "
                          + defType.Name + " - draw-time repair disabled");
            else
                Refl.Info("drawfix: uiIcon resolved as " + _mIcon.Kind + " on " + _mIcon.DeclaringTypeName
                          + (_mIcon.IsStatic ? " (static)" : "") + (_mIcon.CanWrite ? " (writable)" : " (read-only)"));

            if (_mMat == null)
                Refl.Info("drawfix: uiIconMaterial not writable on " + defType.Name + " - material drop disabled");
            else
                Refl.Info("drawfix: uiIconMaterial resolved as " + _mMat.Kind + " on " + _mMat.DeclaringTypeName);

            try
            {
                Type gdType = _mGraphicData == null ? null : _mGraphicData.ValueType;
                if (gdType != null) _mTexPath = Member.Resolve(gdType, "texPath", needWrite: false, preferField: true);
                Type gType = _mGraphic == null ? null : _mGraphic.ValueType;
                if (gType != null) _mPath = Member.Resolve(gType, "path", needWrite: false, preferField: true);
            }
            catch { }

            try
            {
                _badTexType = Refl.T("Verse.BaseContent");
                if (_badTexType != null)
                {
                    FieldInfo f = _badTexType.GetField("BadTex", BindingFlags.Public | BindingFlags.Static)
                                  ?? _badTexType.GetField("BadTex", BindingFlags.NonPublic | BindingFlags.Static);
                    if (f != null) _badTex = f.GetValue(null);
                }
            }
            catch { }

            try
            {
                Type open = Type.GetType("Verse.ContentFinder`1, Assembly-CSharp");
                Type texType = Refl.T("Waylith.Rendering.Texture2D");
                if (open != null && texType != null)
                {
                    Type closed = open.MakeGenericType(texType);
                    _finder = closed.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(string), typeof(bool) }, null);
                }
            }
            catch (Exception e) { WarnOnce("ContentFinder: " + e.Message); }
        }

        private static void WarnOnce(string m)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("drawfix: " + m);
        }

        /// <summary>Drop every cached verdict. Cheap: two container clears.</summary>
        private static void ClearCaches(string why)
        {
            _done.Clear();
            _names.Clear();
            Refl.Info("drawfix: cache cleared (" + why + ")");
        }

        /// <summary>Postfix for PlayDataLoader.ClearAllPlayData / HotReloadDefs.</summary>
        private static void OnPlayDataReload()
        {
            try { ClearCaches("play data reload"); } catch (Exception e) { WarnOnce(e.Message); }
        }

        /// <summary>
        /// Fallback for the case where the reload hooks could not be installed: once every 250 ms,
        /// compare a surviving def instance. A rebuilt DefDatabase hands out a different object, so
        /// the caches are dropped. Never throws into the draw path; on any failure it switches
        /// itself off for the rest of the process.
        /// </summary>
        private static void Probe()
        {
            _nextProbe = unchecked(Environment.TickCount + 250);
            if (_probeOff) return;
            try
            {
                object s = Sentinel();
                if (s == null) return;                       // cannot observe: rely on the hooks
                if (!ReferenceEquals(s, _sentinel))
                {
                    if (_probeSeen) ClearCaches("sentinel changed");
                    _sentinel = s;
                    _probeSeen = true;
                }
            }
            catch (Exception e) { _probeOff = true; WarnOnce("probe disabled: " + e.Message); }
        }

        private static object Sentinel()
        {
            if (_defDbGet == null) return null;
            return _defDbGet.Invoke(null, new object[] { "Steel" });   // 4x/s at most
        }

        private static MethodInfo _defDbGet;

        private static void InstallInvalidation()
        {
            // 1) direct hooks on the two reload entry points
            Type loader = Refl.T("Verse.PlayDataLoader");
            MethodInfo post = typeof(IconFieldFix).GetMethod("OnPlayDataReload",
                BindingFlags.Static | BindingFlags.NonPublic);
            int hooked = 0;
            if (loader != null)
            {
                foreach (MethodInfo m in loader.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.Name != "ClearAllPlayData" && m.Name != "HotReloadDefs") continue;
                    if (m.GetParameters().Length != 0) continue;
                    try
                    {
                        new Harmony("sleepsheep.waylith.agenttouchfix.drawfix.reload")
                            .Patch(m, postfix: new HarmonyMethod(post));
                        hooked++;
                    }
                    catch (Exception e) { Refl.Warn("drawfix: hook " + m.Name + " failed: " + e.Message); }
                }
            }
            Refl.Info("drawfix: invalidation hooks on " + hooked + " PlayDataLoader method(s)");

            // 2) sentinel for the fallback probe: DefDatabase<ThingDef>.GetNamedSilentFail("Steel")
            try
            {
                Type open = null;
                foreach (Type t in _defType.Assembly.GetTypes())
                {
                    if (t.Name == "DefDatabase`1" && t.Namespace == "Verse") { open = t; break; }
                }
                if (open != null)
                {
                    Type closed = open.MakeGenericType(_defType);
                    _defDbGet = closed.GetMethod("GetNamedSilentFail",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }
                Refl.Info("drawfix: sentinel " + (_defDbGet == null ? "unavailable" : "ready"));
            }
            catch (Exception e) { Refl.Warn("drawfix: sentinel setup failed: " + e.Message); }
        }

        internal static void Install()
        {
            if (_defType == null) _defType = Refl.T("Verse.ThingDef");
            Type widgets = Refl.T("Verse.Widgets");
            if (widgets == null) { Refl.Warn("drawfix: Verse.Widgets not found"); return; }

            var targets = new List<MethodInfo>();
            foreach (MethodInfo m in widgets.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "ThingIcon") continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length < 2 || ps[0].ParameterType.Name != "Rect") continue;
                if (ps[1].ParameterType.Name == "ThingDef" || ps[1].ParameterType.Name == "Thing")
                    targets.Add(m);
            }
            if (targets.Count == 0) { Refl.Warn("drawfix: no ThingIcon(Rect, Thing|ThingDef, ...) overloads found"); return; }

            try
            {
                MethodInfo pre = typeof(IconFieldFix).GetMethod("Prefix",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

                foreach (MethodInfo t in targets)
                    new Harmony("sleepsheep.waylith.agenttouchfix.drawfix").Patch(t, new HarmonyMethod(pre));
                Refl.Info("drawfix: patched " + targets.Count + " ThingIcon overload(s)");
                Refl.Info("drawfix: prefix on Verse.Widgets.ThingIcon (uiIcon repaired, icon material dropped)");
            }
            catch (Exception e) { Refl.Warn("drawfix: patch failed: " + e.Message); }

            InstallInvalidation();
        }

        /// <summary>Reference identity: game types may override Equals/GetHashCode, and these are two
        /// different fields on one object, so identity is the only key that means what we want.</summary>
        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            bool IEqualityComparer<object>.Equals(object a, object b) { return ReferenceEquals(a, b); }
            int IEqualityComparer<object>.GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); }
        }
    }
}
