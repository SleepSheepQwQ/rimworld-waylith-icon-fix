using Xunit;

namespace AgentTouchFix.Tests
{
    /// <summary>
    /// The sampler decision table. Every row is checked on both sides of the fix, because the stock answer
    /// is what proves the patch is a no-op for the path that was already correct.
    ///
    /// The port's predicate has two halves - the path says <c>/Textures/UI/</c>, and the file sits under a
    /// content root the game owns - and only the first half is visible from outside the port, so the table
    /// carries the second half as a parameter.
    /// </summary>
    public class SamplerChoiceTests
    {
        private const string ModIcon =
            "/storage/emulated/0/Android/data/com.ports1de.waylith/files/Mods/Foo/Textures/UI/Icons/Bar.png";

        private const string VanillaIcon =
            "/data/app/com.ports1de.waylith/Textures/UI/Icons/Mood.png";

        private const string VanillaThing =
            "/data/app/com.ports1de.waylith/Textures/Things/Steel/Steel_c.png";

        [Theory]
        // path          official root   patched field   stock field
        [InlineData(VanillaIcon, true, "_texSampler", "_texSampler")] // already mip 0: no change
        [InlineData(VanillaIcon, false, "_texSampler", "_texSamplerMipped")] // same path, mod root: fixed
        [InlineData(ModIcon, false, "_texSampler", "_texSamplerMipped")]     // tooltip/icon art: fixed
        [InlineData(VanillaThing, true, "_texSampler", "_texSamplerMipped")] // world texture: fixed
        [InlineData("", false, "_texSampler", "_texSamplerMipped")]
        [InlineData(null, false, "_texSampler", "_texSamplerMipped")]
        public void Table(string sourcePath, bool officialRoot, string patchedField, string stockField)
        {
            Assert.Equal(patchedField, SamplerChoice.Name(SamplerChoice.Patched(sourcePath)));
            Assert.Equal(stockField, SamplerChoice.Name(SamplerChoice.Stock(sourcePath, officialRoot)));
        }

        [Fact]
        public void OnlyTheMippedBranchChanges()
        {
            // the one case the shipped renderer already served with mip 0 must not be reported as a change
            Assert.False(SamplerChoice.Redirects(VanillaIcon, true));

            // everything else was on the mipped branch and is redirected
            Assert.True(SamplerChoice.Redirects(VanillaThing, true));
            Assert.True(SamplerChoice.Redirects(ModIcon, false));

            // null means "no source path known": mip 0 always exists, a LOD range may not
            Assert.True(SamplerChoice.Redirects(null, false));
        }

        [Fact]
        public void TheUiTestIsThePathTextHalfOfTheStockPredicate()
        {
            // mirrors ImportDefaults.IsOfficialUiTexture's Contains(..., OrdinalIgnoreCase) calls...
            Assert.True(SamplerChoice.IsUiPath(VanillaIcon));
            Assert.True(SamplerChoice.IsUiPath(ModIcon));
            Assert.True(SamplerChoice.IsUiPath(@"C:\Games\RimWorld\Textures\UI\Foo.png"));
            Assert.True(SamplerChoice.IsUiPath("/x/textures/ui/foo.png"));

            // ...and nothing else: a folder that merely starts with "UI" is not the UI texture folder
            Assert.False(SamplerChoice.IsUiPath("/x/Textures/UIKit/Foo.png"));
            Assert.False(SamplerChoice.IsUiPath("/x/Textures/UIFoo/Foo.png"));
            Assert.False(SamplerChoice.IsUiPath("/x/Textures/Things/UI/Foo.png"));
            Assert.False(SamplerChoice.IsUiPath(null));
            Assert.False(SamplerChoice.IsUiPath(""));
        }

        [Fact]
        public void AModIconUnderTexturesUiStillReachesTheMippedBranchOnStock()
        {
            // This is the case a path-based fix would have missed: the path says UI, the official-root test
            // says no, so stock picks the mipped sampler. The patch keys on the branch, not on the text.
            Assert.True(SamplerChoice.IsUiPath(ModIcon));
            Assert.Equal(SamplerKind.Mipped, SamplerChoice.Stock(ModIcon, false));
            Assert.Equal(SamplerKind.NonMipped, SamplerChoice.Patched(ModIcon));
        }

        [Fact]
        public void TheLogNamesTheRealFields()
        {
            Assert.Equal("_texSampler", SamplerChoice.Name(SamplerKind.NonMipped));
            Assert.Equal("_texSamplerMipped", SamplerChoice.Name(SamplerKind.Mipped));
        }
    }
}
