using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace AgentTouchFix
{
    /// <summary>
    /// Verified eviction: a texture may only be evicted when we can prove it can be brought back.
    ///
    /// The port decides eviction from metadata (Texture2D.cs:414 -> GpuTextureService.HasValidTextureCache:4308)
    /// but rehydrates from real data (TryOpenPackTexture:4205 / TextureCacheStore.TryOpen) and additionally
    /// requires the format to match (Texture2D.cs:370). Where the two disagree, an evicted texture can never
    /// come back and every draw using it silently renders nothing - that is the missing held weapons.
    ///
    /// Gates, in order:
    ///   G0 nothing to gain / nothing to lose  -> skip
    ///   G1 in-flight (used within graceFrames) -> skip, the command buffer still references the image
    ///   G2 pinned (UI, small, procedural)      -> skip
    ///   G3 proof: real open + format match     -> allow (proof cached for `texproof` seconds)
    /// Above the memory ceiling G2/G3 are waived so the process is never killed; the waiver is counted.
    /// </summary>
    internal static class TexVerify
    {
        private const string Log = "textrace: ";

        // No configuration. Three constants and one automatic budget derived from the device.
        private const int GraceFrames = 3;      // frames an image must be untouched before eviction
        private const int ProofTtl = 30;        // seconds a "can rehydrate" verdict is trusted
        private const int Cooldown = 60;        // seconds before retrying an unprovable texture
        private static long _capBytes = 1536L * 1024L * 1024L;   // replaced by AutoCap() at startup

        private static int _ok, _skipUnprovable, _skipPinned, _skipInflight, _skipNoVal, _hard;
        private static int _skipBuild, _skipDirty, _frameResets, _rssUnknown;
        private static int _cooldown, _proofHits, _proofMisses;
        private static long _lastReportMs;

        // key = sourcePath|premultiplied|format|wxh  ->  epoch seconds when the proof expires
        private static readonly object _sync = new object();
        private static readonly Dictionary<string, long> _proofs = new Dictionary<string, long>(StringComparer.Ordinal);
        // key as above -> epoch seconds until which we stop trying to prove it (protects against I/O thrash)
        private static readonly Dictionary<string, long> _cool = new Dictionary<string, long>(StringComparer.Ordinal);

        private static MethodInfo _openCache;
        private static PropertyInfo _frameSerial;
        private static bool _resolved;

        internal static void Init()
        {
            if (Killed()) { Refl.Info(Log + "kill switch present, not installing"); return; }
            _capBytes = AutoCap();
            TryResolve();
            MethodInfo evict = typeof(TexVerify).GetMethod("Evict_Prefix",
                BindingFlags.Static | BindingFlags.NonPublic);
            var target = Refl.T("Waylith.Rendering.Texture2D");
            MethodInfo m = target == null ? null : target.GetMethod("Evict",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (m == null) { Refl.Warn(Log + "Texture2D.Evict() not found"); return; }

            // Compatibility probe: if another mod already patches Texture2D.Evict(), we run with
            // Priority.Low (their verdict wins) and, in strict mode, we do not install at all.
            string compat = "solo";
            try
            {
                Patches info = HarmonyLib.Harmony.GetPatchInfo(m);
                if (info != null && info.Prefixes != null)
                {
                    var others = new List<string>();
                    foreach (Patch p in info.Prefixes)
                        if (p.owner != "sleepsheep.waylith.agenttouchfix.texverify") others.Add(p.owner);
                    if (others.Count > 0) compat = string.Join("+", others);
                }
            }
            catch { }

            // Conflict handling is automatic: priority Low means their verdict runs first, and a
            // failure in our gate always falls through to the original behaviour.

            new HarmonyLib.Harmony("sleepsheep.waylith.agenttouchfix.texverify")
                .Patch(m, new HarmonyMethod(typeof(TexVerify).GetMethod("Evict_Prefix",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)));
            Refl.Info(Log + "verified eviction installed (grace=" + GraceFrames + "f proof=" + ProofTtl
                      + "s cap=" + (_capBytes >> 20) + "MiB compat=" + compat + ")");
        }

        private static void TryResolve()
        {
            if (_resolved) return;
            _resolved = true;
            Type svc = Refl.T("Waylith.Rendering.GpuTextureService");
            if (svc != null)
            {
                _openCache = svc.GetMethod("TryOpenTextureCache",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                _frameSerial = svc.GetProperty("RenderFrameSerial",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            }
        }

        // ------------------------------------------------------------------ the gate
        [HarmonyPriority(Priority.Low)]   // Run after other mods' prefixes: if any of them already
                                          // decided this eviction, we must not override it.
        internal static bool Evict_Prefix(object __instance)
        {
            try
            {
                if (__instance == null) return true;
                object gpu = Prop(__instance, "GpuResource");
                if (gpu == null) { Interlocked.Increment(ref _skipNoVal); return false; }

                string src = Prop(__instance, "SourcePath") as string;
                object cpu = Prop(__instance, "CpuData");
                if (src == null || cpu == null) { Interlocked.Increment(ref _skipNoVal); return false; }

                if (Prop(__instance, "PendingBuild") != null)
                {
                    Interlocked.Increment(ref _skipBuild);            // a build in flight would land on a disposed image
                    return false;
                }
                object dirty = Prop(cpu, "Dirty");
                if (dirty is bool && (bool)dirty)
                {
                    Interlocked.Increment(ref _skipDirty);            // CPU edits are not in pack/cache: never evict
                    return false;
                }
                if ((bool)Prop(cpu, "HasData")) return true;          // re-upload is cheap and certain

                long now = Now();
                long cap = _capBytes;

                // G1 in-flight
                object lastObj = Prop(gpu, "LastUseFrame");
                long last = lastObj is long ? (long)lastObj : -1L;
                long frame = FrameSerial();
                if (frame >= 0 && last > frame)
                {
                    Interlocked.Increment(ref _frameResets);           // frame serial restarted; treat as stale
                }
                else if (frame >= 0 && last >= 0 && frame - last < GraceFrames)
                {
                    Interlocked.Increment(ref _skipInflight);
                    Report(now);
                    return false;
                }

                long rss = Rss();
                bool overCap = rss > 0 && rss > cap;   // unknown rss (0) must not look like headroom

                // G2 pinned
                if (!overCap)
                {
                    string name = Prop(__instance, "Name") as string ?? "";
                    int w = IntOf(__instance, "width"), h = IntOf(__instance, "height");
                    if (IsPinned(name, w, h, last, frame))
                    {
                        Interlocked.Increment(ref _skipPinned);
                        Report(now);
                        return false;
                    }
                }

                // G3 proof
                bool premul = (bool)Prop(cpu, "AlphaIsPremultiplied");
                object linObj = Prop(cpu, "Linear");
                bool linear = linObj is bool && (bool)linObj;
                string fmt = Convert.ToString(Prop(cpu, "Format"));
                string key = src + "|" + premul + "|" + linear + "|" + fmt;

                long until;
                bool proofHit = false, cooled = false;
                lock (_sync)
                {
                    if (_proofs.TryGetValue(key, out until))
                    {
                        if (until > now) proofHit = true;
                        else _proofs.Remove(key);
                    }
                    if (!proofHit && _cool.TryGetValue(key, out until) && until > now) cooled = true;
                }
                if (proofHit)
                {
                    Interlocked.Increment(ref _proofHits);
                    Interlocked.Increment(ref _ok);
                    Report(now);
                    return true;
                }
                if (cooled)
                {
                    Interlocked.Increment(ref _cooldown);
                    if (overCap) { Interlocked.Increment(ref _hard); return true; }
                    return false;
                }

                string reason;
                if (CanRehydrate(src, premul, fmt, out reason))
                {
                    lock (_sync) { _proofs[key] = now + ProofTtl; _cool.Remove(key); }
                    Interlocked.Increment(ref _proofMisses);
                    Interlocked.Increment(ref _ok);
                    Report(now);
                    return true;
                }

                if (overCap)
                {
                    // Never let the process die for a texture: waive the proof, keep the frame gate.
                    Interlocked.Increment(ref _hard);
                    Refl.Info(Log + "hard-evict (over cap " + (rss >> 20) + "MiB) path=" + src + " reason=" + reason);
                    return true;
                }

                lock (_sync) { _cool[key] = now + Cooldown; }
                Interlocked.Increment(ref _skipUnprovable);
                if (_skipUnprovable <= 40)
                    Refl.Info(Log + "skip unprovable path=" + src + " reason=" + reason
                              + " want=" + fmt + " size=" + IntOf(__instance, "width") + "x" + IntOf(__instance, "height"));
                Report(now);
                return false;
            }
            catch (Exception e)
            {
                Refl.Warn(Log + "gate error, allowing evict: " + e.GetType().Name + " " + e.Message);
                return true;
            }
        }

        // ------------------------------------------------------------------ proof
        private static bool CanRehydrate(string src, bool premultiplied, string wantFormat, out string reason)
        {
            reason = "";
            if (_openCache == null) { reason = "no-api"; return true; }   // fail open: never block eviction on a broken probe
            object[] args = { src, premultiplied, null };
            object ok;
            try { ok = _openCache.Invoke(null, args); }
            catch (Exception e) { reason = "throw:" + e.InnerException?.GetType().Name; return true; }

            object file = args[2];
            if (!(ok is bool) || !(bool)ok || file == null) { reason = "open-failed"; return false; }

            try
            {
                string got = Convert.ToString(Prop(file, "Format"));
                if (wantFormat != null && got != null && got != wantFormat)
                {
                    reason = "format " + got + "!=" + wantFormat;
                    return false;
                }
                int mips = IntOf(file, "MipCount");
                if (mips <= 0) { reason = "no-mips"; return false; }
            }
            finally
            {
                try { (file as IDisposable)?.Dispose(); } catch { }
            }
            return true;
        }

        // ------------------------------------------------------------------ pinning
        private static bool IsPinned(string name, int w, int h, long lastFrame, long frame)
        {
            if (name.StartsWith("UI/", StringComparison.Ordinal) || name.Contains("UI/"))
                return true;
            if ((long)w * h * 4 <= 256 * 1024) return true;                     // small: not worth reclaiming
            if (frame >= 0 && lastFrame >= 0 && frame - lastFrame < 300) return true;  // used in the last ~5 s
            return false;
        }

        // ------------------------------------------------------------------ plumbing
        // Emergency only: if this file exists the mod does nothing at all. No other switches.
        private static bool Killed()
        {
            try { return System.IO.File.Exists("/data/data/com.ports1de.waylith/files/game/agenttouchfix.off"); }
            catch { return false; }
        }

        /// Budget = 45% of physical RAM, clamped to [512 MiB, 3 GiB]. Derived, never configured:
        /// low-RAM phones keep more room for the OS, big phones stop hoarding textures.
        private static long AutoCap()
        {
            long total = 0;
            try
            {
                foreach (string line in System.IO.File.ReadAllLines("/proc/meminfo"))
                {
                    if (!line.StartsWith("MemTotal:")) continue;
                    string[] parts = line.Split(new[] { ' ', '	' }, StringSplitOptions.RemoveEmptyEntries);
                    total = long.Parse(parts[1]) * 1024L;
                    break;
                }
            }
            catch { }
            if (total <= 0) total = 3L * 1024L * 1024L * 1024L;
            long cap = (long)(total * 0.45);
            if (cap < 512L << 20) cap = 512L << 20;
            if (cap > 3L << 30) cap = 3L << 30;
            return cap;
        }

        private static long _rss, _rssMs;
        private static long Rss()
        {
            long now = Now();
            if (now - _rssMs < 2 && _rss > 0) return _rss;
            _rssMs = now;
            try
            {
                string[] p = System.IO.File.ReadAllText("/proc/self/statm").Split(' ');
                _rss = long.Parse(p[1]) * 4096L;
            }
            catch { _rss = 0; Interlocked.Increment(ref _rssUnknown); }
            return _rss;
        }

        private static long FrameSerial()
        {
            try { return _frameSerial == null ? -1 : Convert.ToInt64(_frameSerial.GetValue(null, null)); }
            catch { return -1; }
        }

        private static long Now() { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond; }

        private static int IntOf(object o, string name)
        {
            try { object v = Prop(o, name); return v == null ? 0 : Convert.ToInt32(v); }
            catch { return 0; }
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
                    FieldInfo f = c.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null) return f.GetValue(o);
                }
            }
            catch { }
            return null;
        }

        private static void Report(long now)
        {
            long ms = Environment.TickCount;
            if (ms - _lastReportMs < 30000) return;
            _lastReportMs = ms;
            Refl.Info(Log + "evict allow=" + _ok + " skip{unprovable=" + _skipUnprovable
                      + " pinned=" + _skipPinned + " inflight=" + _skipInflight + " noval=" + _skipNoVal
                      + " build=" + _skipBuild + " dirty=" + _skipDirty + "} hard=" + _hard
                      + " frameReset=" + _frameResets + " rssUnknown=" + _rssUnknown + " cooldown=" + _cooldown
                      + " proof{hit=" + _proofHits + " real=" + _proofMisses + " cached=" + _proofs.Count + "}"
                      + " rss=" + (Rss() >> 20) + "MiB cap=" + (_capBytes >> 20) + "MiB");
        }
    }
}
