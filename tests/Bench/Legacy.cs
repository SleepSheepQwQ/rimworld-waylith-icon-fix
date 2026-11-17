using System;
using System.Reflection;
using System.Threading;

namespace AgentTouchFix
{
    /// <summary>
    /// The prefix exactly as it shipped in v40, kept only so the benchmark has something honest to
    /// compare against: "def" property walk on every call, reflection lookups on every call, and
    /// Material.name read through GetProperties() on every call.
    /// </summary>
    internal static class LegacyPrefix
    {
        private static int _filled, _cleared, _warned;
        private static FieldInfo _fIcon, _fMat, _fGraphic, _fGraphicData, _fTexPath, _fPath, _fDefName;
        private static Type _defType, _badTexType;
        private static object _badTex;
        private static MethodInfo _finder;
        private static string _mode = IconPath.Plain;

        internal static void Prefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 2) return;
                object def = __args[1];
                if (def == null) return;
                // Widgets.ThingIcon(Rect, Thing, ...) passes a Thing: repair the ThingDef behind it
                object innerDef = Prop(def, "def");
                if (innerDef != null && innerDef != def) def = innerDef;
                if (_defType != def.GetType()) Resolve(def.GetType());

                object icon = _fIcon.GetValue(def);
                if (icon == null || IsBad(icon))
                    Fill(def);

                if (_fMat != null)
                {
                    object mat = _fMat.GetValue(def);
                    if (mat != null && IconPath.UsePlain(NameOf(mat), _mode))
                    {
                        _fMat.SetValue(def, null);
                        int c = Interlocked.Increment(ref _cleared);
                        if (c <= 5) Refl.Info("drawfix: dropped icon material '" + NameOf(mat) + "' on " + DefName(def));
                    }
                }
            }
            catch (Exception e) { WarnOnce(e.Message); }
        }

        private static void Fill(object def)
        {
            object graphic = _fGraphic == null ? null : _fGraphic.GetValue(def);
            string path = null;
            if (graphic != null && _fPath != null) path = _fPath.GetValue(graphic) as string;
            if (string.IsNullOrEmpty(path) && _fGraphicData != null && _fTexPath != null)
            {
                object gd = _fGraphicData.GetValue(def);
                if (gd != null) path = _fTexPath.GetValue(gd) as string;
            }
            if (string.IsNullOrEmpty(path)) return;

            object tex = Load(path);
            if (tex == null) tex = Load(path + "_south");
            if (tex == null) return;

            _fIcon.SetValue(def, tex);
            int n = Interlocked.Increment(ref _filled);
            if (n <= 5) Refl.Info("drawfix: uiIcon <- " + path + " on " + DefName(def));
            else if (n % 100 == 0) Refl.Info("drawfix: filled " + n + " icons at draw time");
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

        private static string NameOf(object mat)
        {
            try
            {
                foreach (PropertyInfo p in mat.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (p.Name == "name" && p.GetIndexParameters().Length == 0) return p.GetValue(mat, null) as string;
            }
            catch { }
            return null;
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
    }
}
