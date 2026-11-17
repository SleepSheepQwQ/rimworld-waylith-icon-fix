using System;

namespace AgentTouchFix
{
    /// <summary>
    /// "Is this texture name a graphic mask?" - pure string logic, no game types, so CI can unit-test
    /// it without RimWorld, Harmony or a device.
    ///
    /// Why this exists. Verse.Graphic_Collection.Init builds its variants like this
    /// (Verse/Graphic_Collection.cs:58-66):
    ///
    ///     from x in ContentFinder&lt;Texture2D&gt;.GetAllInFolder(req.path)
    ///     where !x.name.EndsWith("_m")          // "MaskSuffix" of Graphic_Single
    ///     orderby x.name
    ///     select (x, x.name.Split('_')[0])       // grouping key = variant prefix
    ///
    /// `x.name` comes from Path.GetFileNameWithoutExtension, which strips exactly one extension. DLC
    /// textures are named "&lt;tex&gt;.png" and ship as "&lt;tex&gt;.png.dds", so on this port the name comes
    /// back as "UniqueRevolverA.png": the "_m" test fails for "UniqueRevolverA_m.png", the mask is
    /// kept, and Graphic_Random picks it for roughly half of the instances (thingIDNumber % count),
    /// which draws an empty tile. Vanilla never sees this because GetFileNameWithoutExtension eats
    /// the ".dds" only, leaving "UniqueRevolverA.png" as well - the port's own Texture2D.name
    /// plumbing (Waylith.Rendering/Texture2D.cs:779) is what leaks the inner extension through.
    ///
    /// So: strip every trailing texture extension, then apply vanilla's suffix test.
    ///
    /// Two repairs use this rule. <see cref="ContentFallbackFix"/> makes the mask lookups *succeed*
    /// (index keys keep the inner extension, so a miss-only retry with ".png/.psd/.jpg/.dds"
    /// appended finds them). <see cref="CollectionVariants"/> drops what slipped into the variant
    /// list, which is the other half of the same bug: Graphic_Random indexes subGraphics by
    /// thingIDNumber % count, so a surviving mask is drawn for roughly half of the instances.
    ///
    /// Animals name their masks per direction - "Lynx_northm", "_eastm", "_southm", "_westm" - which
    /// is a different suffix family (Graphic_Multi.cs:210 appends "_north" + "m"), so both forms are
    /// recognised here and both peel the inner extension first.
    /// </summary>
    internal static class MaskName
    {
        /// <summary>Graphic_Single.MaskSuffix on this game version.</summary>
        internal const string Suffix = "_m";

        /// <summary>
        /// Directional mask suffixes: Graphic_Multi asks for text + "_north" + "m" per rotation, so
        /// the file is "&lt;base&gt;_northm". Lowercase, like the textures themselves and like the
        /// game's own name.Contains("_north") test.
        /// </summary>
        private static readonly string[] DirectionSuffixes = { "_northm", "_eastm", "_southm", "_westm" };

        /// <summary>
        /// Extensions a texture ships under. A port-side name keeps the inner one, so peeling has to
        /// run more than once ("X_m.jpg.dds" -&gt; "X_m.jpg" -&gt; "X_m").
        /// </summary>
        private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".psd", ".dds" };

        /// <summary>
        /// True when <paramref name="name"/> is a mask for some other texture: it ends with "_m" once
        /// its extensions are peeled off, and "X" is left in front of that suffix.
        /// </summary>
        internal static bool IsMask(string name)
        {
            string s = Strip(name);
            if (s == null) return false;
            if (s.Length <= Suffix.Length) return false;                       // "_m" alone names nothing
            if (!s.EndsWith(Suffix, StringComparison.Ordinal)) return false;   // "X", "X_magazine", "X_m2"

            // "Xm_m" / "XM_m": the base texture itself ends with the mask letter, so vanilla's
            // Split('_')[0] grouping would put the mask under a different key than the texture it
            // belongs to. No released texture has this shape (the 64 Odyssey unique-weapon masks are
            // all "<Name><A-E>_m"), and calling it a mask would silently drop a real variant.
            char before = s[s.Length - Suffix.Length - 1];
            return before != 'm' && before != 'M';
        }

        /// <summary>Peel every known texture extension, case-insensitively. Null for blank input.</summary>
        private static string Strip(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string s = name.Trim();
            if (s.Length == 0) return null;

            bool peeled = true;
            while (peeled)
            {
                peeled = false;
                foreach (string ext in Extensions)
                {
                    if (s.Length <= ext.Length) continue;
                    if (!s.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) continue;
                    s = s.Substring(0, s.Length - ext.Length);
                    peeled = true;
                    break;
                }
            }
            return s;
        }

        /// <summary>
        /// True when <paramref name="name"/> is a per-direction mask ("Lynx_northm", "Lynx_eastm.psd",
        /// "Lynx_westm.png.dds"). The extension is peeled first, so the port's inner extension cannot
        /// hide the suffix - the same failure mode that lets "_m" masks through.
        /// </summary>
        internal static bool IsDirectionMask(string name)
        {
            string s = Strip(name);
            if (s == null) return false;
            foreach (string d in DirectionSuffixes)
            {
                if (s.Length <= d.Length) continue;                             // "_northm" alone names nothing
                if (s.EndsWith(d, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Both mask families: Graphic_Single's "X_m" and Graphic_Multi's per-direction ones.</summary>
        internal static bool IsVariantMask(string name)
        {
            return IsMask(name) || IsDirectionMask(name);
        }

        /// <summary>
        /// The pairing key of a variant: its name with every extension peeled and its mask suffix
        /// removed. "X_m.png" and "X.png" both key as "X"; "Lynx_northm.psd" and "Lynx_north.psd"
        /// both key as "Lynx_north". Null for blank input, which no variant list may contain.
        /// </summary>
        internal static string VariantKey(string name)
        {
            string s = Strip(name);
            if (s == null) return null;
            if (IsMask(s)) return s.Substring(0, s.Length - Suffix.Length);
            if (IsDirectionMask(s)) return s.Substring(0, s.Length - 1);   // "_northm" -> "_north"
            return s;
        }
    }
}
