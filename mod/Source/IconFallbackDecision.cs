using System;

namespace AgentTouchFix
{
    /// <summary>
    /// Where the icon drawn for a Thing <em>instance</em> actually came from.
    /// Order of preference is the enum order: Instance, then DefUiIcon, then DefGraphic, then None.
    /// </summary>
    internal enum IconSource
    {
        /// <summary>Nothing usable. The draw is left exactly as the game produced it (i.e. blank).</summary>
        None = 0,
        /// <summary>The instance's own graphic - what the game resolves for a healthy thing.</summary>
        Instance = 1,
        /// <summary><c>def.uiIcon</c> - the def-level icon, the same one the build menu draws.</summary>
        DefUiIcon = 2,
        /// <summary>The <c>mainTexture</c> of <c>def.graphic</c> - last resort before giving up.</summary>
        DefGraphic = 3
    }

    /// <summary>
    /// Pure routing decision for the Odyssey instance-icon fallback (modified weapons: base
    /// <c>Gun_Revolver</c> + attachments draw an empty tile, while the unmodified def draws fine).
    ///
    /// It lives in its own file - the same arrangement as <see cref="IconPath"/> - so CI can test the
    /// whole truth table with no Harmony, no device, no def database and no game assembly:
    ///
    ///     instance texture usable                    -> Instance    (never override a healthy icon)
    ///     instance empty, def.uiIcon usable          -> DefUiIcon
    ///     def.uiIcon empty, def.graphic has texture  -> DefGraphic
    ///     none of the three                          -> None        (must NOT be cached)
    ///
    /// "empty" means <c>null</c>, or the very object <c>verse.BaseContent.BadTex</c> points at. The
    /// BadTex sentinel is passed in rather than looked up, so this file stays free of game types and
    /// a null <paramref name="badTex"/> still makes every check fail closed instead of throwing.
    ///
    /// Verification matrix, device steps and the regression checklist:
    /// <c>~/.hermes/cache/scratch/shared/VERIFICATION.md</c>.
    /// </summary>
    internal static class IconFallback
    {
        /// <summary>
        /// The decision itself. Pure: no reads of game state, no allocation, no exceptions.
        /// </summary>
        /// <param name="instanceTex">mainTexture of the instance graphic, or null / BadTex.</param>
        /// <param name="defIcon">def.uiIcon, or null / BadTex.</param>
        /// <param name="defGraphicTex">mainTexture of def.graphic, or null / BadTex.</param>
        /// <param name="badTex">Verse.BaseContent.BadTex, used for reference identity only.</param>
        internal static IconSource Choose(object instanceTex, object defIcon, object defGraphicTex, object badTex)
        {
            // NOT IMPLEMENTED ON PURPOSE. This body is the contract the four CI assertions in
            // tests/Fallback/IconFallbackTests.cs lock down; the implementation branch fills it in.
            // Until then tests/Fallback is red here and nowhere else - which is exactly what the
            // red run recorded in VERIFICATION.md says.
            throw new NotImplementedException("IconFallback.Choose (branch odyssey-icon-fallback)");
        }

        /// <summary>
        /// A resolved icon may be remembered for the def/thing it was resolved from; a failure must be
        /// retried on the next draw, never cached - the graphic may still be loading.
        /// </summary>
        internal static bool Cacheable(IconSource s)
        {
            return s != IconSource.None;
        }

        /// <summary>Stable spelling used in the log line, so the device check can grep for it.</summary>
        internal static string Name(IconSource s)
        {
            switch (s)
            {
                case IconSource.Instance: return "instance";
                case IconSource.DefUiIcon: return "defUiIcon";
                case IconSource.DefGraphic: return "defGraphic";
                default: return "none";
            }
        }

        /// <summary>
        /// The single line the device check looks for, e.g.
        /// <c>drawfix: icon fallback defUiIcon on Gun_Revolver</c>. Same "drawfix: " prefix and sink as
        /// every other draw-time message, so it lands in files/game/agenttouchfix.log.
        ///
        /// Emission is the caller's business: the "none" line is a diagnostic and must be rate-limited
        /// (at most once per def per session, five in total), otherwise a def that can never be
        /// repaired - it is retried every frame by design - would fill the log.
        /// </summary>
        internal static string Line(string defName, IconSource s)
        {
            return "drawfix: icon fallback " + Name(s) + " on "
                   + (string.IsNullOrEmpty(defName) ? "?" : defName);
        }
    }
}
