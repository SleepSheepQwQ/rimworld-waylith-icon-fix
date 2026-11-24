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
        [InlineData(false, true, false, true)]   // absent entry: loader retry may fill it
        [InlineData(true, true, false, true)]    // present but null
        [InlineData(true, false, true, true)]    // present but BadTex
        [InlineData(true, false, false, false)]  // already drawable: never reload
        public void OnlyAbsentOrUnusableEntriesAreRetried(bool present, bool isNull,
                                                          bool isBadTex, bool expected)
        {
            Assert.Equal(expected, ContentSweepDecision.Retry(present, isNull, isBadTex));
        }
    }
}
