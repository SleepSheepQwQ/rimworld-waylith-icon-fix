using System;
using System.Collections.Generic;

namespace AgentTouchFix
{
    /// <summary>
    /// Which paths to retry when a content lookup misses - pure, no game types, unit-tested by CI.
    ///
    /// The port keeps the inner extension in every content key. Textures ship as
    /// "UniqueRevolverA_m.png.dds" / "Lynx_eastm.psd.dds" and the key is built by stripping exactly
    /// one extension (Verse/ModContentHolder.cs:56-63, Path.GetExtension), so the index holds
    /// "…/UniqueRevolverA_m.png" and "…/Lynx_eastm.psd". Texture2D.name goes the same way
    /// (Verse/ModDdsLoader.cs:19,74 - Path.GetFileNameWithoutExtension).
    ///
    /// Vanilla asks for the name *without* any extension, because on desktop the index is keyed that
    /// way:
    ///   Graphic_Single.cs:97   ContentFinder&lt;Texture2D&gt;.Get(maskPath ?? path + "_m", false)
    ///   Graphic_Multi.cs:210   ContentFinder&lt;Texture2D&gt;.Get(text + "_north" + "m", false)   // "Lynx_northm"
    /// Every mask lookup therefore misses, maskTex stays null, the shader samples an empty mask and
    /// the whole tile renders blank: Odyssey's 13 unique weapons (both their "_m" and their plain
    /// textures, plus their masks) and every animal whose directional masks exist (Ibex, Lynx - while
    /// Alpaca and Cow, which have no masks, are fine).
    ///
    /// So the repair is a miss-only retry: append the extension the index actually used. Nothing is
    /// tried when the first lookup succeeded, no existing key is rewritten and no negative result is
    /// cached - the retry runs only on the (rare) miss.
    /// </summary>
    internal static class ExtensionFallback
    {
        /// <summary>Retry order. ".psd" first is the common case for DLC art with a source file.</summary>
        internal static readonly string[] Extensions = { ".png", ".psd", ".jpg", ".dds" };

        /// <summary>
        /// Extensions that mean "this already is a file name" - appending to one of them would only
        /// build "X.png.png" style keys, which never exist.
        /// </summary>
        private static readonly string[] Known = { ".png", ".psd", ".jpg", ".jpeg", ".dds" };

        /// <summary>
        /// Candidate paths for <paramref name="path"/>, in a stable order. Empty when there is
        /// nothing to try: blank input, or a path that already carries a texture extension.
        /// </summary>
        internal static string[] Candidates(string path)
        {
            string p = string.IsNullOrEmpty(path) ? null : path.Trim();
            if (string.IsNullOrEmpty(p)) return Array.Empty<string>();
            if (HasExtension(p)) return Array.Empty<string>();

            string[] result = new string[Extensions.Length];
            for (int i = 0; i < Extensions.Length; i++)
            {
                result[i] = p + Extensions[i];
            }
            return result;
        }

        /// <summary>
        /// First candidate that <paramref name="load"/> resolves, or null. Stops at the first hit -
        /// <paramref name="tried"/> reports how many lookups that took, so a test can prove the loop
        /// does not keep going after a hit.
        /// </summary>
        internal static T FirstHit<T>(string path, Func<string, T> load, out string hitPath, out int tried)
            where T : class
        {
            hitPath = null;
            tried = 0;
            if (load == null) return null;

            string[] candidates = Candidates(path);
            for (int i = 0; i < candidates.Length; i++)
            {
                tried++;
                T value = load(candidates[i]);
                if (value != null)
                {
                    hitPath = candidates[i];
                    return value;
                }
            }
            return null;
        }

        /// <summary>Path extension test: last segment only, so "Mods/Some.Mod/Textures/Foo" still appends.</summary>
        private static bool HasExtension(string path)
        {
            int slash = path.LastIndexOfAny(new[] { '/', '\\' });
            string leaf = slash < 0 ? path : path.Substring(slash + 1);
            if (leaf.Length == 0) return false;

            foreach (string ext in Known)
            {
                if (leaf.Length > ext.Length && leaf.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
