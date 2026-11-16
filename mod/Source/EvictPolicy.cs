using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace AgentTouchFix
{
    /// <summary>
    /// Eviction policy: only drop a texture we can re-open cheaply.
    ///
    /// Why: the port evicts when HasValidTextureCache() (a metadata lookup, GpuTextureService.cs:4308) says
    /// the pack "has" the entry, but rehydration needs that entry to actually decode
    /// (TryOpenPackTexture -> AssetPack.TryRent, ContentPack.cs:269). When metadata says yes and the read
    /// says no, the texture is gone for good and every draw using it silently renders nothing - the
    /// missing held weapons.
    ///
    /// Cheap policy, no I/O in the steady state and no repeated read/write of pixels:
    ///
    ///     a. loose file on disk            -> evict   (re-decode later, no bookkeeping)
    ///     b. we already persisted it once  -> evict   (the cache serves it)
    ///     c. pixels still in RAM           -> write the cache once, then evict
    ///     d. none of the above             -> keep resident (never lose what we cannot rebuild)
    ///
    /// Steady state cost: one memoised File.Exists per path per minute. Pixels are written at most once
    /// per texture, and never again on re-eviction.
    /// </summary>
    internal static class EvictPolicy
    {
        private const string Log = "evict: ";
        private const int MemoTtlMs = 60000;
        private const int MemoMax = 8192;

        private static MethodInfo _writeCache;
        private static bool _resolved;

        private static int _evictFile, _evictDurable, _persisted, _kept, _forced;
        private static long _keptBytes, _keptBudget;
        private static long _lastReportMs;

        private sealed class Memo { public long Until; public bool Value; }
        private static readonly object _sync = new object();
        private static readonly Dictionary<string, Memo> _exists = new Dictionary<string, Memo>(StringComparer.Ordinal);
        private static readonly HashSet<string> _durable = new HashSet<string>(StringComparer.Ordinal);

        [ThreadStatic] private static bool _busy;

        internal static void Init()
        {
            if (Killed()) { Refl.Info(Log + "kill switch present, not installing"); return; }
            if (!Resolve()) return;

            Type t = Refl.T("Waylith.Rendering.Texture2D");
            MethodInfo target = t == null ? null : t.GetMethod("Evict",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (target == null) { Refl.Warn(Log + "Texture2D.Evict() not found"); return; }

            new HarmonyLib.Harmony("sleepsheep.waylith.agenttouchfix.evict").Patch(target,
                new HarmonyMethod(typeof(EvictPolicy).GetMethod("Evict_Prefix",
                    BindingFlags.Static | BindingFlags.NonPublic)));
            Refl.Info(Log + "cheap-reopen policy installed (compat=" + Compat(target) + ")");
        }

        private static bool Resolve()
        {
            if (_resolved) return _writeCache != null;
            _resolved = true;
            Type svc = Refl.T("Waylith.Rendering.GpuTextureService");
            _writeCache = svc?.GetMethod("WriteTextureCache",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), Refl.T("Waylith.Rendering.Texture2D") }, null);
            if (_writeCache == null) { Refl.Warn(Log + "WriteTextureCache not found"); return false; }
            return true;
        }

        // ------------------------------------------------------------------ the whole policy
        [HarmonyPriority(Priority.Low)]
        internal static bool Evict_Prefix(object __instance)
        {
            if (_busy || __instance == null) return true;
            try
            {
                string src = Prop(__instance, "SourcePath") as string;
                if (src == null) { Keep("no-source"); return false; }

                if (FileExists(src)) { Count(ref _evictFile); return true; }

                string key = src + "|" + Prop(__instance, "width") + "x" + Prop(__instance, "height");
                if (Durable(key)) { Count(ref _evictDurable); return true; }

                object cpu = Prop(__instance, "CpuData");
                if (cpu == null || !Truthy(Prop(cpu, "HasData")))
                {
                    if (Pressured())
                    {
                        // Keeping without pixels is free of I/O but not of RAM. Under pressure we accept
                        // the engine's own gamble rather than risk being killed: this is the only path
                        // that can still lose a texture, and it is counted and logged.
                        Interlocked.Increment(ref _forced);
                        Report();
                        return true;
                    }
                    Keep(Size(__instance), "no-pixels");
                    return false;
                }

                _busy = true;
                try
                {
                    _writeCache.Invoke(null, new object[] { src, __instance });
                    MarkDurable(key);
                    Count(ref _persisted);
                }
                catch (Exception e) { Keep(Size(__instance), "write-failed:" + (e.InnerException?.GetType().Name ?? e.GetType().Name)); }
                finally { _busy = false; }

                return false;   // WriteTextureCache already queued this eviction
            }
            catch (Exception e)
            {
                Refl.Warn(Log + "gate error, deferring to the engine: " + e.GetType().Name + " " + e.Message);
                return true;
            }
        }

        // ------------------------------------------------------------------ memoised facts
        private static bool FileExists(string path)
        {
            long now = Environment.TickCount;
            lock (_sync)
            {
                Memo m;
                if (_exists.TryGetValue(path, out m) && m.Until > now) return m.Value;
            }

            bool value;
            try { value = System.IO.File.Exists(path); }
            catch { value = false; }

            lock (_sync)
            {
                if (_exists.Count > MemoMax) _exists.Clear();
                _exists[path] = new Memo { Until = now + MemoTtlMs, Value = value };
            }
            return value;
        }

        private static bool Durable(string key) { lock (_sync) return _durable.Contains(key); }

        private static void MarkDurable(string key)
        {
            lock (_sync)
            {
                if (_durable.Count > MemoMax) _durable.Clear();
                _durable.Add(key);
            }
        }

        private static void Count(ref int counter)
        {
            Interlocked.Increment(ref counter);
            Report();
        }

        private static long Size(object tex)
        {
            try
            {
                int w = Convert.ToInt32(Prop(tex, "width") ?? 0);
                int h = Convert.ToInt32(Prop(tex, "height") ?? 0);
                return (long)w * h * 4;
            }
            catch { return 0; }
        }

        /// Budget for textures we keep resident instead of evicting: 25% of RAM, so the zero-I/O path
        /// is bounded and a texture-heavy save cannot grow us into the low-memory killer.
        private static bool Pressured()
        {
            if (_keptBudget == 0)
            {
                long total = 0;
                try
                {
                    foreach (string line in System.IO.File.ReadAllLines("/proc/meminfo"))
                        if (line.StartsWith("MemTotal:"))
                        {
                            total = long.Parse(line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[1]) * 1024L;
                            break;
                        }
                }
                catch { }
                if (total <= 0) total = 4L << 30;
                _keptBudget = (long)(total * 0.25);
                Refl.Info(Log + "keep budget " + (_keptBudget >> 20) + "MiB");
            }
            return Interlocked.Read(ref _keptBytes) > _keptBudget;
        }

        private static void Keep(long bytes, string why)
        {
            Interlocked.Add(ref _keptBytes, bytes);
            int n = Interlocked.Increment(ref _kept);
            if (n <= 20) Refl.Info(Log + "kept (" + why + ")");
            Report();
        }

        private static void Report()
        {
            long ms = Environment.TickCount;
            if (ms - _lastReportMs < 60000) return;
            _lastReportMs = ms;
            Refl.Info(Log + "file=" + _evictFile + " durable=" + _evictDurable + " persisted=" + _persisted
                      + " kept=" + _kept + "(" + (Interlocked.Read(ref _keptBytes) >> 20) + "MiB)"
                      + " forced=" + _forced + " rss=" + (Rss() >> 20) + "MiB");
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
                var others = new List<string>();
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
