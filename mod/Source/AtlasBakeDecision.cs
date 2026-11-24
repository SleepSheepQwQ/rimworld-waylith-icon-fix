using System;

namespace AgentTouchFix
{
    /// <summary>
    /// What to do with one tile after a static atlas bake.
    ///
    /// Kept as a pure function so every branch can be asserted without a GPU, an atlas or Harmony:
    /// the engine side only measures a handful of booleans and executes the answer.
    ///
    /// The defect this exists for: <c>StaticTextureAtlas.Bake</c> registers every texture in
    /// <c>tiles</c> first (BuildMeshesForUvs, static.cs:164-171) and only then blits each one into a
    /// page that was cleared to <c>Color.clear</c> (BlitTexturesToColorAtlas, static.cs:474-492).
    /// A texture that cannot be made GPU-resident makes <c>Graphics.DrawTexture</c> log a stub and
    /// return without writing anything (UnityStubs Graphics.cs:560-596), so that cell keeps the clear
    /// colour, and <c>Graphic.TryGetTextureAtlasReplacementInfo</c> still answers with the atlas page
    /// plus this tile's uvRect - the source texture is never used again and the object draws as a
    /// transparent hole for the rest of the session.
    /// </summary>
    internal enum AtlasBakeAction
    {
        /// <summary>Leave the tile alone: the blit either wrote it, or did not need to.</summary>
        Keep,

        /// <summary>The colour blit could not run; the tile is a transparent hole. Unregister it.</summary>
        DropNotResident,

        /// <summary>
        /// A masked atlas whose mask could not be uploaded. Keeping the tile would bind the whole
        /// mask page (MaskTexture) for this texture, and a mask texel of 0 multiplies the colour away -
        /// the icon would draw black. Keeping the source texture instead loses only the tint.
        /// </summary>
        DropMaskNotResident,

        /// <summary>
        /// The page itself is not readable at all (no device, allocation failure, failed CopyTexture).
        /// Every tile on that page is a hole, so every tile has to go.
        /// </summary>
        DropPageMissing,
    }

    internal static class AtlasBakeDecision
    {
        /// <summary>File-switch convention shared by every module: only "off" disables.</summary>
        internal static bool Enabled(string switchText)
        {
            return !string.Equals((switchText ?? "").Trim(), "off", StringComparison.OrdinalIgnoreCase);
        }

        /// <param name="adoptedFromCache">
        /// <c>StaticTextureAtlas.AdoptedFromCache</c>: the page came from a cache keyed by every source
        /// texture's stamp and every uvRect, and no blit ran at all. Residency is irrelevant there -
        /// dropping tiles would throw away a page that is known to be correct.
        /// </param>
        /// <param name="pageResident">The atlas page holds pixels (its GPU resource exists).</param>
        /// <param name="colorResident"><c>Texture2D.EnsureGpuResident()</c> for the colour texture.</param>
        /// <param name="maskRequired">The atlas is a masked one (groupKey.hasMask) and this texture has a mask.</param>
        /// <param name="maskResident"><c>Texture2D.EnsureGpuResident()</c> for that mask.</param>
        internal static AtlasBakeAction Decide(bool adoptedFromCache, bool pageResident, bool colorResident,
                                               bool maskRequired, bool maskResident)
        {
            if (adoptedFromCache) return AtlasBakeAction.Keep;
            if (!pageResident) return AtlasBakeAction.DropPageMissing;
            if (!colorResident) return AtlasBakeAction.DropNotResident;
            if (maskRequired && !maskResident) return AtlasBakeAction.DropMaskNotResident;
            return AtlasBakeAction.Keep;
        }

        internal static string Reason(AtlasBakeAction action)
        {
            switch (action)
            {
                case AtlasBakeAction.DropNotResident: return "reason=not-resident";
                case AtlasBakeAction.DropMaskNotResident: return "reason=mask-not-resident";
                case AtlasBakeAction.DropPageMissing: return "reason=page-missing";
                default: return "reason=keep";
            }
        }
    }
}
