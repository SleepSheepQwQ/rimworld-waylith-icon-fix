using System;

namespace AgentTouchFix
{
    /// <summary>Which of the renderer's two texture samplers a draw ends up using.</summary>
    internal enum SamplerKind
    {
        /// <summary><c>_texSamplerMipped</c>: linear minification with <c>MaxLod = 1000</c>.</summary>
        Mipped,

        /// <summary><c>_texSampler</c>: linear filtering, <c>MaxLod = 0</c> - mip 0 only.</summary>
        NonMipped
    }

    /// <summary>
    /// Pure classifier for the sampler question, in its own file so CI can unit-test it without
    /// Harmony or a device.
    ///
    /// The shipped renderer picks a sampler with
    /// <c>SamplerFor(path) = ImportDefaults.IsOfficialUiTexture(path) ? _texSampler : _texSamplerMipped</c>
    /// (<c>ImGuiVulkanRenderer.SamplerFor</c>), and <c>ImportDefaults.IsOfficialUiTexture</c> is
    /// <c>path contains /Textures/UI/ &amp;&amp; IsOfficial(path)</c> - the second half being "the file
    /// lives under one of the game's own content roots". A mod's own icon under
    /// <c>.../Mods/Foo/Textures/UI/Icons/Bar.png</c> fails the root test, so it takes the mipped branch
    /// even though its path says UI. That is why this mod does not attempt to re-derive the official-root
    /// test (it cannot, from outside the port) and instead redirects the mipped branch unconditionally:
    /// the branch itself, not the path text, is the thing that is wrong.
    /// </summary>
    internal static class SamplerChoice
    {
        private const string UiSegmentForward = "/Textures/UI/";
        private const string UiSegmentBackward = "\\Textures\\UI\\";

        /// <summary>
        /// The path-text half of the stock predicate, mirroring <c>ImportDefaults.IsOfficialUiTexture</c>'s
        /// <c>Contains(..., OrdinalIgnoreCase)</c> calls. Used for logging and diagnostics only - never as
        /// the reason to keep the mipped sampler, because an unofficial UI path is broken on stock too.
        /// </summary>
        internal static bool IsUiPath(string sourcePath)
        {
            if (sourcePath == null) return false;
            return sourcePath.IndexOf(UiSegmentForward, StringComparison.OrdinalIgnoreCase) >= 0
                   || sourcePath.IndexOf(UiSegmentBackward, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// The shipped predicate, spelled out: <c>IsOfficialUiTexture = IsUiPath &amp;&amp; IsOfficial</c>, and
        /// <paramref name="officialRoot"/> is the <c>IsOfficial</c> half - "the file lives under one of the
        /// game's own content roots". This mod cannot compute that half from outside the port, so nothing in
        /// the runtime path calls this; it is here so the tests can pin exactly which inputs the patch
        /// changes, and so the delta is written down somewhere other than a commit message.
        /// </summary>
        internal static SamplerKind Stock(string sourcePath, bool officialRoot)
        {
            return officialRoot && IsUiPath(sourcePath) ? SamplerKind.NonMipped : SamplerKind.Mipped;
        }

        /// <summary>What the renderer returns once this mod is installed: never the mipped sampler.</summary>
        internal static SamplerKind Patched(string sourcePath)
        {
            return SamplerKind.NonMipped;
        }

        /// <summary>
        /// True when the patch changes the answer for this path. A <c>null</c> path counts as a redirect: an
        /// unknown texture gets mip 0, which always exists, instead of a LOD range that may not.
        /// </summary>
        internal static bool Redirects(string sourcePath, bool officialRoot)
        {
            return Stock(sourcePath, officialRoot) != Patched(sourcePath);
        }

        /// <summary>The field name on the renderer, for log lines that have to be greppable.</summary>
        internal static string Name(SamplerKind kind)
        {
            return kind == SamplerKind.NonMipped ? "_texSampler" : "_texSamplerMipped";
        }
    }
}
