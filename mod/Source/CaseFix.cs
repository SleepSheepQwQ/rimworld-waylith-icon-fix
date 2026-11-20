using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AgentTouchFix
{
    /// <summary>
    /// Makes texture lookups case-insensitive, the way they are on Windows.
    ///
    /// Verse.ContentFinder&lt;Texture2D&gt;.Get walks each mod's ModContentHolder and does
    /// contentList.TryGetValue(itemPath) - an ordinal dictionary lookup. The shipped content is not
    /// consistent with itself: Core ships
    ///
    ///     Resources/Textures/Things/Pawn/Animal/Ibex/IbexMALE_east.psd.dds.split     (capital MALE)
    ///
    /// while ThingDef Races_Animal_* asks for
    ///
    ///     &lt;texPath&gt;Things/Pawn/Animal/Ibex/IbexMale&lt;/texPath&gt;                          (IbexMale)
    ///
    /// On Windows the file system hides that; on Android/Linux the lookup misses, the main texture is
    /// null, and Graphic_Multi draws nothing - one whole animal, while its siblings (IbexFemale_*,
    /// which is spelled consistently) render fine.
    ///
    /// The fix is a postfix on the closed generic: only when the lookup returned null, consult an
    /// index of the same keys lower-cased, and return the holder's own value for the real key. Normal
    /// hits never touch the index; it is rebuilt only when a holder's key count changes.
    /// </summary>
    internal static class CaseFix
    {
        private static MethodInfo _get;          // ContentFinder<Texture2D>.Get(string, bool)
        private static MethodInfo _getContentHolder; // ModContentPack.GetContentHolder<Texture2D>()
        private static MethodInfo _holderGet;    // ModContentHolder<Texture2D>.Get(string)
        private static FieldInfo _contentList;   // ModContentHolder<Texture2D>.contentList
        private static PropertyInfo _runningMods; // LoadedModManager.RunningModsListForReading

        private static Dictionary<string, KeyValuePair<object, string>> _index;
        private static int _stamp = int.MinValue;

        internal static void Install()
        {
            try
            {
                Type tex = Refl.T("UnityEngine.Texture2D");
                Type finder = Refl.T("Verse.ContentFinder`1");
                Type holder = Refl.T("Verse.ModContentHolder`1");
                Type pack = Refl.T("Verse.ModContentPack");
                Type lmm = Refl.T("Verse.LoadedModManager");
                if (tex == null || finder == null || holder == null || pack == null || lmm == null)
                {
                    Refl.Warn("casefix: types missing, skipped");
                    return;
                }

                Type closedFinder = finder.MakeGenericType(tex);
                Type closedHolder = holder.MakeGenericType(tex);

                _get = closedFinder.GetMethod("Get", new[] { typeof(string), typeof(bool) });
                _getContentHolder = pack.GetMethod("GetContentHolder")?.MakeGenericMethod(tex);
                _holderGet = closedHolder.GetMethod("Get", new[] { typeof(string) });
                _contentList = closedHolder.GetField("contentList");
                _runningMods = lmm.GetProperty("RunningModsListForReading", BindingFlags.Public | BindingFlags.Static);

                if (_get == null || _getContentHolder == null || _holderGet == null || _contentList == null || _runningMods == null)
                {
                    Refl.Warn("casefix: members not reachable, skipped");
                    return;
                }

                MethodInfo postfix = typeof(CaseFix).GetMethod(nameof(AfterGet), BindingFlags.Static | BindingFlags.NonPublic);
                new Harmony("sleepsheep.waylith.agenttouchfix.case")
                    .Patch(_get, postfix: new HarmonyMethod(postfix));
                Refl.Info("casefix: case-insensitive miss fallback armed");
            }
            catch (Exception e)
            {
                Refl.Warn("casefix: install failed: " + e.Message);
            }
        }

        /// <summary>Runs only when the ordinary lookup produced nothing.</summary>
        private static void AfterGet(string itemPath, ref Texture2D __result)
        {
            try
            {
                if (__result != null || string.IsNullOrEmpty(itemPath)) return;

                IList mods = _runningMods.GetValue(null, null) as IList;
                if (mods == null) return;

                EnsureIndex(mods);
                if (_index == null) return;

                KeyValuePair<object, string> hit;
                if (!_index.TryGetValue(itemPath, out hit)) return;

                object value = _holderGet.Invoke(hit.Key, new object[] { hit.Value });
                Texture2D tex = value as Texture2D;
                if (tex == null) return;

                __result = tex;
                int n = System.Threading.Interlocked.Increment(ref _recovered);
                if (n <= 3) Refl.Info("casefix: '" + itemPath + "' -> '" + hit.Value + "'");
            }
            catch (Exception e)
            {
                if (System.Threading.Interlocked.Exchange(ref _warned, 1) == 0)
                    Refl.Warn("casefix: " + e.Message);
            }
        }

        private static int _recovered, _warned;

        private static void EnsureIndex(IList mods)
        {
            int count = mods.Count;
            var holders = new object[count];
            var lists = new IDictionary[count];
            int stamp = count;

            for (int i = 0; i < count; i++)
            {
                object holder = _getContentHolder.Invoke(mods[i], null);
                IDictionary list = holder == null ? null : _contentList.GetValue(holder) as IDictionary;
                holders[i] = holder;
                lists[i] = list;
                stamp = unchecked(stamp * 31 + (list == null ? 0 : list.Count));
            }

            if (_index != null && stamp == _stamp) return;

            var index = new Dictionary<string, KeyValuePair<object, string>>(StringComparer.OrdinalIgnoreCase);
            for (int i = count - 1; i >= 0; i--)          // later mods win, like ContentFinder's loop
            {
                IDictionary list = lists[i];
                if (list == null || list.Count == 0) continue;

                foreach (DictionaryEntry entry in list)
                {
                    string key = entry.Key as string;
                    if (key == null || index.ContainsKey(key)) continue;
                    index[key] = new KeyValuePair<object, string>(holders[i], key);
                }
            }

            _index = index;
            _stamp = stamp;
        }

    }
}
