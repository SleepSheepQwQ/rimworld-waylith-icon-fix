using System;
using System.Reflection;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// The equipment box beside the Draft button draws the weapon icon at a small size and comes out
    /// as a grey-white slab with one dark bar, while the Gear tab (larger, mip 0) looks fine.
    /// Weapon DDS files in this port ship a single mip level although textures.info claims 7-9, and
    /// material _MainTex_ST here is (offset=0, scale~0.956). Both faults show up only when the icon is
    /// sampled small. So: draw the icon ourselves with identity UVs straight from mip 0, and skip
    /// Widgets.ThingIcon for the Thing overload that the equipment box uses.
    /// </summary>
    internal static class UvFix
    {
        private static MethodInfo _draw;
        private static object _unit;
        private static bool _warned;
        private static int _drawn;

        internal static void Init()
        {
            try
            {
                Type rectT = FindType("UnityEngine.Rect");
                Type guiT = FindType("UnityEngine.GUI");
                if (rectT == null || guiT == null) { Refl.Warn("uvfix: Rect/GUI type missing"); return; }

                foreach (MethodInfo m in guiT.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "DrawTextureWithTexCoords") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 3 && ps[1].ParameterType.Name == "Texture" && ps[2].ParameterType == rectT)
                    { _draw = m; break; }
                }
                if (_draw == null) { Refl.Warn("uvfix: GUI.DrawTextureWithTexCoords(Rect,Texture,Rect) missing"); return; }
                _unit = Activator.CreateInstance(rectT, new object[] { 0f, 0f, 1f, 1f });

                Type widgets = FindType("Verse.Widgets");
                MethodInfo prefix = typeof(UvFix).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
                int n = 0;
                foreach (MethodInfo m in widgets.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "ThingIcon") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length < 2 || ps[0].ParameterType.Name != "Rect" || ps[1].ParameterType.Name != "Thing") continue;
                    new Harmony("sleepsheep.waylith.agenttouchfix.uvfix").Patch(m, new HarmonyMethod(prefix));
                    n++;
                }
                Refl.Info("uvfix: patched " + n + " ThingIcon(Rect,Thing,...) overload(s)");
            }
            catch (Exception e) { Refl.Warn("uvfix: install failed: " + e.Message); }
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
            Type t = o.GetType();
            for (Type c = t; c != null; c = c.BaseType)
            {
                PropertyInfo pi = c.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (pi != null) { try { return pi.GetValue(o, null); } catch { } }
                FieldInfo fi = c.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fi != null) { try { return fi.GetValue(o); } catch { } }
            }
            return null;
        }

        private static bool Prefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 2) return true;
                object thing = __args[1];
                if (thing == null) return true;
                object def = Get(thing, "def");
                if (def == null) return true;
                object tex = Get(def, "uiIcon");
                if (tex == null) return true;                 // let the normal path handle "no icon"
                if (Get(tex, "width") is int w && w <= 0) return true;

                _draw.Invoke(null, new object[] { __args[0], tex, _unit });
                _drawn++;
                return false;                                 // we drew it: skip the broken path
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Refl.Warn("uvfix: draw failed, falling back: " + e.Message); }
                return true;
            }
        }
    }
}
