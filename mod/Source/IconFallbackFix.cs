using System;
using System.IO;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// Postfix on Verse.Widgets.GetIconFor: give the draw path a def-level texture when the
    /// instance-level one is missing.
    ///
    /// Widgets.ThingIcon(Rect, Thing, ...) draws exactly what GetIconFor(Thing, ...) returns and gives
    /// up when that is null or BaseContent.BadTex (Widgets.cs:489-495, "if (iconFor == null || iconFor
    /// == BaseContent.BadTex) return;"). The Thing overload builds its answer from the live instance -
    /// thing.Graphic.ExtractInnerGraphicFor(thing).MatAt(rot).mainTexture (Widgets.cs:739-741) - so a
    /// Thing whose Graphic is a clone (mod stuff/style variant, worn or reskinned item) can hand back a
    /// texture-less material while the same def's uiIcon and graphic are perfectly drawable.
    ///
    /// The fix is a fallback, not a rewrite: only when the original result is null / BadTex does it
    /// look for a replacement, in this order -
    ///   1. def.uiIcon                       (non-null and not BadTex)
    ///   2. def.graphic.MatAt(def.defaultPlacingRot).mainTexture
    ///   3. neither -> leave the original values exactly as they are
    /// When 1 or 2 is adopted the `out Material` is set to null and `out Color` to white, so
    /// Widgets takes its plain no-material draw path instead of painting again through the material
    /// that just failed. A drawable result is never touched, so the per-call cost is one null test
    /// plus one reference test - no allocation on the hot path.
    ///
    /// Everything is reflection: this mod carries no reference to UnityEngine/UnityStubs, and the two
    /// out-parameters are handed over as a closed generic (TMat / TCol / TTex are bound to the live
    /// parameter types at install time). Any failure - missing type, missing field, patch rejection,
    /// exception inside the postfix - leaves the original draw completely untouched and warns once.
    /// </summary>
    internal static class IconFallbackFix
    {
        /// <summary>Same file-switch convention as the rest of the mod: contents "off" disables.</summary>
        internal const string SwitchPath =
            "/data/data/com.ports1de.waylith/files/game/agenttouchfix.iconfallback";

        private const int LogCap = 5;
        private const string HarmonyId = "sleepsheep.waylith.agenttouchfix.iconfallback";

        private static bool _off;
        private static bool _resolved;
        private static int _hits, _warned;

        // Types are only ever compared by identity against the live ones, never assumed by name.
        private static Type _thingType, _thingDefType, _materialType, _colorType;
        private static object _badTex;
        private static object _white;

        private static FieldInfo _fUiIcon, _fGraphic, _fRot, _fDef, _fDefName;
        private static PropertyInfo _pUiIcon, _pDefName, _pMainTexture;
        private static MethodInfo _matAt;
        private static bool _matAtTwoArgs;

        internal static void Init()
        {
            try
            {
                string text = "";
                try
                {
                    if (File.Exists(SwitchPath)) text = File.ReadAllText(SwitchPath);
                }
                catch (Exception e) { Refl.Warn("iconfallback: switch unreadable: " + e.Message); }

                _off = !IconFallbackDecision.Enabled(text);
                if (_off) Refl.Info("iconfallback: disabled by " + SwitchPath);
            }
            catch (Exception e) { WarnOnce("init: " + e.Message); }
        }

        // ------------------------------------------------------------------ postfix entry points

        /// <summary>
        /// GetIconFor(Thing, Vector2, Rot4?, bool, out float, out float, out Vector2, out Color,
        /// out Material). TTex/TMat/TCol are the live return / material / colour types.
        /// </summary>
        private static void PostfixThing<TTex, TMat, TCol>(object thing, ref TTex __result,
                                                          ref TMat material, ref TCol color)
        {
            if (!NeedsFallback(__result)) return;
            try
            {
                object def = _fDef == null ? null : _fDef.GetValue(thing);
                string source = Pick(def, out object tex);
                if (source.Length == 0) return;

                TTex resolved = (TTex)tex;     // Texture2D -> Texture on the Thing overload
                __result = resolved;
                material = default(TMat);      // null: Widgets draws without a material
                if (_white != null) color = (TCol)_white;

                Report(def, source);
            }
            catch (Exception e) { WarnOnce(e.Message); }
        }

        /// <summary>
        /// GetIconFor(ThingDef, out Material, ThingDef, ThingStyleDef, int?). No colour parameter: the
        /// caller (Widgets.ThingIcon(Rect, ThingDef, ...)) sets GUI.color itself from the def/stuff.
        /// </summary>
        private static void PostfixDef<TTex, TMat>(object thingDef, ref TTex __result, ref TMat material)
        {
            if (!NeedsFallback(__result)) return;
            try
            {
                string source = Pick(thingDef, out object tex);
                if (source.Length == 0) return;

                TTex resolved = (TTex)tex;
                __result = resolved;
                material = default(TMat);

                Report(thingDef, source);
            }
            catch (Exception e) { WarnOnce(e.Message); }
        }

        /// <summary>Hot path: one box of a reference type (no allocation) and two comparisons.</summary>
        private static bool NeedsFallback<T>(T result)
        {
            object tex = (object)result;
            return tex == null || ReferenceEquals(tex, _badTex);
        }

        // ------------------------------------------------------------------ the fallback itself

        /// <summary>
        /// Adopt def.uiIcon, else the def graphic's texture, else nothing. Runs only for a result that
        /// was already unusable, so the reflection below is never on the steady-state path.
        /// </summary>
        private static string Pick(object def, out object tex)
        {
            tex = null;
            if (def == null) return IconFallbackDecision.None;

            object icon = Read(_fUiIcon, _pUiIcon, def);
            bool iconOk = IsDrawable(icon);

            // The graphic walk allocates (boxed Rot4 + argument array), so it is skipped outright
            // when the def already has a usable icon.
            object graphicTex = null;
            bool graphicOk = false;
            if (!iconOk)
            {
                graphicTex = GraphicTexture(def);
                graphicOk = IsDrawable(graphicTex);
            }

            string source = IconFallbackDecision.Choose(false, iconOk, graphicOk);
            if (source == IconFallbackDecision.UiIcon) tex = icon;
            else if (source == IconFallbackDecision.Graphic) tex = graphicTex;
            return source;
        }

        private static object GraphicTexture(object def)
        {
            if (_fGraphic == null || _matAt == null || _fRot == null) return null;

            object graphic = _fGraphic.GetValue(def);
            if (graphic == null) return null;

            object rot = _fRot.GetValue(def);
            object mat = _matAtTwoArgs
                ? _matAt.Invoke(graphic, new object[] { rot, null })      // MatAt(Rot4, Thing)
                : _matAt.Invoke(graphic, new object[] { rot });           // MatAt(Rot4)
            if (mat == null || _pMainTexture == null) return null;

            return _pMainTexture.GetValue(mat, null);
        }

        private static bool IsDrawable(object tex)
        {
            return IconFallbackDecision.Drawable(tex == null, _badTex != null && ReferenceEquals(tex, _badTex));
        }

        private static object Read(FieldInfo field, PropertyInfo prop, object def)
        {
            try
            {
                if (field != null) return field.GetValue(def);
                if (prop != null) return prop.GetValue(def, null);
            }
            catch (Exception e) { WarnOnce("read: " + e.Message); }
            return null;
        }

        private static string DefName(object def)
        {
            object name = Read(_fDefName, _pDefName, def);
            return name as string ?? "?";
        }

        private static void Report(object def, string source)
        {
            int n = Interlocked.Increment(ref _hits);
            if (n > LogCap) return;
            Refl.Info("icon fallback: " + DefName(def) + " via " + source);
        }

        private static void WarnOnce(string m)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("iconfallback: " + m);
        }

        // ------------------------------------------------------------------ reflection setup

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            _thingType = Refl.T("Verse.Thing");
            _thingDefType = Refl.T("Verse.ThingDef");
            if (_thingDefType != null)
            {
                _fUiIcon = Field(_thingDefType, "uiIcon");
                if (_fUiIcon == null) _pUiIcon = Prop(_thingDefType, "uiIcon");
                _fGraphic = Field(_thingDefType, "graphic");
                _fRot = Field(_thingDefType, "defaultPlacingRot");
                _fDefName = Field(_thingDefType, "defName");
                if (_fDefName == null) _pDefName = Prop(_thingDefType, "defName");
            }
            if (_thingType != null) _fDef = Field(_thingType, "def");

            try
            {
                Type g = _fGraphic == null ? null : _fGraphic.FieldType;
                if (g != null)
                {
                    foreach (MethodInfo m in g.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (m.Name != "MatAt") continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length == 0 || ps[0].ParameterType.Name != "Rot4") continue;
                        _matAt = m;
                        _matAtTwoArgs = ps.Length >= 2;
                        _materialType = m.ReturnType;
                        break;
                    }
                    if (_matAt != null) _pMainTexture = Prop(_materialType, "mainTexture");
                }
            }
            catch (Exception e) { WarnOnce("graphic: " + e.Message); }

            try
            {
                Type baseContent = Refl.T("Verse.BaseContent");
                if (baseContent != null)
                {
                    FieldInfo f = baseContent.GetField("BadTex", BindingFlags.Public | BindingFlags.Static)
                                  ?? baseContent.GetField("BadTex", BindingFlags.NonPublic | BindingFlags.Static);
                    if (f != null) _badTex = f.GetValue(null);
                }
            }
            catch (Exception e) { WarnOnce("BadTex: " + e.Message); }

            try
            {
                Type color = _colorType ?? Refl.T("UnityEngine.Color");
                if (color != null)
                {
                    _colorType = color;
                    // Color.white is a static property on this port (UnityStubs/UnityEngine/Color.cs:72);
                    // a static field of the same name is accepted too.
                    PropertyInfo w = null;
                    for (Type c = color; c != null && w == null; c = c.BaseType)
                        w = c.GetProperty("white", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (w != null) _white = w.GetValue(null, null);
                    else
                    {
                        FieldInfo f = color.GetField("white", BindingFlags.Public | BindingFlags.Static)
                                      ?? color.GetField("white", BindingFlags.NonPublic | BindingFlags.Static);
                        if (f != null) _white = f.GetValue(null);
                    }
                }
                if (_white == null) WarnOnce("Color.white not found");
            }
            catch (Exception e) { WarnOnce("Color.white: " + e.Message); }
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

        private static PropertyInfo Prop(Type t, string name)
        {
            for (Type c = t; c != null; c = c.BaseType)
            {
                PropertyInfo p = c.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.GetIndexParameters().Length == 0) return p;
            }
            return null;
        }

        private static ParameterInfo Param(ParameterInfo[] ps, string name)
        {
            for (int i = 0; i < ps.Length; i++)
                if (ps[i].Name == name) return ps[i];
            return null;
        }

        internal static void Install()
        {
            try
            {
                if (_off) return;

                Type widgets = Refl.T("Verse.Widgets");
                if (widgets == null) { Refl.Warn("iconfallback: Verse.Widgets not found"); return; }

                Resolve();
                if (_thingDefType == null) { Refl.Warn("iconfallback: Verse.ThingDef not found"); return; }

                MethodInfo thingPatch = typeof(IconFallbackFix).GetMethod("PostfixThing",
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo defPatch = typeof(IconFallbackFix).GetMethod("PostfixDef",
                    BindingFlags.Static | BindingFlags.NonPublic);
                var harmony = new Harmony(HarmonyId);
                int patched = 0;

                foreach (MethodInfo m in widgets.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "GetIconFor") continue;

                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 0) continue;

                    // Type-exact target test: only get the Thing / ThingDef entry points, and only the
                    // overloads that actually produce the icon material (the 4-arg ThingDef overload
                    // just forwards to the 5-arg one and must not be patched twice).
                    bool onThing = _thingType != null && ps[0].ParameterType == _thingType;
                    bool onDef = ps[0].ParameterType == _thingDefType;
                    if (!onThing && !onDef) continue;

                    ParameterInfo mat = Param(ps, "material");
                    if (mat == null || !mat.ParameterType.IsByRef) continue;

                    try
                    {
                        MethodInfo closed;
                        if (onThing)
                        {
                            ParameterInfo col = Param(ps, "color");
                            if (col == null) continue;
                            if (_colorType != null && col.ParameterType != _colorType) continue;
                            closed = thingPatch.MakeGenericMethod(m.ReturnType, mat.ParameterType.GetElementType(),
                                                                  col.ParameterType.GetElementType());
                        }
                        else
                        {
                            closed = defPatch.MakeGenericMethod(m.ReturnType, mat.ParameterType.GetElementType());
                        }

                        harmony.Patch(m, postfix: new HarmonyMethod(closed));
                        patched++;
                    }
                    catch (Exception e)
                    {
                        Refl.Warn("iconfallback: " + m.Name + "/" + ps.Length + " not patched: " + e.Message);
                    }
                }

                Refl.Info("iconfallback: patched " + patched + " GetIconFor overload(s)");
            }
            catch (Exception e) { WarnOnce("install: " + e.Message); }
        }
    }
}
