using System;
using System.Reflection;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// The gizmo to the right of Draft is the weapon's attack verb (Command_VerbTarget.DrawIcon).
    /// When the command is disabled, Command.GizmoOnGUIInt forces parms.lowLight = true, so
    /// Widgets.ThingIcon builds a ShaderDatabase.GrayscaleGUI material whose maskTex comes from
    /// material.GetTexture(MaskTex). Weapon defs have no maskPath, so maskTex is null and the icon
    /// renders as a desaturated near-white slab with one dark bar, while the Gear tab (grayscale
    /// always false) looks right.
    ///
    /// Fix: drop the grayscale flag for icons whose graphic has no mask texture. The engine then
    /// draws the normal UI material; the caller's GUI.color (SaturationChanged(0)/alpha 0.6) still
    /// dims it, so disabled-state feedback is preserved without the broken shader path.
    /// </summary>
    internal static class UvFix
    {
        private static int _flipped;
        private static bool _logged;

        internal static void Init()
        {
            try
            {
                Type widgets = FindType("Verse.Widgets");
                Type shaderDb = FindType("Verse.ShaderDatabase");
                if (widgets == null) { Refl.Warn("grayfix: Verse.Widgets missing"); return; }
                if (shaderDb != null) _grayShader = Get(shaderDb, "GrayscaleGUI");
                if (_grayShader == null) { Refl.Warn("grayfix: ShaderDatabase.GrayscaleGUI missing"); return; }

                MethodInfo prefix = typeof(UvFix).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
                int n = 0;
                foreach (MethodInfo m in widgets.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "ThingIcon") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length < 2 || ps[1].ParameterType.Name != "Thing") continue;
                    int gi = -1;
                    for (int i = 2; i < ps.Length; i++) if (ps[i].ParameterType == typeof(bool)) gi = i;
                    if (gi < 0) continue;
                    _grayIndex = gi;
                    new Harmony("sleepsheep.waylith.agenttouchfix.grayfix").Patch(m, new HarmonyMethod(prefix));
                    n++;
                }
                Refl.Info("grayfix: patched " + n + " ThingIcon(Rect,Thing,...) overload(s), grayscale arg #" + _grayIndex);
            }
            catch (Exception e) { Refl.Warn("grayfix: install failed: " + e.Message); }
        }

        private static object _grayShader;
        private static int _grayIndex = -1;

        private static bool Prefix(object[] __args)
        {
            try
            {
                if (_grayIndex < 0 || __args == null || __args.Length <= _grayIndex) return true;
                if (!(__args[_grayIndex] is bool wantGray) || !wantGray) return true;

                object thing = __args[1];
                object graphic = thing == null ? null : Get(thing, "Graphic");
                object mat = graphic == null ? null : Get(graphic, "MatSingle");
                object mask = mat == null ? null : Get(mat, "maskTex");
                if (mask != null) return true;                 // real mask: grayscale works as intended

                __args[_grayIndex] = false;                    // no mask -> plain UI material
                _flipped++;
                if (!_logged) { _logged = true; Refl.Info("grayfix: first flip, mask was null"); }
                return true;
            }
            catch (Exception e)
            {
                if (!_logged) { _logged = true; Refl.Warn("grayfix: skipped: " + e.Message); }
                return true;
            }
        }

        private static Type FindType(string name)
        {
            Type t = Type.GetType(name);
            if (t != null) return t;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = a.GetType(name); if (t != null) return t; } catch { }
            }
            return null;
        }

        private static object Get(object o, string name)
        {
            if (o == null) return null;
            for (Type c = o.GetType(); c != null; c = c.BaseType)
            {
                PropertyInfo pi = c.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (pi != null) { try { return pi.GetValue(o, null); } catch { } }
                FieldInfo fi = c.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fi != null) { try { return fi.GetValue(o); } catch { } }
            }
            return null;
        }
    }
}
