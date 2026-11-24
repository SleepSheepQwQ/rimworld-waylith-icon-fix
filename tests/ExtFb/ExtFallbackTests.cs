using Xunit;

namespace AgentTouchFix.Tests
{
    /// <summary>
    /// ExtFallbackDecision - which retry a failed texture lookup gets, and in what order.
    ///
    /// Two shapes reach the fallback and they must not be confused:
    ///   * the key is absent  -> the content list was keyed with an inner extension ("Foo_m.png"),
    ///     so retry the path with an extension appended (no file IO);
    ///   * the key is present -> the mod owns the key but the value is null/BadTex (the .dds failed
    ///     and ShadowedByDds already removed the .png), so read the same-named .png from disk.
    /// </summary>
    public class ExtFallbackTests
    {
        [Fact]
        public void ABarePathGetsEveryTextureExtensionInTheDocumentedOrder()
        {
            Assert.Equal(new[] { "A_m.png", "A_m.jpg", "A_m.jpeg", "A_m.psd" },
                         ExtFallbackDecision.Candidates("A_m"));
        }

        [Fact]
        public void APathThatAlreadyCarriesAnExtensionIsNotStacked()
        {
            Assert.Empty(ExtFallbackDecision.Candidates("A_m.png"));
            Assert.Empty(ExtFallbackDecision.Candidates("A_m.PNG"));
            Assert.Empty(ExtFallbackDecision.Candidates("A_m.jpg"));
            Assert.Empty(ExtFallbackDecision.Candidates("A_m.jpeg"));
            Assert.Empty(ExtFallbackDecision.Candidates("A_m.psd"));
            Assert.Empty(ExtFallbackDecision.Candidates("A_m.dds"));
        }

        [Fact]
        public void BlankInputHasNoCandidates()
        {
            Assert.Empty(ExtFallbackDecision.Candidates(null));
            Assert.Empty(ExtFallbackDecision.Candidates(""));
            Assert.Empty(ExtFallbackDecision.Candidates("   "));
        }

        [Fact]
        public void ADottedDirectoryIsNotAnExtensionOnTheFile()
        {
            Assert.Equal(new[] { "Mods/Some.Mod/Textures/Foo.png", "Mods/Some.Mod/Textures/Foo.jpg",
                                 "Mods/Some.Mod/Textures/Foo.jpeg", "Mods/Some.Mod/Textures/Foo.psd" },
                         ExtFallbackDecision.Candidates("Mods/Some.Mod/Textures/Foo"));
        }

        [Fact]
        public void TheShadowedFileCaseResolvesToThePngBesideIt()
        {
            // the exact key the CI fixture asks for (dds shadows png) and the one that has no png
            Assert.Equal("Things/DdsProbe/Shadow.png", ExtFallbackDecision.ImageName("Things/DdsProbe/Shadow"));
            Assert.Equal("Things/DdsProbe/Dxt3.png", ExtFallbackDecision.ImageName("Things/DdsProbe/Dxt3"));
        }

        [Fact]
        public void NoImageNameIsBuiltForAPathThatIsAlreadyAFile()
        {
            Assert.Null(ExtFallbackDecision.ImageName("Things/DdsProbe/Shadow.png"));
            Assert.Null(ExtFallbackDecision.ImageName(null));
            Assert.Null(ExtFallbackDecision.ImageName("  "));
        }

        [Fact]
        public void AbsentAndBadTexAreUnusableButARealTextureIsNot()
        {
            object bad = new object();
            object real = new object();

            Assert.True(ExtFallbackDecision.Unusable(null, bad));       // the key resolved to nothing
            Assert.True(ExtFallbackDecision.Unusable(bad, bad));        // the shared placeholder
            Assert.False(ExtFallbackDecision.Unusable(real, bad));      // art that can be drawn

            // BadTex is compared by reference: a texture that merely looks like it is not it
            object lookalike = new object();
            Assert.False(ExtFallbackDecision.Unusable(lookalike, bad));
        }

        [Fact]
        public void ABadTexThatIsNullOnThisHostStillMarksOnlyNullAsUnusable()
        {
            // measured on the CI host: BaseContent.BadTex is null there, so "unusable" degenerates
            // to "the lookup returned nothing" - it must not reject a real texture
            Assert.True(ExtFallbackDecision.Unusable(null, null));
            Assert.False(ExtFallbackDecision.Unusable(new object(), null));
        }

        [Fact]
        public void APresentKeyGetsTheFileRetryAndAnAbsentKeyGetsTheContentRetry()
        {
            Assert.Equal(ExtFallbackDecision.SameNameImage, ExtFallbackDecision.Choose(true, false));
            Assert.Equal(ExtFallbackDecision.ContentKey, ExtFallbackDecision.Choose(false, false));

            // a drawable result is never retried, whatever the index says
            Assert.Equal(ExtFallbackDecision.None, ExtFallbackDecision.Choose(true, true));
            Assert.Equal(ExtFallbackDecision.None, ExtFallbackDecision.Choose(false, true));
        }
    }
}
