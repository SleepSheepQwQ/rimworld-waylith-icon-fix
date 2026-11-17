using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// Miss-only extension fallback for texture lookups.
    ///
    /// The port keys its content index by the file name *including* the inner extension of a
    /// double-extension texture: "UniqueRevolverA_m.png.dds" and "Lynx_eastm.psd.dds" become
    /// "…/UniqueRevolverA_m.png" and "…/Lynx_eastm.psd" (Verse/ModContentHolder.cs:56-63 strips one
    /// extension; Verse/ModDdsLoader.cs:19,74 does the same for Texture2D.name). Vanilla asks for the
    /// bare name, because on desktop the index has no extension:
    ///
    ///   Graphic_Single.cs:97   maskTex = ContentFinder&lt;Texture2D&gt;.Get(path + "_m", false)
    ///   Graphic_Multi.cs:210   mask[0]  = ContentFinder&lt;Texture2D&gt;.Get("Lynx" + "_north" + "m", false)
    ///
    /// Every such lookup misses, the graphic is built with maskTex = null, the mask-aware shader
    /// samples an empty mask and the whole thing draws blank. That is the Odyssey unique weapons and
    /// every animal that has directional masks (Ibex, Lynx) - Alpaca and Cow have none and are fine.
    ///
    /// The repair is one postfix that fires only when the lookup already failed: retry the same path
    /// with ".png" / ".psd" / ".jpg" / ".dds" appended and return the first hit. Successful lookups
    /// are untouched, no index key is rewritten, and nothing is cached - including misses.
    ///
    /// Cost: zero on a hit (one null test). On a miss, up to four extra lookups plus one reflection
    /// call each; misses are load-time only and bounded (Graphic_Single/Graphic_Multi probe a handful
    /// of mask paths per graphic).
    /// </summary>
    internal static class ContentFallbackFix
    {
        private const int LogCap = 5;

        private static int _logged, _hits, _warned;

        // ContentFinder<Texture2D>.Get(string, bool) and ModContentHolder<Texture2D>.Get(string),
        // resolved on the closed generic types this port actually instantiates.
        private static MethodInfo _finderGet, _holderGet;

        [ThreadStatic] private static bool _busy;   // never start a second round from inside a retry

        internal static void Install()
        {
            Type tex = Refl.T("UnityEngine.Texture2D") ?? Refl.T("Waylith.Rendering.Texture2D");
            if (tex == null) { Refl.Warn("contentfix: no Texture2D type to close ContentFinder`1 with"); return; }

            // 1) the entry point Graphic_Single / Graphic_Multi call
            _finderGet = Closed(tex, "Verse.ContentFinder`1", "Get", new[] { typeof(string), typeof(bool) });
            if (_finderGet == null)
            {
                Refl.Warn("contentfix: ContentFinder<" + tex.Name + ">.Get(string, bool) not found");
            }
            else
            {
                Patch(_finderGet, "OnFinderMiss",
                      "contentfix: postfix on ContentFinder<" + tex.Name + ">.Get(string, bool)");
            }

            // 2) the layer underneath: mod and DLC content index lookups
            _holderGet = Closed(tex, "Verse.ModContentHolder`1", "Get", new[] { typeof(string) });
            if (_holderGet == null)
            {
                Refl.Warn("contentfix: ModContentHolder<" + tex.Name + ">.Get(string) not found");
            }
            else
            {
                Patch(_holderGet, "OnHolderMiss",
                      "contentfix: postfix on ModContentHolder<" + tex.Name + ">.Get(string)");
            }

            Refl.Info("contentfix: miss-only extension fallback armed ("
                      + string.Join(" ", ExtensionFallback.Extensions) + ")");
        }

        private static MethodInfo Closed(Type tex, string generic, string method, Type[] signature)
        {
            try
            {
                Type open = Refl.T(generic);
                if (open == null) return null;
                return open.MakeGenericType(tex).GetMethod(method,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance,
                    null, signature, null);
            }
            catch (Exception e)
            {
                Refl.Warn("contentfix: " + generic + "[" + tex.Name + "]." + method + ": " + e.Message);
                return null;
            }
        }

        private static void Patch(MethodInfo target, string postfixName, string what)
        {
            try
            {
                MethodInfo post = typeof(ContentFallbackFix).GetMethod(postfixName,
                    BindingFlags.Static | BindingFlags.NonPublic);
                new Harmony("sleepsheep.waylith.agenttouchfix.contentfix")
                    .Patch(target, postfix: new HarmonyMethod(post));
                Refl.Info(what);
            }
            catch (Exception e) { Refl.Warn("contentfix: patch failed on " + target.Name + ": " + e.Message); }
        }

        /// <summary>ContentFinder&lt;Texture2D&gt;.Get(string itemPath, bool reportFailure).</summary>
        private static void OnFinderMiss(string itemPath, ref object __result)
        {
            if (__result != null || _busy) return;
            Fallback(itemPath, null, ref __result);
        }

        /// <summary>ModContentHolder&lt;Texture2D&gt;.Get(string path).</summary>
        private static void OnHolderMiss(object __instance, string path, ref object __result)
        {
            if (__result != null || _busy || __instance == null) return;
            Fallback(path, __instance, ref __result);
        }

        private static void Fallback(string path, object instance, ref object result)
        {
            try
            {
                Func<string, object> load = instance == null
                    ? (Func<string, object>)TryFinder
                    : p => TryHolder(instance, p);

                string hitPath;
                int tried;
                object found;

                _busy = true;
                try { found = ExtensionFallback.FirstHit(path, load, out hitPath, out tried); }
                finally { _busy = false; }

                if (found == null) return;

                result = found;
                Interlocked.Increment(ref _hits);
                if (Interlocked.Increment(ref _logged) <= LogCap)
                {
                    Refl.Info("content: extension fallback '" + path + "' -> '" + hitPath + "'");
                }
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("contentfix: " + e.Message);
            }
        }

        /// <summary>
        /// Re-enter the patched lookup with the candidate path. Bounded: the retry either hits (the
        /// postfix stops) or misses, and Candidates() of a path that already carries an extension is
        /// empty, so a third round cannot start. _busy covers the rest.
        /// </summary>
        private static object TryFinder(string path)
        {
            try { return _finderGet == null ? null : _finderGet.Invoke(null, new object[] { path, false }); }
            catch { return null; }
        }

        private static object TryHolder(object instance, string path)
        {
            try { return _holderGet == null ? null : _holderGet.Invoke(instance, new object[] { path }); }
            catch { return null; }
        }
    }
}
