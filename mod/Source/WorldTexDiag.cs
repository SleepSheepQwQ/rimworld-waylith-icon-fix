using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace AgentTouchFix
{
    /// <summary>
    /// Diagnostic + candidate fix for "held weapons mostly do not render".
    ///
    /// In this port every world draw goes through
    ///     Graphics.DrawMesh(mesh, matrix, material, layer)   (PawnRenderUtility.DrawEquipmentAiming:114)
    /// and a material draw whose texture has been evicted produces nothing at all - no error, no
    /// placeholder. Textures are evicted from the GPU whenever `DrainPendingEvictions` decides the
    /// disk cache can re-hydrate them, which on this device happens constantly (measured ~6100
    /// evictions per minute in an earlier session).
    ///
    /// Two switches, both read at startup from files/game/:
    ///   agenttouchfix.texkeep = on      -> never evict (diagnostic: "is eviction the cause?")
    ///   agenttouchfix.weapdiag = on     -> log the material state of every weapon draw, once per def
    /// </summary>
    internal static class WorldTexDiag
    {
        private static int _evictions, _weaponSamples, _warned;
        private static long _lastReportMs;
        private static readonly System.Collections.Generic.HashSet<string> _seenDefs =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        internal static bool Keep
        {
            get { return FlagOn("agenttouchfix.texkeep"); }
        }

        internal static bool Diag
        {
            get { return FlagOn("agenttouchfix.weapdiag"); }
        }

        private static bool FlagOn(string name)
        {
            try
            {
                string p = "/data/data/com.ports1de.waylith/files/game/" + name;
                if (!System.IO.File.Exists(p)) return false;
                string v = (System.IO.File.ReadAllText(p) ?? "").Trim().ToLowerInvariant();
                return v == "on" || v == "1" || v.StartsWith("on");
            }
            catch { return false; }
        }

        // ---- eviction gate -----------------------------------------------------------------
        internal static bool Evict_Prefix()
        {
            try
            {
                int n = Interlocked.Increment(ref _evictions);
                long now = Environment.TickCount;
                if (now - _lastReportMs > 10000)
                {
                    _lastReportMs = now;
                    Refl.Info("texkeep: suppressed " + n + " evictions so far");
                }
            }
            catch { }
            return false;   // skip the original Evict()
        }

        internal static void Install()
        {
            try
            {
                Type t = Refl.T("Waylith.Rendering.Texture2D");
                MethodInfo evict = t == null ? null : t.GetMethod("Evict",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (evict == null) { Refl.Warn("texkeep: Texture2D.Evict() not found"); return; }

                var h = new HarmonyLib.Harmony("sleepsheep.waylith.agenttouchfix.texkeep");
                h.Patch(evict, new HarmonyMethod(typeof(WorldTexDiag).GetMethod("Evict_Prefix",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)));
                Refl.Info("texkeep: Texture2D.Evict() suppressed (diagnostic mode)");
            }
            catch (Exception e) { Refl.Warn("texkeep install failed: " + e.Message); }
        }

        // ---- weapon draw probe -------------------------------------------------------------
        internal static void Weapon_Prefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 1) return;
                object eq = __args[0];
                if (eq == null) return;

                object def = Prop(eq, "def");
                string name = def == null ? "?" : (Prop(def, "defName") as string ?? "?");
                lock (_seenDefs)
                {
                    if (!_seenDefs.Add(name)) return;
                    if (_seenDefs.Count > 12) return;
                }
                Interlocked.Increment(ref _weaponSamples);

                object graphic = Prop(eq, "Graphic");
                object mat = null;
                try
                {
                    MethodInfo m = graphic == null ? null : graphic.GetType().GetMethod("MatSingleFor");
                    if (m != null) mat = m.Invoke(graphic, new[] { eq });
                }
                catch { }

                object eng = mat == null ? null : Prop(mat, "engineMaterial");
                object tex = eng == null ? null : TexOf(eng, "_MainTex");
                object sh = eng == null ? null : Prop(eng, "Shader");

                Refl.Info("weapdiag: " + name
                    + " graphic=" + (graphic == null ? "null" : graphic.GetType().Name)
                    + " mat=" + (mat == null ? "null" : (Prop(mat, "name") as string ?? "?"))
                    + " engine=" + (eng == null ? "null" : "ok")
                    + " shader=" + (sh == null ? "?" : (Prop(sh, "name") as string ?? "?"))
                    + " mainTex=" + (tex == null ? "null" : (Prop(tex, "name") as string ?? "?")));
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("weapdiag: " + e.Message);
            }
        }

        private static object TexOf(object material, string name)
        {
            try
            {
                foreach (MethodInfo m in material.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string) && m.Name == "GetTexture")
                        return m.Invoke(material, new object[] { name });
                }
            }
            catch { }
            return null;
        }

        private static object Prop(object o, string name)
        {
            if (o == null) return null;
            try
            {
                for (Type c = o.GetType(); c != null; c = c.BaseType)
                {
                    PropertyInfo p = c.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p != null) return p.GetValue(o, null);
                }
            }
            catch { }
            return null;
        }

        internal static void InstallWeaponProbe()
        {
            try
            {
                Type t = Refl.T("Verse.PawnRenderUtility");
                MethodInfo draw = t == null ? null : t.GetMethod("DrawEquipmentAiming",
                    BindingFlags.Public | BindingFlags.Static, null, new[]
                    {
                        Refl.T("Verse.Thing"), Refl.T("UnityEngine.Vector3"), typeof(float)
                    }, null);
                if (draw == null) { Refl.Warn("weapdiag: DrawEquipmentAiming not found"); return; }
                new HarmonyLib.Harmony("sleepsheep.waylith.agenttouchfix.weapdiag")
                    .Patch(draw, new HarmonyMethod(typeof(WorldTexDiag).GetMethod("Weapon_Prefix",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)));
                Refl.Info("weapdiag: patched PawnRenderUtility.DrawEquipmentAiming");
            }
            catch (Exception e) { Refl.Warn("weapdiag install failed: " + e.Message); }
        }
    }
}
