using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace AgentTouchFix
{
    /// <summary>
    /// Minimal probe: two hooks only, no material access, no hot engine methods.
    ///
    ///   [D] PawnRenderUtility.DrawCarriedThing - is the held/equipment draw reached, and for which def/texPath
    ///   [L] GpuTextureService.TryOpenTextureCache - which texture names the engine cannot resolve
    ///
    /// Everything is wrapped in try/catch and rate limited; nothing is modified.
    /// </summary>
    internal static class Probe
    {
        private const string File = "/data/data/com.ports1de.waylith/files/game/probe.log";
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags St = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static readonly object Gate = new object();
        private static readonly System.Collections.Generic.HashSet<string> Seen = new System.Collections.Generic.HashSet<string>();
        private static int _draws, _miss;
        private static long _last;

        private static void W(string lane, string line)
        {
            try
            {
                lock (Gate)
                    System.IO.File.AppendAllText(File, DateTime.Now.ToString("HH:mm:ss.fff") + " [" + lane + "] " + line + "\n");
            }
            catch { }
        }

        private static bool Once(string key)
        {
            lock (Seen) { if (Seen.Count > 120) return false; return Seen.Add(key); }
        }

        internal static void Init()
        {
            try
            {
                var h = new HarmonyLib.Harmony("sleepsheep.waylith.agenttouchfix.probe");
                int ok = 0;

                MethodInfo carried = Refl.T("Verse.PawnRenderUtility")?.GetMethod("DrawCarriedThing", St);
                if (carried != null)
                {
                    h.Patch(carried, new HarmonyMethod(typeof(Probe).GetMethod("Draw_Pre",
                        BindingFlags.Static | BindingFlags.NonPublic)));
                    ok++;
                }

                MethodInfo open = Refl.T("Waylith.Rendering.GpuTextureService")?.GetMethod("TryOpenTextureCache", St);
                if (open != null)
                {
                    h.Patch(open, null, new HarmonyMethod(typeof(Probe).GetMethod("Open_Post",
                        BindingFlags.Static | BindingFlags.NonPublic)));
                    ok++;
                }

                W("S", "min probe installed hooks=" + ok + "/2");
            }
            catch (Exception e) { W("S", "install failed: " + e.Message); }
        }

        private static void Draw_Pre(object __0, object __2)
        {
            try
            {
                Interlocked.Increment(ref _draws);
                string def = Str(Prop(__0, "def"), "defName");
                object gd = Prop(Prop(__0, "def"), "graphicData");
                string tex = gd == null ? "?" : (Prop(gd, "texPath") as string ?? "?");
                string graphic = Prop(Prop(__2, "def"), "defName");
                if (Once("d:" + def + "|" + tex))
                    W("D", "carried def=" + def + " texPath=" + tex + " thing=" + graphic);
                Summary();
            }
            catch { }
        }

        private static void Open_Post(object[] __args, bool __result)
        {
            try
            {
                if (__result) return;
                string path = __args != null && __args.Length > 0 ? __args[0] as string : "-";
                Interlocked.Increment(ref _miss);
                if (Once("l:" + path)) W("L", "lookup MISS " + path);
                Summary();
            }
            catch { }
        }

        private static void Summary()
        {
            long now = Environment.TickCount;
            if (now - _last < 20000) return;
            _last = now;
            W("S", "draws=" + _draws + " lookupMiss=" + _miss);
        }

        private static string Str(object o, string name)
        {
            object v = Prop(o, name);
            return v as string ?? "?";
        }

        private static object Prop(object o, string name)
        {
            if (o == null) return null;
            try
            {
                for (Type c = o.GetType(); c != null; c = c.BaseType)
                {
                    PropertyInfo p = c.GetProperty(name, Any);
                    if (p != null) return p.GetValue(o, null);
                    FieldInfo f = c.GetField(name, Any);
                    if (f != null) return f.GetValue(o);
                }
            }
            catch { }
            return null;
        }
    }
}
