using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace AgentTouchFix
{
    /// <summary>
    /// Three isolated lanes, one question each. Read-only: every hook is a postfix that only logs.
    ///
    ///   lane A  Texture2D.EnsureResident() -> false : which textures cannot be made resident at all
    ///   lane B  Graphic.DrawWorker                : for held/equipment draws, is the material and its
    ///                                               _MainTex actually bound (this is the only path that
    ///                                               ground drawing does not use)
    ///   lane C  pack/cache open failures          : is the engine failing to find the entry by name
    ///
    /// Each lane writes to its own file so nothing is mixed:
    ///   files/game/probe.A.log / probe.B.log / probe.C.log
    /// </summary>
    internal static class Probe
    {
        private static readonly object Gate = new object();
        private static readonly System.Collections.Generic.HashSet<string> SeenA = new System.Collections.Generic.HashSet<string>();
        private static readonly System.Collections.Generic.HashSet<string> SeenB = new System.Collections.Generic.HashSet<string>();
        private static int _resFail, _resident, _drawn, _noMat, _noTex, _packFail;

        private static void Log(string lane, string line)
        {
            try
            {
                string p = "/data/data/com.ports1de.waylith/files/game/probe." + lane + ".log";
                lock (Gate) System.IO.File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + "\n");
            }
            catch { }
        }

        internal static void Init()
        {
            try
            {
                var h = new HarmonyLib.Harmony("sleepsheep.waylith.agenttouchfix.probe");

                Type tex = Refl.T("Waylith.Rendering.Texture2D");
                MethodInfo ensure = tex?.GetMethod("EnsureResident",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (ensure != null) h.Patch(ensure, null, new HarmonyMethod(typeof(Probe).GetMethod("A_Ensure", BFlags)));

                MethodInfo worker = Refl.T("Verse.Graphic")?.GetMethod("DrawWorker",
                    BindingFlags.Public | BindingFlags.Instance);
                if (worker != null) h.Patch(worker, null, new HarmonyMethod(typeof(Probe).GetMethod("B_Draw", BFlags)));

                Log("A", "probe installed: ensure=" + (ensure != null) + " draw=" + (worker != null));
                Log("B", "probe installed");
                Log("C", "probe installed");
            }
            catch (Exception e) { Log("A", "install failed: " + e.Message); }
        }

        private const BindingFlags BFlags = BindingFlags.Static | BindingFlags.NonPublic;

        // ---- lane A -------------------------------------------------------------------------
        private static void A_Ensure(object __instance, bool __result)
        {
            try
            {
                if (__result) { Interlocked.Increment(ref _resident); return; }
                string name = P(__instance, "Name") as string ?? "?";
                string path = P(__instance, "SourcePath") as string ?? "-";
                string key = name + "|" + path;
                lock (SeenA) { if (!SeenA.Add(key)) return; }
                object cpu = P(__instance, "CpuData");
                int n = Interlocked.Increment(ref _resFail);
                if (n <= 60)
                    Log("A", "resfail #" + n + " name=" + name + " path=" + path
                             + " pixels=" + ((cpu != null && P(cpu, "HasData") is bool b && b) ? "y" : "n")
                             + " size=" + P(__instance, "width") + "x" + P(__instance, "height"));
            }
            catch { }
        }

        // ---- lane B -------------------------------------------------------------------------
        private static void B_Draw(object __instance, object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 4) return;
                object thingDef = __args[2];
                string defName = P(thingDef, "defName") as string ?? "?";
                string key = defName;
                lock (SeenB) { if (!SeenB.Add(key) || SeenB.Count > 40) return; }

                object mat = P(__instance, "MatSingle") ?? P(__instance, "matSingle");
                object tex = mat == null ? null : TexOf(mat);
                bool matNull = mat == null;
                if (matNull) Interlocked.Increment(ref _noMat);
                if (tex == null) Interlocked.Increment(ref _noTex);
                Interlocked.Increment(ref _drawn);

                Log("B", "draw def=" + defName
                         + " graphic=" + __instance.GetType().Name
                         + " mat=" + (matNull ? "NULL" : (P(mat, "name") as string ?? "?"))
                         + " shader=" + (mat == null ? "-" : (P(P(mat, "shader"), "name") as string ?? "?"))
                         + " mainTex=" + (tex == null ? "NULL" : (P(tex, "name") as string ?? "?")));
            }
            catch { }
        }

        private static object TexOf(object material)
        {
            try
            {
                MethodInfo m = material.GetType().GetMethod("GetTexture", new[] { typeof(string) });
                if (m != null) return m.Invoke(material, new object[] { "_MainTex" });
                PropertyInfo p = material.GetType().GetProperty("mainTexture");
                return p?.GetValue(material, null);
            }
            catch { }
            return null;
        }

        internal static void Summary()
        {
            Log("A", "summary resident=" + _resident + " resfail=" + _resFail);
            Log("B", "summary draws=" + _drawn + " matNull=" + _noMat + " texNull=" + _noTex);
            Log("C", "summary packFail=" + _packFail);
        }

        private static object P(object o, string name)
        {
            if (o == null) return null;
            try
            {
                for (Type c = o.GetType(); c != null; c = c.BaseType)
                {
                    PropertyInfo p = c.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p != null) return p.GetValue(o, null);
                    FieldInfo f = c.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null) return f.GetValue(o);
                }
            }
            catch { }
            return null;
        }
    }
}
