using Xunit;

namespace AgentTouchFix.Tests
{
    public class ContentSweepTests
    {
        [Theory]
        [InlineData("Textures/Things/Item/Foo.dds", "Things/Item/Foo")]
        [InlineData("textures/Things/Item/Foo.PNG.DDS", "Things/Item/Foo.PNG")]
        [InlineData("/Textures/Things/Foo_m.png.dds", "Things/Foo_m.png")]
        [InlineData("Things/Foo.dds", "Things/Foo")]
        public void ContentPathBecomesTheHolderKey(string path, string expected)
        {
            Assert.Equal(expected, ContentSweepDecision.KeyFromPath(path));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Textures/Things/Foo.png")]
        [InlineData("Textures/Things/.dds")]
        public void NonDdsPathsAreNotSweepCandidates(string path)
        {
            Assert.Null(ContentSweepDecision.KeyFromPath(path));
        }

        [Theory]
        [InlineData(false, true, true, true)]    // header says the loader maps it wrongly
        [InlineData(false, false, false, true)]  // absent/null/BadTex: one retry
        [InlineData(false, false, true, false)]  // already drawable and correctly headed
        [InlineData(true, true, false, false)]   // prefix already built this object
        public void OnlyUnownedOrHeaderBrokenEntriesAreRetried(bool alreadyOwned,
                                                               bool headerNeedsRepair,
                                                               bool contentUsable,
                                                               bool expected)
        {
            Assert.Equal(expected,
                ContentSweepDecision.NeedsLoaderRepair(alreadyOwned, headerNeedsRepair, contentUsable));
        }
    }
}
