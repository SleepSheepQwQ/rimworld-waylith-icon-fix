using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// Postfix on Verse.Graphic_Collection.Init that drops mask textures from subGraphics.
    ///
    /// Graphic_Collection.Init (Verse/Graphic_Collection.cs:58-66) is supposed to leave masks out of
    /// the variant list, but its test is !x.name.EndsWith("_m") and this port's Texture2D.name keeps
    /// the inner extension of a double-extension texture ("UniqueRevolverA_m.png"), so the test never
    /// matches. The mask ends up as a sibling variant and Graphic_Random draws it for roughly half of
    /// the instances (thingIDNumber % subGraphics.Length).
    ///
    /// ContentFallbackFix repairs the *lookup* half of that bug (the mask texture now resolves, so the
    /// real variant is no longer blank); this repairs the *list* half. Both are needed: with only the
    /// fallback the mask is a legible mask instead of a blank tile, which is still the wrong sprite.
    ///
    /// The decision itself is <see cref="CollectionVariants"/>: we hand it the file name behind each
    /// subgraphic and act only when it says the list is fully explained by texture/mask pairs. Every
    /// other shape - a normal collection with no masks, a Multi-only collection, a folder we have not
    /// modelled - leaves subGraphics exactly as the game built it. Load-time only, once per graphic.
    /// </summary>
    internal static class CollectionFix
    {
        private const int LogCap = 5;

        private static int _logged, _dropped, _warned;

        private static FieldInfo _subGraphics, _path;
        private static Type _collectionType;

        internal static void Install()
        {
            try
            {
                Type collection = Refl.T("Verse.Graphic_Collection");
                if (collection == null)
                {
                    Refl.Warn("collection: Verse.Graphic_Collection not loaded, mask filter skipped");
                    return;
                }
                _collectionType = collection;

                MethodInfo init = Refl.M(collection, "Init", 1);
                if (init == null)
                {
                    Refl.Warn("collection: Graphic_Collection.Init(GraphicRequest) not found, mask filter skipped");
                    return;
                }

                _subGraphics = Field(collection, "subGraphics");
                _path = Field(Refl.T("Verse.Graphic") ?? collection.BaseType, "path");
                if (_subGraphics == null || _path == null)
                {
                    Refl.Warn("collection: subGraphics/path not reachable by reflection, mask filter skipped");
                    return;
                }

                MethodInfo postfix = typeof(CollectionFix).GetMethod("AfterInit",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (postfix == null)
                {
                    Refl.Warn("collection: postfix method missing, mask filter skipped");
                    return;
                }

                new Harmony("sleepsheep.waylith.agenttouchfix.collection")
                    .Patch(init, postfix: new HarmonyMethod(postfix));

                Refl.Info("collection: mask filter armed (postfix on Verse.Graphic_Collection.Init)");
            }
            catch (Exception e)
            {
                // A missing collection class is not a reason to fail the load: the other fixes stand on
                // their own, and the game without this one is what shipped before.
                Refl.Warn("collection: install failed: " + e.Message);
            }
        }

        /// <summary>
        /// Verse.Graphic_Collection.Init(GraphicRequest) - reads subGraphics, asks
        /// <see cref="CollectionVariants"/> which entries are real variants, and swaps in a shorter
        /// array when (and only when) the shape check passed.
        /// </summary>
        private static void AfterInit(object __instance)
        {
            try
            {
                if (__instance == null) return;
                FilterInPlace(__instance);
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("collection: " + e.Message);
            }
        }

        /// <summary>
        /// Repairs collections that were built before the mod could install its Init postfix. Defs and
        /// their graphic objects are constructed during content load, before StaticConstructorOnStartup;
        /// without this pass a player who enables GlyphFix still has the leaked masks in every existing
        /// unique-weapon collection for the whole session.
        /// </summary>
        internal static void RepairLoaded()
        {
            if (_collectionType == null || _subGraphics == null || _path == null) return;
            try
            {
                Type thingDef = Refl.T("Verse.ThingDef");
                Type database = Refl.T("Verse.DefDatabase`1");
                if (thingDef == null || database == null) return;

                object listValue = Member.Resolve(database.MakeGenericType(thingDef),
                    "AllDefsListForReading", false, false)?.Get(null);
                IEnumerable defs = listValue as IEnumerable;
                if (defs == null) return;

                Member graphicMember = Member.Resolve(thingDef, "graphic", false, true);
                if (graphicMember == null) return;

                var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
                int scanned = 0, collections = 0, before = 0, after = 0;
                foreach (object def in defs)
                {
                    if (def == null) continue;
                    scanned++;
                    object graphic = graphicMember.Get(def);
                    object collection = InnerCollection(graphic);
                    if (collection == null || !seen.Add(collection)) continue;
                    collections++;

                    Array current = _subGraphics.GetValue(collection) as Array;
                    if (current == null) continue;
                    before += current.Length;
                    if (FilterInPlace(collection))
                        after += ((Array)_subGraphics.GetValue(collection)).Length;
                    else
                        after += current.Length;
                }

                if (collections > 0)
                {
                    Refl.Info("collection: existing graphic scan defs=" + scanned
                              + " collections=" + collections + " variants=" + before
                              + " -> " + after);
                }
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref _warned, 1) == 0)
                    Refl.Warn("collection: existing graphic scan failed: " + e.Message);
            }
        }

        /// <summary>Filter one Graphic_Collection array in place. Returns true when it was shortened.</summary>
        private static bool FilterInPlace(object collection)
        {
            Array current = _subGraphics.GetValue(collection) as Array;
            if (current == null || current.Length < 2) return false;

            var names = new string[current.Length];
            for (int i = 0; i < current.Length; i++)
            {
                object graphic = current.GetValue(i);
                names[i] = graphic == null ? null : Leaf((string)_path.GetValue(graphic));
            }

            List<int> keep = CollectionVariants.Survivors(names);
            if (keep == null)
            {
                if (Interlocked.Increment(ref _logged) <= LogCap)
                    Refl.Info(CollectionVariants.ShapeLog + names.Length + ")");
                return false;
            }
            if (keep.Count == names.Length) return false;

            Type element = current.GetType().GetElementType();
            Array rebuilt = Array.CreateInstance(element, keep.Count);
            for (int i = 0; i < keep.Count; i++) rebuilt.SetValue(current.GetValue(keep[i]), i);
            _subGraphics.SetValue(collection, rebuilt);

            int n = Interlocked.Increment(ref _dropped);
            if (n <= LogCap)
            {
                Refl.Info("collection: dropped " + (names.Length - keep.Count) + " mask variant(s) at '"
                          + Leaf((string)_path.GetValue(collection)) + "', "
                          + names.Length + " -> " + keep.Count);
            }
            return true;
        }

        /// <summary>Unwrap one or more Graphic subGraphic wrappers to the innermost collection.</summary>
        private static object InnerCollection(object graphic)
        {
            for (int depth = 0; graphic != null && depth < 8; depth++)
            {
                if (_collectionType.IsInstanceOfType(graphic)) return graphic;
                FieldInfo child = Field(graphic.GetType(), "subGraphic");
                if (child == null) return null;
                object next = child.GetValue(graphic);
                if (next == null || ReferenceEquals(next, graphic)) return null;
                graphic = next;
            }
            return null;
        }

        /// <summary>Last path segment - the file name a texture was loaded under, or the folder key.</summary>
        private static string Leaf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int slash = path.LastIndexOfAny(new[] { '/', '\\' });
            return slash < 0 ? path : path.Substring(slash + 1);
        }

        /// <summary>Field by name, walking up: subGraphics lives on Graphic_Collection, path on Graphic.</summary>
        private static FieldInfo Field(Type t, string name)
        {
            while (t != null)
            {
                FieldInfo f = t.GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }
    }
}
