using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace AgentTouchFix
{
    /// <summary>
    /// One-shot instrumentation: every remaining hypothesis gets its own lane in one log, so a single
    /// run answers all of them. Read-only - all hooks are prefix/postfix that only write a line.
    ///
    ///   [C1] DrawCarriedThing     is the held/equipment draw reached, and for which def
    ///   [C2] Graphic.DrawWorker   material / shader / _MainTex / _MaskTex / _MainTex_ST / drawSize
    ///   [C3] DrawMesh             commands dropped because material or mesh was null
    ///   [R1] EnsureResident=false textures that cannot be made resident
    ///   [R2] TryUploadFromCache   rehydrate success/failure
    ///   [R3] TryOpenTextureCache  pack/cache lookup miss (by name)
    ///   [M1] CompletedChain       stored mips vs requested mips (file has 1, index claims 8)
    ///   [S]  summary every 20 s
    /// </summary>
    internal static class Probe
    {
        private const string Dir = "/data/data/com.ports1de.waylith/files/game/";
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags St = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static readonly object Gate = new object();
        private static readonly System.Collections.Generic.HashSet<string> Seen = new System.Collections.Generic.HashSet<string>();

        private static int _carried, _drawn, _matNull, _texNull, _maskNull, _resFail, _resident,
                            _uploadOk, _uploadFail, _cacheMiss, _cacheHit, _chain, _chainShort, _drops;
        private static long _lastSummary;

        private static void W(string lane, string line)
        {
            try
            {
                lock (Gate)
                    System.IO.File.AppendAllText(Dir + "probe.log",
                        DateTime.Now.ToString("HH:mm:ss.fff") + " [" + lane + "] " + line + "\n");
            }
            catch { }
        }

        private static bool Once(string key, int limit)
        {
            lock (Seen) { if (Seen.Count >= limit) return false; return Seen.Add(key); }
        }

        private static MethodInfo M(string name)
        {
            return typeof(Probe).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        }

        internal static void Init()
        {
            try
            {
                var h = new HarmonyLib.Harmony("sleepsheep.waylith.agenttouchfix.probe");
                int ok = 0;

                MethodInfo carried = Refl.T("Verse.PawnRenderUtility")?.GetMethod("DrawCarriedThing", St);
                if (carried != null) { h.Patch(carried, new HarmonyMethod(M("C1_Pre"))); ok++; }

                MethodInfo worker = Refl.T("Verse.Graphic")?.GetMethod("DrawWorker", Any);
                if (worker != null) { h.Patch(worker, null, new HarmonyMethod(M("C2_Post"))); ok++; }

                MethodInfo dm = Refl.T("UnityEngine.Graphics")?.GetMethod("DrawMesh", St, null,
                    new[] { Refl.T("UnityEngine.Mesh"), Refl.T("UnityEngine.Matrix4x4"),
                            Refl.T("UnityEngine.Material"), typeof(int) }, null);
                if (dm != null) { h.Patch(dm, new HarmonyMethod(M("C3_Pre"))); ok++; }

                MethodInfo ensure = Refl.T("Waylith.Rendering.Texture2D")?.GetMethod("EnsureResident", Any, null, Type.EmptyTypes, null);
                if (ensure != null) { h.Patch(ensure, null, new HarmonyMethod(M("R1_Post"))); ok++; }

                MethodInfo up = Refl.T("Waylith.Rendering.Texture2D")?.GetMethod("TryUploadFromCache",
                    BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (up != null) { h.Patch(up, null, new HarmonyMethod(M("R2_Post"))); ok++; }

                MethodInfo open = Refl.T("Waylith.Rendering.GpuTextureService")?.GetMethod("TryOpenTextureCache", St);
                if (open != null) { h.Patch(open, null, new HarmonyMethod(M("R3_Post"))); ok++; }

                Type chain = Refl.T("Waylith.Graphics.Textures.CompletedChainTextureSource");
                ConstructorInfo ctor = null;
                if (chain != null)
                {
                    ConstructorInfo[] cs = chain.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (cs.Length > 0) ctor = cs[0];
                }
                if (ctor != null) { h.Patch(ctor, new HarmonyMethod(M("M1_Pre"))); ok++; }

                W("S", "probe installed, hooks=" + ok + "/7");
            }
            catch (Exception e) { W("S", "install failed: " + e.Message); }
        }

        // ---------------------------------------------------------------- C1
        private static void C1_Pre(object __0)
        {
            try
            {
                Interlocked.Increment(ref _carried);
                string name = Prop(Prop(__0, "def"), "defName") as string ?? "?";
                if (Once("c1:" + name, 80)) W("C1", "DrawCarriedThing def=" + name);
                Summary();
            }
            catch { }
        }

        // ---------------------------------------------------------------- C2
        private static void C2_Post(object __instance, object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 4) return;
                if (!InGame()) return;                       // touching materials during load deferred-resolves
                                                             // them and breaks PlayDataLoader - never do that
                string defName = Prop(__args[2], "defName") as string ?? "?";
                if (!Once("c2:" + defName, 60)) return;

                object mat = Prop(__instance, "MatSingle") ?? Prop(__instance, "matSingle") ?? Prop(__instance, "mat");
                string matName = mat is Array arr ? "[array " + arr.Length + "]" : (Prop(mat, "name") as string ?? "NULL");
                object tex = mat == null ? null : Tex(mat, "_MainTex");
                object mask = mat == null ? null : Tex(mat, "_MaskTex");
                object st = mat == null ? null : Vec(mat, "_MainTex_ST");

                Interlocked.Increment(ref _drawn);
                if (mat == null) Interlocked.Increment(ref _matNull);
                if (tex == null) Interlocked.Increment(ref _texNull);
                if (mask == null) Interlocked.Increment(ref _maskNull);

                W("C2", "draw def=" + defName + " gfx=" + __instance.GetType().Name
                        + " mat=" + matName
                        + " shader=" + Str(mat, "shader", "name")
                        + " mainTex=" + (tex == null ? "NULL" : (Prop(tex, "name") as string ?? "?"))
                        + " mask=" + (mask == null ? "-" : (Prop(mask, "name") as string ?? "?"))
                        + " st=" + (st == null ? "-" : st.ToString())
                        + " drawSize=" + Prop(__instance, "drawSize"));
                Summary();
            }
            catch { }
        }

        // ---------------------------------------------------------------- C3
        private static void C3_Pre(object[] __args)
        {
            try
            {
                bool meshNull = __args[0] == null;
                bool matNull = __args[2] == null;
                if (meshNull || matNull)
                {
                    int n = Interlocked.Increment(ref _drops);
                    if (n <= 40) W("C3", "dropped meshNull=" + meshNull + " matNull=" + matNull);
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------- R1
        private static void R1_Post(object __instance, bool __result)
        {
            try
            {
                if (__result) { Interlocked.Increment(ref _resident); return; }
                string name = Prop(__instance, "Name") as string ?? "?";
                string path = Prop(__instance, "SourcePath") as string ?? "-";
                if (!Once("r1:" + name + "|" + path, 120)) return;
                int n = Interlocked.Increment(ref _resFail);
                W("R1", "resfail #" + n + " name=" + name + " path=" + path
                        + " pixels=" + (Truthy(Prop(Prop(__instance, "CpuData"), "HasData")) ? "y" : "n")
                        + " size=" + Prop(__instance, "width") + "x" + Prop(__instance, "height"));
                Summary();
            }
            catch { }
        }

        // ---------------------------------------------------------------- R2
        private static void R2_Post(object __instance, bool __result)
        {
            try
            {
                if (__result) { Interlocked.Increment(ref _uploadOk); return; }
                Interlocked.Increment(ref _uploadFail);
                string path = Prop(__instance, "SourcePath") as string ?? "-";
                if (Once("r2:" + path, 60)) W("R2", "upload-from-cache FAILED path=" + path);
                Summary();
            }
            catch { }
        }

        // ---------------------------------------------------------------- R3
        private static void R3_Post(object[] __args, bool __result)
        {
            try
            {
                string path = __args != null && __args.Length > 0 ? __args[0] as string : "-";
                if (__result) { Interlocked.Increment(ref _cacheHit); return; }
                Interlocked.Increment(ref _cacheMiss);
                if (Once("r3:" + path, 60)) W("R3", "lookup MISS path=" + path);
                Summary();
            }
            catch { }
        }

        // ---------------------------------------------------------------- M1
        private static void M1_Pre(object[] __args)
        {
            try
            {
                object stored = __args[0];
                int want = __args[1] is int i ? i : -1;
                int have = Prop(stored, "MipCount") is int mi ? mi : -1;
                Interlocked.Increment(ref _chain);
                if (have >= 0 && want > have) Interlocked.Increment(ref _chainShort);
                if (Once("m1:" + have + "->" + want, 20))
                    W("M1", "completed chain storedMips=" + have + " wanted=" + want
                            + " size=" + Prop(stored, "Width") + "x" + Prop(stored, "Height")
                            + " fmt=" + Prop(stored, "Format"));
                Summary();
            }
            catch { }
        }

        // ---------------------------------------------------------------- summary
        private static void Summary()
        {
            long now = Environment.TickCount;
            if (now - _lastSummary < 20000) return;
            _lastSummary = now;
            W("S", "carried=" + _carried + " drawn=" + _drawn + " matNull=" + _matNull
                  + " texNull=" + _texNull + " maskNull=" + _maskNull
                  + " resfail=" + _resFail + " resident=" + _resident
                  + " upOk=" + _uploadOk + " upFail=" + _uploadFail
                  + " lookHit=" + _cacheHit + " lookMiss=" + _cacheMiss
                  + " chain=" + _chain + " chainShort=" + _chainShort + " drops=" + _drops);
        }

        // ---------------------------------------------------------------- helpers
        /// True only while a map is actually running; anything earlier must stay untouched.
        private static bool InGame()
        {
            try
            {
                object cur = Refl.T("Verse.Current");
                object state = cur == null ? null : Prop(cur, "ProgramState");
                string name = state?.ToString() ?? "";
                return name == "Playing" || name == "Map" || name == "Entry";
            }
            catch { return false; }
        }

        private static bool _entryLogged;

        private static object Tex(object mat, string prop)
        {
            try { return mat.GetType().GetMethod("GetTexture", new[] { typeof(string) })?.Invoke(mat, new object[] { prop }); }
            catch { return null; }
        }

        private static object Vec(object mat, string prop)
        {
            try { return mat.GetType().GetMethod("GetVector", new[] { typeof(string) })?.Invoke(mat, new object[] { prop }); }
            catch { return null; }
        }

        private static string Str(object o, string first, string second)
        {
            object b = Prop(Prop(o, first), second);
            return b as string ?? "?";
        }

        private static bool Truthy(object o) { return o is bool b && b; }

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
