using System;
using Xunit;

namespace AgentTouchFix.Tests
{
    /// <summary>
    /// The four assertions of the Odyssey instance-icon fallback, plus the truth table, the log text
    /// and the cache rule they depend on. Everything here is pure: the three textures are just
    /// <see cref="object"/> references and BaseContent.BadTex is passed in as one more reference, so
    /// the matrix runs on a bare .NET runner with no game, no device and no Harmony.
    ///
    /// The failure this covers: a modified weapon (vanilla Gun_Revolver + attachments) draws an empty
    /// tile, because the instance graphic's material yields no readable mainTexture in this port,
    /// while the plain def icon is fine. The fallback therefore prefers the instance texture when it
    /// exists, and only degrades when it does not.
    /// </summary>
    public class IconFallbackTests
    {
        // Two stand-in textures and the BadTex sentinel: identity is the only thing that matters.
        private static readonly object Tex = new object();      // a texture that can be drawn
        private static readonly object Other = new object();    // a different drawable texture
        private static readonly object Bad = new object();      // stands in for BaseContent.BadTex

        // ---------------------------------------------------------------- assertion 1

        [Fact]
        public void Assertion1_InstanceTextureResolvable_NoFallback()
        {
            // the healthy case, and the only one where the mod must keep its hands off: whatever the
            // def says, a usable instance texture wins and is drawn unchanged
            Assert.Equal(IconSource.Instance, IconFallback.Choose(Tex, null, null, Bad));
            Assert.Equal(IconSource.Instance, IconFallback.Choose(Tex, Other, Other, Bad));
            Assert.Equal(IconSource.Instance, IconFallback.Choose(Tex, Bad, Bad, Bad));
        }

        // ---------------------------------------------------------------- assertion 2

        [Fact]
        public void Assertion2_InstanceTextureEmpty_FallsBackToDefUiIcon()
        {
            // exactly what a modified weapon looks like here: Graphic...MatAt(rot).mainTexture == null
            Assert.Equal(IconSource.DefUiIcon, IconFallback.Choose(null, Tex, Other, Bad));
            // BadTex is "empty" too - it is not a texture to draw, so it must not shadow the def icon
            Assert.Equal(IconSource.DefUiIcon, IconFallback.Choose(Bad, Tex, Other, Bad));
        }

        // ---------------------------------------------------------------- assertion 3

        [Fact]
        public void Assertion3_DefIconAlsoBroken_FallsBackToDefGraphicMainTexture()
        {
            Assert.Equal(IconSource.DefGraphic, IconFallback.Choose(null, null, Tex, Bad));
            Assert.Equal(IconSource.DefGraphic, IconFallback.Choose(null, Bad, Tex, Bad));
            Assert.Equal(IconSource.DefGraphic, IconFallback.Choose(Bad, Bad, Other, Bad));
        }

        // ---------------------------------------------------------------- assertion 4

        [Fact]
        public void Assertion4_AllUnavailable_NoThrowAndFailureIsNotCached()
        {
            IconSource s = IconFallback.Choose(null, null, null, Bad);
            Assert.Equal(IconSource.None, s);
            // the whole point: a failure is retried on the next draw (the graphic may still be
            // loading), so it must never be remembered as resolved
            Assert.False(IconFallback.Cacheable(s));
        }

        // ---------------------------------------------------------------- the table behind them

        [Fact]
        public void Precedence_IsInstanceThenDefUiIconThenDefGraphicThenNone()
        {
            // all eight combinations, so a change in the order of the fallback cannot pass unnoticed
            for (int m = 0; m < 8; m++)
            {
                bool instanceOk = (m & 1) != 0;
                bool defIconOk = (m & 2) != 0;
                bool defGraphicOk = (m & 4) != 0;

                IconSource want = instanceOk ? IconSource.Instance
                                : defIconOk ? IconSource.DefUiIcon
                                : defGraphicOk ? IconSource.DefGraphic
                                : IconSource.None;

                IconSource got = IconFallback.Choose(instanceOk ? Tex : null,
                                                     defIconOk ? Tex : null,
                                                     defGraphicOk ? Tex : null,
                                                     Bad);

                Assert.True(want == got,
                    "instance=" + instanceOk + " defIcon=" + defIconOk + " defGraphic=" + defGraphicOk
                    + ": expected " + want + ", got " + got);
            }
        }

        [Fact]
        public void GarbageAndNullArguments_DoNotThrow()
        {
            // the prefix runs on the draw path for every visible tile: it may never throw, not even
            // with an unresolved BadTex (null) or an argument that is not a texture at all
            Assert.Equal(IconSource.None, IconFallback.Choose(null, null, null, null));
            Assert.Equal(IconSource.Instance, IconFallback.Choose(Tex, null, null, null));
            Assert.Equal(IconSource.DefUiIcon, IconFallback.Choose(null, "not a texture", null, null));
        }

        [Fact]
        public void Cacheable_AcceptsEveryResolvedSource_AndRejectsOnlyNone()
        {
            Assert.True(IconFallback.Cacheable(IconSource.Instance));
            Assert.True(IconFallback.Cacheable(IconSource.DefUiIcon));
            Assert.True(IconFallback.Cacheable(IconSource.DefGraphic));
            Assert.False(IconFallback.Cacheable(IconSource.None));
        }

        // ---------------------------------------------------------------- the log contract

        [Fact]
        public void LogLine_IsTheTextTheDeviceCheckGrepsFor()
        {
            // the user-side check in VERIFICATION.md greps for exactly these strings:
            //   grep "icon fallback" files/game/agenttouchfix.log
            Assert.Equal("drawfix: icon fallback instance on Gun_Revolver",
                IconFallback.Line("Gun_Revolver", IconSource.Instance));
            Assert.Equal("drawfix: icon fallback defUiIcon on Gun_Revolver",
                IconFallback.Line("Gun_Revolver", IconSource.DefUiIcon));
            Assert.Equal("drawfix: icon fallback defGraphic on Gun_Revolver",
                IconFallback.Line("Gun_Revolver", IconSource.DefGraphic));
            // a def whose defName could not be read still produces one greppable line
            Assert.Equal("drawfix: icon fallback none on ?", IconFallback.Line(null, IconSource.None));
        }
    }
}
