using System;
using System.Collections.Generic;

namespace AgentTouchFix
{
    /// <summary>
    /// Chooses the texture path IconFieldFix should ask ContentFinder for.
    ///
    /// Graphic_Collection.path is the containing folder (".../Revolver"), not a texture. The actual
    /// variant name is on each Collection.subGraphics[i].path. BuildableDef.ResolveIcon resolves a
    /// collection through ExtractInnerGraphicFor(null), which is subGraphics[0], so the first
    /// non-mask variant is the matching fallback. A collection whose array is empty or contains only
    /// masks keeps its own path and lets the existing ContentFinder behaviour stand.
    /// </summary>
    internal static class IconVariantPath
    {
        internal static string Choose(IReadOnlyList<string> variants, string fallback)
        {
            if (variants != null)
            {
                for (int i = 0; i < variants.Count; i++)
                {
                    string path = variants[i];
                    if (string.IsNullOrEmpty(path)) continue;
                    if (MaskName.IsVariantMask(path)) continue;
                    return path;
                }
            }
            return fallback;
        }
    }
}
