using System;
using System.Collections.Generic;

namespace AgentTouchFix
{
    /// <summary>
    /// Drops mask textures from a Graphic_Collection's variant list - conservatively.
    ///
    /// Verse/Graphic_Collection.cs:58-66 builds that list like this:
    ///
    ///     from x in ContentFinder&lt;Texture2D&gt;.GetAllInFolder(req.path)
    ///     where !x.name.EndsWith("_m")        // vanilla's mask filter
    ///     orderby x.name
    ///     select (x, x.name.Split('_')[0])
    ///
    /// On this port x.name keeps the inner extension of a double-extension texture
    /// ("UniqueRevolverA_m.png"), so EndsWith("_m") matches nothing and every mask survives into
    /// subGraphics next to the texture it belongs to. Graphic_Random then picks
    /// subGraphics[thingIDNumber % count], i.e. roughly half of the items draw the mask itself.
    ///
    /// Why the shape check. The filter runs on a list the game already built, so a wrong guess would
    /// silently renumber variants of graphics that were fine (Graphic_MealVariants divides
    /// subGraphics.Length by 3, Graphic_StackCount switches on it). So the result is adopted only
    /// when the list is *fully* explained by "texture plus its mask":
    ///
    ///   - before == after * 2                        (every second entry is a mask)
    ///   - every pairing key holds exactly two names   (one texture, one mask - not two textures,
    ///                                                  not two masks, not a lone mask)
    ///
    /// Anything else returns the input untouched and reports
    /// "collection: variant shape unexpected, left untouched (n=&lt;count&gt;)" once, which is how a
    /// collection we did not model shows up in the log instead of corrupting the variant order.
    ///
    /// Pure string logic, no game types - tests/TexSt compiles this file directly.
    /// </summary>
    internal static class CollectionVariants
    {
        /// <summary>Prefix of the one log line the shape check can emit; the count is appended.</summary>
        internal const string ShapeLog = "collection: variant shape unexpected, left untouched (n=";

        /// <summary>Texture names minus their masks; the input itself when the shape is not understood.</summary>
        internal static IReadOnlyList<string> Filter(IReadOnlyList<string> names)
        {
            return Filter(names, null);
        }

        /// <summary>
        /// As above, reporting an unexpected shape to <paramref name="log"/> (null to stay silent).
        /// </summary>
        internal static IReadOnlyList<string> Filter(IReadOnlyList<string> names, Action<string> log)
        {
            if (names == null || names.Count == 0) return Array.Empty<string>();

            List<int> keep = Survivors(names);
            if (keep == null)
            {
                if (log != null) log(ShapeLog + names.Count + ")");
                return names;                       // untouched: same instance, same order
            }

            var kept = new List<string>(keep.Count);
            for (int i = 0; i < keep.Count; i++) kept.Add(names[keep[i]]);
            return kept;
        }

        /// <summary>
        /// Indices of the non-mask entries, ascending. Null when the shape is not the "every texture
        /// is paired with its own mask" shape this repair is allowed to touch; empty for empty input.
        /// Never logs - <see cref="Filter"/> owns the message so callers cannot double-report.
        /// </summary>
        internal static List<int> Survivors(IReadOnlyList<string> names)
        {
            if (names == null || names.Count == 0) return new List<int>();

            var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var keys = new List<string>();

            for (int i = 0; i < names.Count; i++)
            {
                string key = MaskName.VariantKey(names[i]);
                if (key == null) return null;               // blank entry: not a list we understand

                List<int> group;
                if (!groups.TryGetValue(key, out group))
                {
                    group = new List<int>(2);
                    groups[key] = group;
                    keys.Add(key);
                }
                group.Add(i);
            }

            var keep = new List<int>(names.Count / 2);
            for (int k = 0; k < keys.Count; k++)
            {
                List<int> group = groups[keys[k]];
                if (group.Count != 2) return null;          // two textures, two masks, or an orphan

                int maskAt = -1;
                for (int j = 0; j < group.Count; j++)
                {
                    if (!MaskName.IsVariantMask(names[group[j]])) continue;
                    if (maskAt >= 0) return null;           // two masks under one key
                    maskAt = group[j];
                }
                if (maskAt < 0) return null;                // two textures under one key

                keep.Add(group[0] == maskAt ? group[1] : group[0]);
            }

            if (keep.Count * 2 != names.Count) return null;  // implied by the per-group test, kept explicit
            keep.Sort();                                     // restore the game's own name order
            return keep;
        }
    }
}
