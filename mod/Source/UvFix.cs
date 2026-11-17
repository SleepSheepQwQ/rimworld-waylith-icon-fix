using System;
using System.Reflection;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// The box beside Draft is the weapon attack gizmo (Command_VerbTarget.DrawIcon). When the
    /// command is disabled, Command.GizmoOnGUIInt sets parms.lowLight = true, which reaches
    /// Widgets.ThingIcon(..., grayscale: true). That branch (Widgets.cs:504-523) builds a material
    /// from ShaderDatabase.GrayscaleGUI == LoadShader("Misc/GrayscaleGUI").
    ///
    /// This port does not ship that shader program: Waylith.Rendering.ShaderRegistry.Resolve falls
    /// back to the default shader for unregistered names, so the icon is drawn through the wrong
    /// program and shows as a flat grey slab with nothing visible.
    ///
    /// Fix: force grayscale = false for Thing icons. Widgets.cs:504 then keeps the already resolved
    /// UI material. The disabled look is still conveyed by the caller, which sets GUI.color to a
    /// desaturated, half-alpha tint (Command.cs:356).
    ///
    /// No member names are looked up here: the flag is located by parameter type on the overload
    /// whose second parameter is a Thing (Widgets.cs:481), which is the only one carrying grayscale.
    /// </summary>
    internal static class UvFix
    {
        private static int _grayIndex = -1;
        private static int _flipped;
        private static bool _logged;

        internal static void Init()
        {
            try
            {
                Type widgets = FindType("Verse.Widgets");
                if (widgets == null) { Refl.Warn("grayfix: Verse.Widgets not found"); return; }

                MethodInfo prefix = typeof(UvFix).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
                if (prefix == null) { Refl.Warn("grayfix: prefix missing"); return; }

                int patched = 0;
                foreach (MethodInfo m in widgets.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "ThingIcon") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 7) continue;                       // Thing overload: rect,thing,alpha,rot,stack,scale,grayscale
                    if (ps[0].ParameterType.Name != "Rect") continue;
                    if (ps[1].ParameterType.Name != "Thing") continue;
                    if (ps[6].ParameterType != typeof(bool)) continue;
                    if (!ps[6].HasDefaultValue || !(ps[6].DefaultValue is bool d) || d) continue;   // default false
                    _grayIndex = 6;
                    new Harmony("sleepsheep.waylith.agenttouchfix.grayfix").Patch(m, new HarmonyMethod(prefix));
                    patched++;
                }
                Refl.Info("grayfix: patched " + patched + " ThingIcon(Rect,Thing,...) overload(s), grayscale at #" + _grayIndex);
                if (patched == 0) Refl.Warn("grayfix: no matching overload - nothing patched");
            }
            catch (Exception e) { Refl.Warn("grayfix: install threw " + e.GetType().Name + ": " + e.Message); }
        }

        private static bool Prefix(object[] __args)
        {
            try
            {
                if (_grayIndex < 0 || __args == null || __args.Length <= _grayIndex) return true;
                if (!(__args[_grayIndex] is bool want) || !want) return true;
                __args[_grayIndex] = false;
                _flipped++;
                if (!_logged) { _logged = true; Refl.Info("grayfix: first grayscale flip (icon " + _flipped + ")"); }
            }
            catch (Exception e)
            {
                if (!_logged) { _logged = true; Refl.Warn("grayfix: prefix threw " + e.GetType().Name + ": " + e.Message); }
            }
            return true;
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
    }
}
