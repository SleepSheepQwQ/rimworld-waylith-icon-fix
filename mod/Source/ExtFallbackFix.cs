using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// Repairs texture lookups at the lookup layer, without touching the rule that decides which
    /// file owns a key.
    ///
    /// The defect this answers, measured on the real host (run 44738256380): a mod ships "Shadow.png"
    /// and "Shadow.dds" with the same name, ModContentLoader.ShadowedByDds deletes the png, and the
    /// dds is a format the loader refuses (DXT3). LoadItem catches the refusal and stores an item
    /// carrying BaseContent.BadTex, so the key exists and its value cannot be drawn: "Shadow
    /// hit=False" while "PngOnly" - the same bytes with no dds beside them - loads. On the device
    /// BadTex is a 2x2 placeholder; on the CI host it is null. Both mean "the loader failed", and
    /// neither means "the asset is absent".
    ///
    /// Two different retries, chosen by whether the holder already owns the key:
    ///
    ///   key present, value unusable  -> read &lt;mod&gt;/Textures/&lt;key&gt;.png and return the decoded
    ///                                   image through the port's own loader (the path a normal png
    ///                                   content item takes, so the pixels are not a second
    ///                                   decoder's idea of them);
    ///   key absent                   -> retry the path with ".png" / ".jpg" / ".jpeg" / ".psd"
    ///                                   against the same holder (the key of a double-extension
    ///                                   texture carries its inner extension: "Foo_m.png.dds" is
    ///                                   filed as "Foo_m.png" while Graphic_Single asks for "Foo_m").
    ///                                   A genuine miss stays a miss and never touches the disk.
    ///
    /// Why the postfix sits on ModContentHolder&lt;Texture2D&gt;.Get(string) and not on
    /// ContentFinder&lt;Texture2D&gt;.Get(string, bool): ContentFinder.Get loops the active mods and
    /// calls this holder method for each, keeping the last non-null answer - it *is* the lookup, one
    /// frame below the entry point. The entry point itself is unavailable: CaseFix already holds the
    /// result contract there (`ref T __result`), and Harmony 3.0 refuses a second one - measured,
    /// verbatim:
    ///
    ///   extfallback: ContentFinder&lt;Texture2D&gt;.Get refused: ArgumentException: The existing result
    ///   contract requires an assignable type: ... CaseFix::AfterGet(... T&amp; __result) ... supplied
    ///   UnityEngine.Texture2D, requested AgentTouchFix.T&amp;
    ///
    /// This is a deliberate second seam, not a weaker one: it runs per mod, so it knows *which* mod
    /// owns the key (no global index, no mod-root scan) and it answers before ContentFinder has to
    /// decide. It also leaves ShadowedByDds alone - that rule still deletes the png, the entry is
    /// repaired after the fact, which is why a dds that *does* decode still wins (its result is
    /// usable and is never replaced).
    ///
    /// Cost and safety:
    ///   * a usable result costs one reference test - the postfix returns before any dictionary or IO;
    ///   * an unusable one is answered once per (holder, key) and cached, including "nothing to load",
    ///     so a texture is decoded once and a repeated miss is one dictionary hit;
    ///   * the cache is dropped when a holder reloads (the textures it handed us are destroyed then);
    ///   * main thread only (Verse.UnityData.IsInMainThread), anyReloaded-gated for the same reason
    ///     ContentFinder gates on it;
    ///   * every exception is swallowed and logged once; the worst case is the behaviour without this
    ///     module.
    /// </summary>
    internal static class ExtFallbackFix
    {
        private const int LogCap = 6;
        private const int MaxEntries = 4096;

        /// <summary>Cache value for "the retry ran and found nothing" - distinct from "no entry yet".</summary>
        private static readonly object _none = new object();

        private static bool _ready;
        private static string _mode = "none";
        private static int _repaired, _warned;

        [ThreadStatic] private static bool _busy;   // never start a second retry from inside one

        private static Type _texType, _virtualFileType;
        private static ConstructorInfo _filesystemFileCtor;
        private static MethodInfo _finderGet, _holderGet, _loadItem, _loadTexture;
        private static FieldInfo _contentList, _contentItem, _modField, _anyReloaded, _badTexField;
        private static Member _rootDir, _isMainThread;
        private static object _badTex;

        /// <summary>Per holder cache; holders are per-mod singletons, so identity is the key.</summary>
        private static Dictionary<object, HolderCache> _cache;

        private sealed class HolderCache
        {
            internal int Count;                                  // contentList.Count when Map was built
            internal readonly Dictionary<string, object> Map = new Dictionary<string, object>(StringComparer.Ordinal);
        }

        internal static void Install()
        {
            if (_ready) return;
            try
            {
                _texType = Refl.T("UnityEngine.Texture2D");
                _virtualFileType = Refl.T("RimWorld.IO.VirtualFile");
                Type filesystemFile = Refl.T("RimWorld.IO.FilesystemFile");

                Type finderOpen = Refl.T("Verse.ContentFinder`1");
                Type holderOpen = Refl.T("Verse.ModContentHolder`1");
                Type loaderOpen = Refl.T("Verse.ModContentLoader`1");
                Type pack = Refl.T("Verse.ModContentPack");
                Type unityData = Refl.T("Verse.UnityData");

                if (_texType == null || _virtualFileType == null || filesystemFile == null
                    || finderOpen == null || holderOpen == null || loaderOpen == null || pack == null)
                {
                    Refl.Warn("extfallback: types missing, skipped");
                    return;
                }

                Type closedFinder = finderOpen.MakeGenericType(_texType);
                Type closedHolder = holderOpen.MakeGenericType(_texType);
                Type closedLoader = loaderOpen.MakeGenericType(_texType);

                _finderGet = closedFinder.GetMethod("Get", new[] { typeof(string), typeof(bool) });
                _holderGet = closedHolder.GetMethod("Get", new[] { typeof(string) });
                _contentList = closedHolder.GetField("contentList");
                _modField = closedHolder.GetField("mod");
                _anyReloaded = closedHolder.GetField("anyReloaded", BindingFlags.Public | BindingFlags.Static)
                               ?? closedHolder.GetField("anyReloaded", BindingFlags.NonPublic | BindingFlags.Static);
                _loadTexture = closedLoader.GetMethod("LoadTexture", new[] { _virtualFileType });
                _loadItem = closedLoader.GetMethod("LoadItem", new[] { _virtualFileType });
                _filesystemFileCtor = filesystemFile.GetConstructor(new[] { typeof(FileInfo) });

                _rootDir = Member.Resolve(pack, "rootDirInt", false, true);
                _isMainThread = Member.Resolve(unityData, "IsInMainThread", false, false);

                Type baseContent = Refl.T("Verse.BaseContent");
                _badTexField = baseContent?.GetField("BadTex", BindingFlags.Public | BindingFlags.Static)
                               ?? baseContent?.GetField("BadTex", BindingFlags.NonPublic | BindingFlags.Static);
                _badTex = _badTexField?.GetValue(null);

                if (_holderGet == null || _contentList == null || _modField == null
                    || _loadTexture == null || _filesystemFileCtor == null || _rootDir == null)
                {
                    Refl.Warn("extfallback: members not reachable, skipped"
                              + " (holderGet=" + (_holderGet != null) + " list=" + (_contentList != null)
                              + " mod=" + (_modField != null) + " load=" + (_loadTexture != null)
                              + " file=" + (_filesystemFileCtor != null) + " root=" + (_rootDir != null) + ")");
                    return;
                }

                _cache = new Dictionary<object, HolderCache>();
                _ready = true;

                // 1) The entry point, kept as the documented attempt: CaseFix owns its result
                //    contract, so this is expected to be refused and says so in the log.
                // 2) The holder lookup ContentFinder calls - the available seam.
                // 3) LoadItem, the last resort: correct, but mod content is already loaded by the
                //    time Bootstrap runs, so it only helps a later reload.
                if (!TryPostfix(_finderGet, nameof(AfterGet), "get", "ContentFinder<Texture2D>.Get(string, bool)"))
                {
                    if (!TryPostfix(_holderGet, nameof(AfterHolderGet), "holder", "ModContentHolder<Texture2D>.Get(string)"))
                    {
                        if (!TryPostfix(_loadItem, nameof(AfterLoadItem), "loaditem", "ModContentLoader<Texture2D>.LoadItem"))
                        {
                            Refl.Warn("extfallback: no hook could be installed");
                            return;
                        }

                        _contentItem = Refl.T("Verse.LoadedContentItem`1")
                            ?.MakeGenericType(_texType)?.GetField("contentItem");
                        if (_contentItem == null)
                            Refl.Warn("extfallback: LoadedContentItem.contentItem not reachable; the loaditem hook is inert");
                    }
                }

                // A holder reload destroys the textures it handed us; drop everything cached.
                TryPostfix(closedHolder.GetMethod("ReloadAll", new[] { typeof(bool) }), nameof(AfterReload), "reload", "ModContentHolder<Texture2D>.ReloadAll");
                TryPostfix(closedHolder.GetMethod("ClearDestroy", Type.EmptyTypes), nameof(AfterReload), "reload", "ModContentHolder<Texture2D>.ClearDestroy");

                Refl.Info("extfallback: armed at " + _mode
                          + " (unusable result -> " + string.Join("/", ExtFallbackDecision.Extensions)
                          + " retry, then the same-named .png of the mod that owns the key;"
                          + " per-holder cache, main thread only, anyReloaded-gated)");
            }
            catch (Exception e)
            {
                Refl.Warn("extfallback: install failed: " + Root(e).GetType().Name + ": " + Root(e).Message);
            }
        }

        private static bool TryPostfix(MethodInfo target, string postfixName, string mode, string what)
        {
            if (target == null) return false;
            try
            {
                MethodInfo post = typeof(ExtFallbackFix).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic);
                if (post == null) return false;
                new Harmony("sleepsheep.waylith.agenttouchfix.extfallback")
                    .Patch(target, postfix: new HarmonyMethod(post));
                if (mode != "reload" && _mode == "none") _mode = mode;
                Refl.Info("extfallback: postfix on " + what);
                return true;
            }
            catch (Exception e)
            {
                Refl.Warn("extfallback: " + what + " refused: " + Root(e).GetType().Name + ": " + Root(e).Message);
                return false;
            }
        }

        // ------------------------------------------------------------------ lookup layer

        /// <summary>
        /// ContentFinder&lt;Texture2D&gt;.Get(string, bool). Not reached in practice (CaseFix holds the
        /// result contract); kept so the refusal is measured and logged instead of assumed.
        /// </summary>
        private static void AfterGet(string itemPath, ref object __result)
        {
            Repair(null, itemPath, ref __result, "get");
        }

        /// <summary>The lookup ContentFinder performs for every active mod, in mod order.</summary>
        private static void AfterHolderGet(object __instance, string path, ref object __result)
        {
            Repair(__instance, path, ref __result, "holder");
        }

        /// <summary>
        /// Fallback installation: the item is created once, so the repair is a load-time substitution
        /// of the content item's texture. No per-lookup work at all.
        /// </summary>
        private static void AfterLoadItem(object __result)
        {
            try
            {
                if (!_ready || _contentItem == null || __result == null) return;
                object current = _contentItem.GetValue(__result);
                if (!ExtFallbackDecision.Unusable(current, BadTex())) return;
                if (!OnMainThread()) return;

                string path = InternalPath(__result);
                if (path == null || !path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) return;

                string image = path.Substring(0, path.Length - 4) + ".png";
                string hit = File.Exists(image) ? image : FindIgnoreCase(image);
                if (hit == null) return;

                object tex = Load(hit);
                if (tex == null) return;

                _contentItem.SetValue(__result, tex);
                LogRepair(path, hit);
            }
            catch (Exception e)
            {
                WarnOnce("loaditem: " + e.Message);
            }
        }

        /// <summary>Shared body of the two lookup postfixes.</summary>
        private static void Repair(object holder, string path, ref object __result, string seam)
        {
            try
            {
                if (!_ready || _busy) return;
                if (string.IsNullOrEmpty(path)) return;

                // The hot path: art that can be drawn is never second-guessed.
                if (!ExtFallbackDecision.Unusable(__result, BadTex())) return;
                if (!OnMainThread()) return;
                if (!AnyReloaded()) return;

                string key = KeyOf(path);
                if (key.Length == 0) return;

                HolderCache entry = CacheOf(holder);
                if (entry == null) return;

                // The list is the cache's generation: a reload, a hot reload or a mod list change
                // moves the count and everything cached for this holder is suspect.
                IDictionary list = holder == null ? null : _contentList.GetValue(holder) as IDictionary;
                int count = list == null ? 0 : list.Count;
                if (entry.Count != count) { entry.Map.Clear(); entry.Count = count; }

                object cached;
                if (entry.Map.TryGetValue(key, out cached))
                {
                    if (!ReferenceEquals(cached, _none)) __result = cached;
                    return;
                }

                bool present = list != null && list.Contains(key);

                int tier = ExtFallbackDecision.Choose(present, false);
                string how;
                object found = tier == ExtFallbackDecision.SameNameImage
                    ? LoadSameNameImage(holder, key, out how)
                    : RetryContentKeys(holder, key, out how);

                Remember(entry.Map, key, found);
                if (found == null) return;

                __result = found;
                LogRepair(key, how);
            }
            catch (Exception e)
            {
                WarnOnce(seam + " " + path + ": " + e.Message);
            }
        }

        // ------------------------------------------------------------------ retries

        /// <summary>
        /// Case A. Re-enter the same holder with an extension appended; _busy keeps the nested call
        /// from starting another round, and Candidates() of a path that already carries an extension
        /// is empty, so recursion is bounded.
        /// </summary>
        private static object RetryContentKeys(object holder, string key, out string how)
        {
            how = null;
            if (holder == null) return null;

            string[] candidates = ExtFallbackDecision.Candidates(key);
            if (candidates.Length == 0) return null;

            _busy = true;
            try
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    object found = null;
                    try { found = _holderGet.Invoke(holder, new object[] { candidates[i] }); }
                    catch { found = null; }

                    if (!ExtFallbackDecision.Unusable(found, BadTex()))
                    {
                        how = "key " + candidates[i];
                        return found;
                    }
                }
            }
            finally { _busy = false; }

            return null;
        }

        /// <summary>
        /// Case B. The holder owns the key and its value is unusable: read the same-named .png from
        /// that mod's Textures/ tree. Exact name first, then one case-insensitive pass (the port runs
        /// on a case-sensitive file system).
        /// </summary>
        private static object LoadSameNameImage(object holder, string key, out string how)
        {
            how = null;
            string name = ExtFallbackDecision.ImageName(key);
            if (name == null) return null;

            DirectoryInfo root = RootOf(holder);
            if (root == null) return null;

            string rel = name;
            if (rel.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring("Textures/".Length);

            string path = Path.Combine(root.FullName, "Textures", rel.Replace('/', Path.DirectorySeparatorChar));
            string hit = File.Exists(path) ? path : FindIgnoreCase(path);
            if (hit == null) return null;

            object tex = Load(hit);
            if (tex == null) return null;

            how = "file " + hit;
            return tex;
        }

        /// <summary>
        /// The port's own image path, the one a .png content item goes through
        /// (ModContentLoader&lt;Texture2D&gt;.LoadTexture -> Texture2D.LoadImage), so the pixels are
        /// the ones a normal png in a mod folder produces - not a second decoder's idea of them.
        /// </summary>
        private static object Load(string path)
        {
            try
            {
                object file = _filesystemFileCtor.Invoke(new object[] { new FileInfo(path) });
                object tex = _loadTexture.Invoke(null, new object[] { file });
                if (tex == null) WarnOnce("loader returned null for " + path);
                return tex;
            }
            catch (Exception e)
            {
                WarnOnce("load " + path + ": " + Root(e).GetType().Name + ": " + Root(e).Message);
                return null;
            }
        }

        // ------------------------------------------------------------------ cache

        private static HolderCache CacheOf(object holder)
        {
            if (holder == null) return null;
            HolderCache entry;
            if (!_cache.TryGetValue(holder, out entry))
            {
                entry = new HolderCache { Count = -1 };
                _cache[holder] = entry;
            }
            return entry;
        }

        private static void Remember(Dictionary<string, object> map, string key, object texture)
        {
            if (map.Count >= MaxEntries) map.Clear();
            map[key] = texture ?? _none;
        }

        /// <summary>Postfix on ReloadAll / ClearDestroy: the textures we cached are now destroyed.</summary>
        private static void AfterReload()
        {
            try { _cache?.Clear(); } catch { }
        }

        // ------------------------------------------------------------------ plumbing

        private static string KeyOf(string itemPath)
        {
            string key = itemPath.Replace('\\', '/').Trim();
            if (key.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase))
                key = key.Substring("Textures/".Length);
            key = key.TrimStart('/');
            if (key.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                key = key.Substring(0, key.Length - 4);
            return key;
        }

        private static DirectoryInfo RootOf(object holder)
        {
            try
            {
                object mod = holder == null ? null : _modField.GetValue(holder);
                if (mod == null) return null;
                DirectoryInfo root = _rootDir.Get(mod) as DirectoryInfo;
                return root != null && root.Exists ? root : null;
            }
            catch { return null; }
        }

        private static string InternalPath(object loadedItem)
        {
            try
            {
                Member file = Member.Resolve(loadedItem.GetType(), "internalFile", false, true);
                object vf = file?.Get(loadedItem);
                Member full = vf == null ? null : Member.Resolve(vf.GetType(), "FullPath", false, false);
                return full?.Get(vf) as string;
            }
            catch { return null; }
        }

        /// <summary>Exact name missed: one directory listing, compared the way Windows would.</summary>
        private static string FindIgnoreCase(string fullPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(fullPath);
                string leaf = Path.GetFileName(fullPath);
                if (dir == null || leaf == null || !Directory.Exists(dir)) return null;

                foreach (string candidate in Directory.GetFiles(dir))
                {
                    if (string.Equals(Path.GetFileName(candidate), leaf, StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
            }
            catch { }
            return null;
        }

        private static bool OnMainThread()
        {
            if (_isMainThread == null) return true;      // the boot path is the main thread
            try { return _isMainThread.Get(null) is bool b && b; }
            catch { return false; }
        }

        private static bool AnyReloaded()
        {
            try
            {
                if (_anyReloaded == null) return true;
                return _anyReloaded.GetValue(null) is bool b && b;
            }
            catch { return true; }
        }

        /// <summary>
        /// BaseContent.BadTex, read lazily: the field is only assigned by BaseContent's static
        /// constructor, which may not have run when this module installs. Null is a legal value on
        /// the CI host (there is no shipped 2x2 placeholder there), so a null reading is retried
        /// instead of being frozen at install time.
        /// </summary>
        private static object BadTex()
        {
            if (_badTex != null) return _badTex;
            try { _badTex = _badTexField?.GetValue(null); } catch { }
            return _badTex;
        }

        private static void LogRepair(string key, string how)
        {
            int n = Interlocked.Increment(ref _repaired);
            if (n <= LogCap) Refl.Info("extfallback: '" + key + "' <- " + how);
        }

        private static void WarnOnce(string message)
        {
            if (Interlocked.Increment(ref _warned) > 6) return;
            Refl.Warn("extfallback: " + message);
        }

        private static Exception Root(Exception e)
        {
            var t = e as TargetInvocationException;
            return t != null && t.InnerException != null ? t.InnerException : e;
        }
    }
}
