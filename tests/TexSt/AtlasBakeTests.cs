using Xunit;

namespace AgentTouchFix.Tests
{
    /// <summary>
    /// AtlasBakeDecision - what happens to one tile after StaticTextureAtlas.Bake.
    ///
    /// The pure function has to answer three different questions with one enum, and every branch is
    /// reachable in the field:
    ///   * a cached page needs no blit, so residency is not evidence of anything (warm start),
    ///   * a page that never got pixels makes every tile on it a hole (no device / OOM),
    ///   * a source that did not reach the page is a single hole,
    ///   * a mask that did not reach the mask page paints the icon black once the replacement swaps
    ///     the whole mask page in, so it must be dropped even though the colour arrived.
    /// The "keep" cases are asserted as carefully as the drops: a false drop costs the icon its atlas
    /// draw for the rest of the session.
    /// </summary>
    public class AtlasBakeTests
    {
        [Fact]
        public void CachedPageKeepsEverythingEvenWhenNothingIsResident()
        {
            // AdoptedFromCache: TryAdoptCachedPages returned true, no DrawTexture ran at all. Every
            // source may be evicted (that is what the page cache is for) - dropping here would throw
            // away a page whose key already proves it matches these sources.
            Assert.Equal(AtlasBakeAction.Keep,
                AtlasBakeDecision.Decide(adoptedFromCache: true, pageResident: false,
                                         colorResident: false, maskRequired: true, maskResident: false));
            Assert.Equal(AtlasBakeAction.Keep,
                AtlasBakeDecision.Decide(true, false, false, false, false));
        }

        [Fact]
        public void MissingPageDropsEveryTileWhateverTheSourcesSay()
        {
            // The page itself has no GPU resource: CopyTexture/GL.Clear never landed. Every uvRect on
            // it samples nothing, so even a resident source has to be unregistered.
            Assert.Equal(AtlasBakeAction.DropPageMissing,
                AtlasBakeDecision.Decide(false, pageResident: false, colorResident: true,
                                         maskRequired: false, maskResident: true));
        }

        [Fact]
        public void SourceThatNeverReachedThePageIsDropped()
        {
            // The documented defect: Graphics.DrawTexture calls StubWarner.LogStub and returns, the
            // cell keeps Color.clear, and TryGetTextureAtlasReplacementInfo still hands the graphic
            // that cell in exchange for its own texture.
            Assert.Equal(AtlasBakeAction.DropNotResident,
                AtlasBakeDecision.Decide(false, true, colorResident: false, maskRequired: false, maskResident: true));
        }

        [Fact]
        public void MaskThatNeverReachedTheMaskPageIsDropped()
        {
            Assert.Equal(AtlasBakeAction.DropMaskNotResident,
                AtlasBakeDecision.Decide(false, true, colorResident: true, maskRequired: true, maskResident: false));
        }

        [Fact]
        public void UnmaskedTextureInAMaskedAtlasKeepsItsTile()
        {
            // groupKey.hasMask atlases exist, but Insert() only records a mask when one was passed;
            // a texture without one has no mask cell to miss.
            Assert.Equal(AtlasBakeAction.Keep,
                AtlasBakeDecision.Decide(false, true, true, maskRequired: false, maskResident: false));
        }

        [Fact]
        public void HealthyBakeIsUntouched()
        {
            Assert.Equal(AtlasBakeAction.Keep, AtlasBakeDecision.Decide(false, true, true, false, false));
            Assert.Equal(AtlasBakeAction.Keep, AtlasBakeDecision.Decide(true, true, true, true, true));
        }

        [Theory]
        [InlineData("", true)]
        [InlineData(null, true)]
        [InlineData("on", true)]
        [InlineData(" OFF ", false)]
        [InlineData("off", false)]
        public void OnlyTheOffSwitchDisablesTheModule(string text, bool enabled)
        {
            Assert.Equal(enabled, AtlasBakeDecision.Enabled(text));
        }

        [Fact]
        public void ReasonsAreDistinctSoALogCanBeCounted()
        {
            Assert.NotEqual(AtlasBakeDecision.Reason(AtlasBakeAction.DropNotResident),
                            AtlasBakeDecision.Reason(AtlasBakeAction.DropMaskNotResident));
            Assert.NotEqual(AtlasBakeDecision.Reason(AtlasBakeAction.DropMaskNotResident),
                            AtlasBakeDecision.Reason(AtlasBakeAction.DropPageMissing));
            Assert.Contains("not-resident", AtlasBakeDecision.Reason(AtlasBakeAction.DropNotResident));
        }
    }
}
