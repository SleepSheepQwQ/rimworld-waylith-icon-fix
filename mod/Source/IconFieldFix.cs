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
    ///   - a def that came out healthy is remembered in <see cref="_done"/> (reference identity) and
    ///     every later call for it is a single hash lookup plus a return;
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
        private static FieldInfo _fIcon, _fMat, _fGraphic, _fGraphicData, _fTexPath, _fPath, _fDefName;
        private static Type _defType, _badTexType;
        private static object _badTex;
        private static MethodInfo _finder;
        private static string _mode = IconPath.Plain;

        // defs whose icon/material were left in a drawable state - the steady-state fast path.
        private static readonly HashSet<object> _done = new HashSet<object>(RefEq.Instance);
        // material -> Material.name (null is a valid cached answer)
        private static readonly Dictionary<object, string> _names = new Dictionary<object, string>(RefEq.Instance);
        // material type -> its "name" property, so the walk happens once per type
        private static readonly Dictionary<Type, PropertyInfo> _nameProp = new Dictionary<Type, PropertyInfo>();

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
                object def = __args[1];
                if (def == null) return;

                // Widgets.ThingIcon(Rect, Thing, ...) passes a Thing: repair the ThingDef behind it.
                // Only worth a reflected property walk when the argument is not already a ThingDef -
                // the hot overload then never pays for it.
                if (def.GetType().Name != "ThingDef")
                {
                    object innerDef = Prop(def, "def");
                    if (innerDef != null && innerDef != def) def = innerDef;
                }

                if (_done.Contains(def)) return;   // steady state: one hash lookup, nothing else

                if (_defType != def.GetType()) Resolve(def.GetType());
                if (_fIcon == null) { WarnOnce("uiIcon field not found on " + def.GetType().Name); return; }

                object icon = _fIcon.GetValue(def);
                bool ok = icon != null && !IsBad(icon);
                if (!ok) ok = Fill(def);

                if (_fMat != null)
                {
                    object mat = _fMat.GetValue(def);
                    if (mat != null)
                    {
                        string name = NameOf(mat);
                        if (IconPath.UsePlain(name, _mode))
                        {
                            _fMat.SetValue(def, null);
                            int c = Interlocked.Increment(ref _cleared);
                            if (c <= 5) Refl.Info("drawfix: dropped icon material '" + name + "' on " + DefName(def));
                        }
                    }
                }

                // Only a def that came out drawable is remembered; one that could not be repaired is
                // retried on the next frame (the graphic may load later).
                if (ok) Remember(def);
            }
            catch (Exception e) { WarnOnce(e.Message); }
        }

        private static void Remember(object def)
        {
            if (_done.Count >= DoneCap) _done.Clear();   // bounded: rebuild instead of growing
            _done.Add(def);
        }

        /// <summary>Load the graphic's texture into uiIcon. True when the field is drawable afterwards.</summary>
        private static bool Fill(object def)
        {
            object graphic = _fGraphic == null ? null : _fGraphic.GetValue(def);
            string path = null;
            if (graphic != null && _fPath != null) path = _fPath.GetValue(graphic) as string;
            if (string.IsNullOrEmpty(path) && _fGraphicData != null && _fTexPath != null)
            {
                object gd = _fGraphicData.GetValue(def);
                if (gd != null) path = _fTexPath.GetValue(gd) as string;
            }
            if (string.IsNullOrEmpty(path)) return false;

            object tex = Load(path);
            if (tex == null) tex = Load(path + "_south");
            if (tex == null) return false;

            _fIcon.SetValue(def, tex);
            int n = Interlocked.Increment(ref _filled);
            if (n <= 5) Refl.Info("drawfix: uiIcon <- " + path + " on " + DefName(def));
            else if (n % 100 == 0) Refl.Info("drawfix: filled " + n + " icons at draw time");
            return true;
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
            return _fDefName == null ? "?" : (_fDefName.GetValue(def) as string) ?? "?";
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
            _fIcon = Field(defType, "uiIcon");
            _fMat = Field(defType, "uiIconMaterial");
            _fGraphic = Field(defType, "graphic");
            _fGraphicData = Field(defType, "graphicData");
            _fDefName = Field(defType, "defName");

            try
            {
                Type gdType = _fGraphicData == null ? null : _fGraphicData.FieldType;
                if (gdType != null) _fTexPath = Field(gdType, "texPath");
                Type gType = _fGraphic == null ? null : _fGraphic.FieldType;
                if (gType != null) _fPath = Field(gType, "path");
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

        private static FieldInfo Field(Type t, string name)
        {
            for (Type c = t; c != null; c = c.BaseType)
            {
                FieldInfo f = c.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return f;
            }
            return null;
        }

        private static object Prop(object o, string name)
        {
            if (o == null) return null;
            try
            {
                for (Type c = o.GetType(); c != null; c = c.BaseType)
                {
                    var p = c.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p != null) return p.GetValue(o, null);
                }
            }
            catch { }
            return null;
        }

        private static void WarnOnce(string m)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("drawfix: " + m);
        }

        internal static void Install()
        {
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
        }

        /// <summary>Reference identity: game types may override Equals/GetHashCode, and these are two
        /// different fields on one object, so identity is the only key that means what we want.</summary>
        private sealed class RefEq : IEqualityComparer<object>
        {
            internal static readonly RefEq Instance = new RefEq();
            bool IEqualityComparer<object>.Equals(object a, object b) { return ReferenceEquals(a, b); }
            int IEqualityComparer<object>.GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); }
        }
    }
}
