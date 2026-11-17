using System.Collections.Generic;
using Xunit;

namespace AgentTouchFix.Tests
{
    /// <summary>
    /// ExtensionFallback - the miss-only retry list that repairs the port's content lookup.
    ///
    /// The port's index keys keep the inner extension of a double-extension texture
    /// ("UniqueRevolverA_m.png.dds" -> "…/UniqueRevolverA_m.png"), while the game asks for the bare
    /// name ("…/UniqueRevolverA_m"), which is why mask lookups miss and masked graphics draw blank.
    /// </summary>
    public class ExtensionFallbackTests
    {
        [Fact]
        public void ABarePathGetsEveryTextureExtensionInAStableOrder()
        {
            Assert.Equal(new[] { "A_m.png", "A_m.psd", "A_m.jpg", "A_m.dds" }, ExtensionFallback.Candidates("A_m"));
        }

        [Fact]
        public void APathThatAlreadyCarriesAnExtensionIsNotStacked()
        {
            Assert.Empty(ExtensionFallback.Candidates("A_m.png"));
            Assert.Empty(ExtensionFallback.Candidates("A_m.psd"));
            Assert.Empty(ExtensionFallback.Candidates("A_m.JPG"));
            Assert.Empty(ExtensionFallback.Candidates("A_m.dds"));
            Assert.Empty(ExtensionFallback.Candidates("A_m.jpeg"));
        }

        [Fact]
        public void BlankInputHasNoCandidates()
        {
            Assert.Empty(ExtensionFallback.Candidates(null));
            Assert.Empty(ExtensionFallback.Candidates(""));
            Assert.Empty(ExtensionFallback.Candidates("   "));
        }

        [Fact]
        public void DirectoriesDoNotStopTheAppend()
        {
            string p = "Things/Animal/Lynx/Lynx_northm";
            Assert.Equal(new[]
            {
                p + ".png", p + ".psd", p + ".jpg", p + ".dds",
            }, ExtensionFallback.Candidates(p));

            // a dotted directory is not an extension on the file name
            Assert.Equal(4, ExtensionFallback.Candidates("Mods/Some.Mod/Textures/Foo").Length);
        }

        [Fact]
        public void TheRealOdysseyAndAnimalLookupsAreCovered()
        {
            // the exact strings Graphic_Single / Graphic_Multi ask for on this port
            Assert.Contains("Things/Item/Equipment/WeaponRanged/UniqueWeapons/Revolver/UniqueRevolverA_m.png",
                            ExtensionFallback.Candidates("Things/Item/Equipment/WeaponRanged/UniqueWeapons/Revolver/UniqueRevolverA_m"));
            Assert.Contains("Things/Animal/Lynx/Lynx_northm.psd",
                            ExtensionFallback.Candidates("Things/Animal/Lynx/Lynx_northm"));
        }

        /// <summary>
        /// The contract the postfix relies on: the first hit wins, and nothing is looked up after it.
        /// The fake index stands in for the port's content list.
        /// </summary>
        [Fact]
        public void TheFirstHitIsReturnedAndTheLoopStops()
        {
            var index = new Dictionary<string, string>
            {
                { "Lynx_northm.psd", "mask" },     // second candidate
                { "Lynx_northm.jpg", "also-here" } // must never be reached
            };
            var asked = new List<string>();

            string hitPath;
            int tried;
            string value = ExtensionFallback.FirstHit("Lynx_northm", p =>
            {
                asked.Add(p);
                return index.TryGetValue(p, out string v) ? v : null;
            }, out hitPath, out tried);

            Assert.Equal("mask", value);
            Assert.Equal("Lynx_northm.psd", hitPath);
            Assert.Equal(2, tried);
            Assert.Equal(new[] { "Lynx_northm.png", "Lynx_northm.psd" }, asked);   // stopped at the hit
        }

        [Fact]
        public void AMissStaysAMissAndTriesEveryCandidateOnce()
        {
            var asked = new List<string>();
            string hitPath;
            int tried;
            string value = ExtensionFallback.FirstHit<string>("Lynx_northm", p =>
            {
                asked.Add(p);
                return null;
            }, out hitPath, out tried);

            Assert.Null(value);
            Assert.Null(hitPath);
            Assert.Equal(4, tried);
            Assert.Equal(4, asked.Count);          // every candidate once, no negative caching
            Assert.Equal(asked.Count, new HashSet<string>(asked).Count);
        }

        [Fact]
        public void APathWithAnExtensionIsNeverRetried()
        {
            int calls = 0;
            string hitPath;
            int tried;
            string value = ExtensionFallback.FirstHit<string>("A_m.png", p =>
            {
                calls++;
                return null;
            }, out hitPath, out tried);

            Assert.Null(value);
            Assert.Equal(0, calls);
            Assert.Equal(0, tried);
        }

        [Fact]
        public void AHitOnTheFirstCandidateCostsOneLookup()
        {
            int calls = 0;
            string hitPath;
            int tried;
            string value = ExtensionFallback.FirstHit("UniqueRevolverA_m", p =>
            {
                calls++;
                return p == "UniqueRevolverA_m.png" ? "tex" : null;
            }, out hitPath, out tried);

            Assert.Equal("tex", value);
            Assert.Equal(1, calls);
            Assert.Equal(1, tried);
        }
    }
}
