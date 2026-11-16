using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace AgentTouchFix
{
    /// <summary>
    /// Eviction policy: persist, then drop.
    ///
    /// The port decides eviction from metadata (Texture2D.cs:414 -> HasValidTextureCache) but rehydrates
    /// from data (TryOpenPackTexture / TextureCacheStore), and additionally needs the format to match
    /// (TryUploadFromCache). Where the two disagree an evicted texture can never come back, and every
    /// draw using it silently renders nothing - the missing held weapons.
    ///
    /// Rather than guarding each eviction with heuristics (frame gates, pinning, proofs, memory
    /// ceilings), this restores the invariant the engine already has a primitive for:
    ///
    ///     no texture is ever dropped before its pixels are durable
    ///
    /// GpuTextureService.WriteTextureCache(path, texture) writes the cache and releases the GPU copy -
    /// exactly what an eviction means. So the fix is one rule:
    ///
    ///     if the cache cannot serve this texture -> write it first, drop afterwards
    ///
    /// Everything else (who calls Evict, how often, how big the texture is, how much RAM is free)
    /// becomes irrelevant, and the port's own reclaim keeps working.
    /// </summary>
    internal static class EvictPolicy
    {
        private const string Log = "evict: ";

        private static int _fast, _persisted, _kept;
        private static long _lastReportMs;

        private static MethodInfo _hasValid, _writeCache;
        private static PropertyInfo _frameSerial;
        private static bool _resolved;

        [ThreadStatic] private static bool _busy;

        internal static void Init()
        {
            if (Killed()) { Refl.Info(Log + "kill switch present, not installing"); return; }

            if (!Resolve()) return;

            MethodInfo evict = typeof(EvictPolicy).GetMethod("Evict_Prefix",
                BindingFlags.Static | BindingFlags.NonPublic);
            Type t = Refl.T("Waylith.Rendering.Texture2D");
            MethodInfo target = t == null ? null : t.GetMethod("Evict",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (target == null) { Refl.Warn(Log + "Texture2D.Evict() not found"); return; }

            string compat = Compat(target);
            new HarmonyLib.Harmony("sleepsheep.waylith.agenttouchfix.evict").Patch(target, new HarmonyMethod(evict));
            Refl.Info(Log + "persist-then-drop installed (compat=" + compat + ")");
        }

        private static bool Resolve()
        {
            if (_resolved) return _hasValid != null && _writeCache != null;
            _resolved = true;
            Type svc = Refl.T("Waylith.Rendering.GpuTextureService");
            if (svc == null) { Refl.Warn(Log + "GpuTextureService not found"); return false; }

            _hasValid = svc.GetMethod("HasValidTextureCache",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(bool), typeof(int), typeof(int) }, null);
            _writeCache = svc.GetMethod("WriteTextureCache",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), Refl.T("Waylith.Rendering.Texture2D") }, null);
            if (_hasValid == null || _writeCache == null)
            {
                Refl.Warn(Log + "engine primitives missing (hasValid=" + (_hasValid != null)
                          + " writeCache=" + (_writeCache != null) + ")");
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ the whole policy
        [HarmonyPriority(Priority.Low)]      // other mods decide first; we never override a refusal
        internal static bool Evict_Prefix(object __instance)
        {
            if (_busy || __instance == null) return true;
            try
            {
                if (Prop(__instance, "GpuResource") == null) return false;      // nothing to free

                string src = Prop(__instance, "SourcePath") as string;
                object cpu = Prop(__instance, "CpuData");
                if (src == null || cpu == null) { Keep("no-source"); return false; }

                bool premultiplied = Truthy(Prop(cpu, "AlphaIsPremultiplied"));
                int w = Convert.ToInt32(Prop(__instance, "width") ?? 0);
                int h = Convert.ToInt32(Prop(__instance, "height") ?? 0);

                if (CacheServes(src, premultiplied, w, h))
                {
                    Interlocked.Increment(ref _fast);                            // reversible as-is
                    Report();
                    return true;
                }

                if (!Truthy(Prop(cpu, "HasData"))) { Keep("no-pixels"); return false; }

                // Pixels only exist in RAM: make them durable first, then let the engine drop the image.
                _busy = true;
                try
                {
                    _writeCache.Invoke(null, new object[] { src, __instance });
                    Interlocked.Increment(ref _persisted);
                }
                catch (Exception e)
                {
                    Keep("write-failed:" + (e.InnerException?.GetType().Name ?? e.GetType().Name));
                }
                finally { _busy = false; }

                Report();
                return false;   // WriteTextureCache already queued the eviction; do not evict twice
            }
            catch (Exception e)
            {
                Refl.Warn(Log + "gate error, deferring to the engine: " + e.GetType().Name + " " + e.Message);
                return true;
            }
        }

        private static bool CacheServes(string path, bool premultiplied, int w, int h)
        {
            try { return Truthy(_hasValid.Invoke(null, new object[] { path, premultiplied, w, h })); }
            catch { return true; }   // unknown -> let the engine do what it always did
        }

        // ------------------------------------------------------------------ plumbing
        private static void Keep(string why)
        {
            int n = Interlocked.Increment(ref _kept);
            if (n <= 20) Refl.Info(Log + "kept resident (" + why + ")");
            Report();
        }

        private static void Report()
        {
            long ms = Environment.TickCount;
            if (ms - _lastReportMs < 60000) return;
            _lastReportMs = ms;
            Refl.Info(Log + "reversible=" + _fast + " persisted=" + _persisted + " kept=" + _kept
                      + " rss=" + (Rss() >> 20) + "MiB");
        }

        private static long _rss, _rssMs;
        private static long Rss()
        {
            long now = Environment.TickCount;
            if (now - _rssMs < 2000 && _rss > 0) return _rss;
            _rssMs = now;
            try
            {
                string[] p = System.IO.File.ReadAllText("/proc/self/statm").Split(' ');
                _rss = long.Parse(p[1]) * 4096L;
            }
            catch { _rss = 0; }
            return _rss;
        }

        private static string Compat(MethodInfo target)
        {
            try
            {
                Patches info = HarmonyLib.Harmony.GetPatchInfo(target);
                if (info?.Prefixes == null) return "solo";
                var others = new System.Collections.Generic.List<string>();
                foreach (Patch p in info.Prefixes)
                    if (p.owner != "sleepsheep.waylith.agenttouchfix.evict") others.Add(p.owner);
                return others.Count == 0 ? "solo" : string.Join("+", others);
            }
            catch { return "unknown"; }
        }

        private static bool Killed()
        {
            try { return System.IO.File.Exists("/data/data/com.ports1de.waylith/files/game/agenttouchfix.off"); }
            catch { return false; }
        }

        private static bool Truthy(object o) { return o is bool && (bool)o; }

        private static object Prop(object o, string name)
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
