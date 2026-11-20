using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// Stops a .dds that cannot be decoded from deleting the .png that can.
    ///
    /// Verse.ModContentLoader&lt;Texture2D&gt;.LoadAllForMod builds the set of .dds paths in a mod and then,
    /// for every other file, asks ShadowedByDds(filename, ddsFiles): when "foo.png" sits next to
    /// "foo.dds", the png is skipped entirely - LoadItem never runs, nothing is logged, and the mod's
    /// only copy of the art is the .dds. If that .dds is one the loader refuses (DXT3/DXT2/DXT4/ATI1/
    /// ATI2/BC4U/BC5U all throw in DdsPixelFormat.ToTextureFormat) the result is BaseContent.BadTex:
    /// a blank square, with the working png sitting right beside it. Measured in CI run 44738256380:
    /// `Things/DdsProbe/Shadow hit=False` while `PngOnly`, the same pixels in a png with no .dds next
    /// to it, hit=True.
    ///
    /// The guard is a prefix on that one method, in both directions:
    ///
    ///   image file, same-named .dds exists, .dds does not decode  -> __result = false (do not shadow)
    ///   .dds file, does not decode, same-named image exists       -> __result = true  (drop the .dds)
    ///
    /// The second half is not optional. ModContentHolder.ReloadAll strips the extension and keys its
    /// dictionary by the remaining path, so "Shadow.dds" and "Shadow.png" are the same key, and
    /// GetAllFilesForMod enumerates the folder in ordinal order - "Shadow.dds" sorts before
    /// "Shadow.png". Dropping the png alone would leave the broken .dds owning the key (its LoadItem
    /// returns a LoadedContentItem carrying a null/BadTex item, which ReloadAll still stores), and the
    /// lookup would stay empty. Only removing the .dds from the yielded sequence lets the image take
    /// the key it should have had.
    ///
    /// Conversely, when the .dds *can* be decoded the original behaviour stands untouched: the .dds
    /// wins, exactly as on Windows. And a .dds with no same-named image is never looked at, so a broken
    /// DDS keeps failing on its own - this module hides no defect, it only stops one file from hiding
    /// another.
    ///
    /// Installation point matters: RimWorld queues StaticConstructorOnStartupUtility.CallAll() after
    /// LoadAllActiveMods(), and this mod's Bootstrap is a static constructor, so patching there would be
    /// too late for the content that is loaded at boot. The module initializer runs when the game loads
    /// GlyphFix.dll (ModAssemblyHandler.ReloadAll, before any mod's textures are read); Bootstrap's
    /// Install() call is then a no-op, and still a fallback if the early attempt could not reach
    /// Harmony.
    /// </summary>
    internal static class ShadowFix
    {
        private const int MaxCache = 8192;
        private const int LogCap = 8;

        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".psd" };

        private static readonly object _installLock = new object();
        private static readonly object _cacheLock = new object();

        private static bool _installed, _ready;
        private static int _warned, _logged;

        private static MethodInfo _runningMods;
        private static FieldInfo _folders;

        private static string[] _roots;
        private static int _rootStamp = int.MinValue;

        /// <summary>Full .dds path -> last-seen (length, mtime) and the verdict for those bytes.</summary>
        private static readonly Dictionary<string, Verdict> _verdicts =
            new Dictionary<string, Verdict>(StringComparer.Ordinal);

        /// <summary>Relative base path -> full path of a same-named image, "" when there is none.</summary>
        private static readonly Dictionary<string, string> _images =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private struct Verdict
        {
            internal long Length;
            internal long Ticks;
            internal bool Decodable;
            internal string Why;
        }

        // ------------------------------------------------------------------ install

        /// <summary>Runs before any mod content is read (see the class comment).</summary>
#pragma warning disable CA2255 // ModuleInitializer in a library: that is the whole point here
        [ModuleInitializer]
#pragma warning restore CA2255
        internal static void EarlyInstall()
        {
            try { Install(); }
            catch (Exception e)
            {
                // A throwing module initializer would fail the whole assembly load, which would cost
                // every other module in this DLL. Swallow, and let Bootstrap retry later.
                try { Refl.Warn("shadowfix: early install failed: " + e.GetType().Name + ": " + e.Message); }
                catch { }
            }
        }

        internal static void Install()
        {
            lock (_installLock)
            {
                if (_installed) return;
                try
                {
                    Type loader = Refl.T("Verse.ModContentLoader`1");
                    Type tex = Refl.T("UnityEngine.Texture2D");
                    Type pack = Refl.T("Verse.ModContentPack");
                    Type lmm = Refl.T("Verse.LoadedModManager");
                    if (loader == null || tex == null || pack == null || lmm == null)
                    {
                        Refl.Warn("shadowfix: types missing, skipped");
                        return;
                    }

                    MethodInfo target = loader.MakeGenericType(tex)
                        .GetMethod("ShadowedByDds", BindingFlags.Public | BindingFlags.Static, null,
                                   new[] { typeof(string), typeof(HashSet<string>) }, null);
                    PropertyInfo running = lmm.GetProperty("RunningModsListForReading",
                                                           BindingFlags.Public | BindingFlags.Static);
                    _runningMods = running == null ? null : running.GetGetMethod();
                    _folders = pack.GetField("foldersToLoadDescendingOrder",
                                             BindingFlags.Public | BindingFlags.Instance);
                    if (target == null || _runningMods == null || _folders == null)
                    {
                        Refl.Warn("shadowfix: ShadowedByDds / mod folders not reachable, skipped");
                        return;
                    }

                    MethodInfo prefix = typeof(ShadowFix).GetMethod(nameof(Prefix),
                                                                    BindingFlags.Static | BindingFlags.NonPublic);
                    new Harmony("sleepsheep.waylith.agenttouchfix.shadow")
                        .Patch(target, prefix: new HarmonyMethod(prefix));

                    _installed = true;
                    _ready = true;
                    Refl.Info("shadowfix: dds shadow guard armed on ModContentLoader<Texture2D>.ShadowedByDds"
                              + " (undecodable .dds loses to its png/jpg/psd sibling)");
                }
                catch (Exception e)
                {
                    Refl.Warn("shadowfix: install failed: " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        // ------------------------------------------------------------------ prefix

        /// <summary>
        /// Harmony prefix. Returns true (run the original, untouched) unless it has decided the
        /// extension-less content key must go to the image instead of the .dds.
        /// </summary>
        private static bool Prefix(string filename, HashSet<string> ddsFiles, ref bool __result)
        {
            try
            {
                if (!_ready || ddsFiles == null) return true;
                if (string.IsNullOrEmpty(filename) || filename.Length <= 4) return true;

                string norm = filename.Replace('\\', '/');
                string relBase = norm.Substring(0, norm.Length - 4);   // 4 = every extension but .jpeg here

                if (norm.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    // The .dds side. Only interesting when an image it is about to delete exists.
                    string image = FindImage(relBase);
                    if (image == null) return true;

                    string ddsPath = Find(relBase + ".dds");
                    if (ddsPath == null) return true;
                    if (Decodable(ddsPath, out string why)) return true;

                    __result = true;          // "shadowed": the loader skips this file
                    Say("shadowfix: '" + relBase + ".dds' dropped (" + why + "), image '"
                        + Path.GetFileName(image) + "' keeps the key");
                    return false;
                }

                // The image side: mirror the original's key computation before doing any work.
                string key = norm.ToLowerInvariant();
                string ddsKey = key.Substring(0, key.Length - 4) + ".dds";
                if (!ddsFiles.Contains(ddsKey)) return true;          // not shadowed anyway

                string path = Find(relBase + ".dds");
                if (path == null) return true;                        // cannot judge: leave the engine's answer
                if (Decodable(path, out string reason)) return true;   // decodable: the .dds must keep winning

                __result = false;         // do not shadow: the image loads as well
                Say("shadowfix: '" + relBase + "' -> png (dds unreadable: " + reason + ")");
                return false;
            }
            catch (Exception e)
            {
                WarnOnce("prefix: " + e.GetType().Name + ": " + e.Message);
                return true;
            }
        }

        private static void Say(string line)
        {
            int n = System.Threading.Interlocked.Increment(ref _logged);
            if (n <= LogCap) Refl.Info(line);
        }

        private static void WarnOnce(string m)
        {
            if (System.Threading.Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("shadowfix: " + m);
        }

        // ------------------------------------------------------------------ file resolution

        /// <summary>
        /// Every load folder of every running mod, later mods first (ContentFinder's own order).
        /// Cached until the folder layout changes, which only happens on a mod reload.
        /// </summary>
        private static string[] Roots()
        {
            object value;
            try { value = _runningMods.Invoke(null, null); }
            catch { return _roots; }

            var mods = value as IList;
            if (mods == null) return _roots;

            int stamp = mods.Count;
            var all = new List<string>();
            for (int i = mods.Count - 1; i >= 0; i--)
            {
                var folders = _folders.GetValue(mods[i]) as List<string>;
                if (folders == null) continue;
                stamp = unchecked(stamp * 31 + folders.Count);
                for (int j = folders.Count - 1; j >= 0; j--)
                {
                    string f = folders[j];
                    if (!string.IsNullOrEmpty(f)) all.Add(f);
                }
            }

            lock (_cacheLock)
            {
                if (_roots != null && stamp == _rootStamp) return _roots;
                _roots = all.ToArray();
                _rootStamp = stamp;
                _images.Clear();
            }
            return _roots;
        }

        /// <summary>Full path of <paramref name="rel"/> under the first running mod that has it.</summary>
        private static string Find(string rel)
        {
            string[] roots = Roots();
            if (roots == null) return null;
            string tail = rel.Replace('/', Path.DirectorySeparatorChar);
            for (int i = 0; i < roots.Length; i++)
            {
                try
                {
                    string p = Path.Combine(roots[i], tail);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        private static string FindImage(string relBase)
        {
            lock (_cacheLock)
            {
                string cached;
                if (_images.TryGetValue(relBase, out cached))
                    return cached.Length == 0 ? null : cached;
            }

            string found = null;
            for (int i = 0; i < ImageExtensions.Length && found == null; i++)
                found = Find(relBase + ImageExtensions[i]);

            lock (_cacheLock)
            {
                if (_images.Count >= MaxCache) _images.Clear();
                _images[relBase] = found ?? "";
            }
            return found;
        }

        // ------------------------------------------------------------------ header cache

        /// <summary>
        /// Reads the first 148 bytes of <paramref name="path"/> and caches the verdict against the
        /// file's length and mtime, so a texture pack with a thousand .dds files costs one header read
        /// each and nothing on the second pass.
        /// </summary>
        private static bool Decodable(string path, out string why)
        {
            why = "";
            long length = -1, ticks = 0;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) { why = "missing"; return false; }
                length = fi.Length;
                ticks = fi.LastWriteTimeUtc.Ticks;
            }
            catch (Exception e)
            {
                why = "unreadable (" + e.GetType().Name + ")";
                return false;
            }

            lock (_cacheLock)
            {
                Verdict v;
                if (_verdicts.TryGetValue(path, out v) && v.Length == length && v.Ticks == ticks)
                {
                    why = v.Why;
                    return v.Decodable;
                }
            }

            bool ok = false;
            ShadowDdsHead head;
            try
            {
                var buf = new byte[ShadowDds.Dx10HeaderBytes];
                int read = 0;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int n;
                    while (read < buf.Length && (n = fs.Read(buf, read, buf.Length - read)) > 0) read += n;
                }
                if (!ShadowDds.TryRead(buf, read, out head))
                {
                    why = read < ShadowDds.BaseHeaderBytes ? "short file (" + read + " bytes)" : "bad header";
                }
                else if (!head.Decodable)
                {
                    why = ShadowDds.Describe(head) + " is not a format the loader decodes";
                }
                else if (!ShadowDds.PayloadComplete(head, length))
                {
                    why = ShadowDds.Describe(head) + " payload truncated (" + length + " bytes)";
                }
                else
                {
                    ok = true;
                    why = ShadowDds.Describe(head);
                }
            }
            catch (Exception e)
            {
                why = "read failed (" + e.GetType().Name + ")";
            }

            lock (_cacheLock)
            {
                if (_verdicts.Count >= MaxCache) _verdicts.Clear();
                _verdicts[path] = new Verdict
                {
                    Length = length,
                    Ticks = ticks,
                    Decodable = ok,
                    Why = why,
                };
            }
            return ok;
        }
    }
}
