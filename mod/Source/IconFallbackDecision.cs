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
            // Reference identity only: these are three fields on objects the mod does not own, so
            // the only question that can be answered without a renderer is "is this the sentinel or
            // nothing". "Usable" for the instance texture also covers a material without _MainTex
            // and a texture that is not resident - the caller passes null in those cases, see
            // MaterialUsable below.
            if (Drawable(instanceTex, badTex)) return IconSource.Instance;
            if (Drawable(defIcon, badTex)) return IconSource.DefUiIcon;
            if (Drawable(defGraphicTex, badTex)) return IconSource.DefGraphic;
            return IconSource.None;
        }

        /// <summary>Non-null and not the BadTex sentinel. A null <paramref name="badTex"/> never matches.</summary>
        private static bool Drawable(object tex, object badTex)
        {
            if (tex == null) return false;
            if (badTex != null && ReferenceEquals(tex, badTex)) return false;
            return true;
        }

        /// <summary>
        /// The gate in front of <see cref="Choose"/> for the *instance* material: this port hands out
        /// materials whose shader has no _MainTex, and textures that are not resident, and either one
        /// makes the tile blank even though mainTexture may look non-null. Pure, so the three ways an
        /// instance graphic can fail are tested without a renderer.
        /// </summary>
        internal static bool MaterialUsable(bool hasMaterial, bool hasMainTex, bool resident)
        {
            return hasMaterial && hasMainTex && resident;
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
                case IconSource.DefUiIcon: return "def.uiIcon";
                case IconSource.DefGraphic: return "def.graphic";
                default: return "none";
            }
        }

        /// <summary>
        /// The single line the device check looks for, exactly
        /// <c>icon fallback: &lt;defName&gt; via &lt;source&gt;</c>, e.g.
        /// <c>icon fallback: Gun_Revolver via def.uiIcon</c>. Written through the same sink as every
        /// other draw-time message, so it lands in files/game/agenttouchfix.log.
        ///
        /// Emission is the caller's business, and only once per (defName, source): a def that can
        /// never be repaired is retried on every draw by design, so the "none" line in particular
        /// must be rate-limited or it would fill the log.
        /// </summary>
        internal static string Line(string defName, IconSource s)
        {
            return "icon fallback: " + (string.IsNullOrEmpty(defName) ? "?" : defName)
                   + " via " + Name(s);
        }

        /// <summary>
        /// Periodic count, in the same shape as "drawfix: filled N icons at draw time", so a device log
        /// shows at a glance whether one surface or the whole game went through the fallback.
        /// </summary>
        internal static string Summary(int n)
        {
            return "drawfix: icon fallback used " + n + (n == 1 ? " time" : " times");
        }
    }
}
