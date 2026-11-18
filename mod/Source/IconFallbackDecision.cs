using System;

namespace AgentTouchFix
{
    /// <summary>
    /// Pure selection logic for the icon fallback - no game types, so CI unit-tests it directly.
    ///
    /// Verse.Widgets.ThingIcon(Rect, Thing, ...) asks Widgets.GetIconFor(Thing, ...) for the icon and
    /// bails out on the spot when that comes back null or as BaseContent.BadTex (Widgets.cs:489-495):
    ///
    ///     Texture iconFor = GetIconFor(thing, ..., out material);
    ///     if (iconFor == null || iconFor == BaseContent.BadTex) return;   // nothing drawn
    ///
    /// GetIconFor(Thing, ...) is instance level only - thing.Graphic.ExtractInnerGraphicFor(thing)
    /// .MatAt(rot).mainTexture (Widgets.cs:739-741) - with no def-level fallback. A Thing carrying a
    /// cloned Graphic (a mod's stuff/colour variant, a worn or reskinned item) can therefore return a
    /// texture-less material while the very same def has a perfectly good uiIcon or graphic.
    ///
    /// Order is fixed: uiIcon first (the cheapest and the most specific: it already carries the def's
    /// own art, style and rotation), then the def's graphic at its default placing rotation. A result
    /// that was drawable to begin with is never touched, so the steady state costs a null test.
    /// </summary>
    internal static class IconFallbackDecision
    {
        /// <summary>No fallback: the caller keeps whatever the original produced.</summary>
        internal const string None = "";

        /// <summary>def.uiIcon was drawable and was adopted.</summary>
        internal const string UiIcon = "uiIcon";

        /// <summary>def.graphic.MatAt(def.defaultPlacingRot).mainTexture was drawable and was adopted.</summary>
        internal const string Graphic = "graphic";

        /// <summary>The one predicate everything else is built on: present, and not the BadTex placeholder.</summary>
        internal static bool Drawable(bool isNull, bool isBadTex)
        {
            return !isNull && !isBadTex;
        }

        /// <summary>
        /// Which source to adopt. <paramref name="resultDrawable"/> wins outright - a working instance
        /// icon is never second-guessed. Otherwise uiIcon, then graphic, then nothing.
        /// </summary>
        internal static string Choose(bool resultDrawable, bool uiIconDrawable, bool graphicDrawable)
        {
            if (resultDrawable) return None;
            if (uiIconDrawable) return UiIcon;
            if (graphicDrawable) return Graphic;
            return None;
        }

        /// <summary>
        /// Switch file contents -> enabled. Only an explicit off turns the fix off: an absent file
        /// (null/blank) and anything unrecognised keep it on, because the fallback itself is inert
        /// unless GetIconFor actually fails.
        /// </summary>
        internal static bool Enabled(string switchText)
        {
            if (switchText == null) return true;
            string s = switchText.Trim().ToLowerInvariant();
            if (s.Length == 0) return true;
            return !(s == "off" || s == "0" || s == "false" || s == "disable" || s == "disabled");
        }
    }
}
