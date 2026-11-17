using System.Collections.Generic;
using Xunit;

namespace AgentTouchFix.Tests
{
    /// <summary>
    /// MaskName.IsMask - the one decision the Graphic_Collection fix makes per subgraphic.
    ///
    /// The port leaks the inner extension into Texture2D.name ("UniqueRevolverA_m.png.dds" comes back
    /// as "UniqueRevolverA_m.png"), which is why vanilla's bare EndsWith("_m") stops matching and the
    /// masks survive into Graphic_Random's variant array.
    /// </summary>
    public class MaskNameTests
    {
        [Theory]
        // the shapes that must be dropped
        [InlineData("X_m", true)]
        [InlineData("X_m.png", true)]
        [InlineData("X_m.jpg.dds", true)]
        // real Odyssey unique-weapon files, both as shipped and as this port names them
        [InlineData("UniqueRevolverA_m.png.dds", true)]
        [InlineData("UniqueRevolverA_m.png", true)]
        [InlineData("UniqueAssaultRifleE_m.dds", true)]
        [InlineData("  UniqueRevolverB_m.png  ", true)]
        [InlineData("unique_m.PNG", true)]
        // not masks
        [InlineData("X", false)]
        [InlineData("X_magazine", false)]
        [InlineData("X_m2", false)]
        [InlineData("XM_m", false)]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        // the plain halves of the real pairs, which must survive
        [InlineData("UniqueRevolverA.png.dds", false)]
        [InlineData("UniqueRevolverA.png", false)]
        [InlineData("_m", false)]
        [InlineData("X_m.png.dds.bak", false)]
        public void IsMask(string name, bool expected)
        {
            Assert.Equal(expected, MaskName.IsMask(name));
        }

        [Fact]
        public void TheRealDlcPairSplitsIntoTexturePlusMask()
        {
            // exactly what ContentFinder returns for Weapons_Unique/Revolver, in orderby name order
            string[] shipped =
            {
                "UniqueRevolverA.png", "UniqueRevolverA_m.png",
                "UniqueRevolverB.png", "UniqueRevolverB_m.png",
            };

            var kept = new List<string>();
            foreach (string n in shipped)
            {
                if (!MaskName.IsMask(n)) kept.Add(n);
            }

            Assert.Equal(2, kept.Count);
            Assert.False(MaskName.IsMask(kept[0]));
            Assert.False(MaskName.IsMask(kept[1]));
        }

        [Fact]
        public void ThePortNameRegressionIsWhatThisFixCompensatesFor()
        {
            // No name ends with a literal "_m" once the port has appended the inner extension - which
            // is exactly why the vanilla filter in Graphic_Collection.Init drops nothing here.
            string[] portNames =
            {
                "UniqueRevolverA.png", "UniqueRevolverA_m.png",
                "UniqueRevolverB.png", "UniqueRevolverB_m.png",
            };

            int naive = 0;
            foreach (string n in portNames)
            {
                if (!n.EndsWith("_m")) naive++;      // the game's own test
            }
            Assert.Equal(portNames.Length, naive);   // bug: masks survive

            int fixedCount = 0;
            foreach (string n in portNames)
            {
                if (!MaskName.IsMask(n)) fixedCount++;
            }
            Assert.Equal(2, fixedCount);             // and drop to the real variants
        }

        /// <summary>
        /// The property Graphic_Random depends on: count is what the modulo divides, so a stray mask
        /// makes half the instances blank. After the filter every weapon's variants pair up under one
        /// family prefix, and the total is even.
        /// </summary>
        [Fact]
        public void AfterDroppingMasksTheVariantsAreEvenAndPairUpByPrefix()
        {
            string[] portNames =
            {
                "UniqueRevolverA.png", "UniqueRevolverA_m.png",
                "UniqueRevolverB.png", "UniqueRevolverB_m.png",
                "UniqueRevolverC.png", "UniqueRevolverC_m.png",
                "UniqueRevolverD.png", "UniqueRevolverD_m.png",
            };

            var kept = new List<string>();
            foreach (string n in portNames)
            {
                if (!MaskName.IsMask(n)) kept.Add(n);
            }

            Assert.Equal(4, kept.Count);
            Assert.True(kept.Count % 2 == 0, "variant count must be even: " + kept.Count);

            for (int i = 0; i + 1 < kept.Count; i += 2)
            {
                Assert.Equal(Family(kept[i]), Family(kept[i + 1]));
            }

            foreach (string n in kept)
            {
                Assert.False(MaskName.IsMask(n));
            }
        }

        /// <summary>"UniqueRevolverB.png" -&gt; "UniqueRevolver" (drop the extension and the variant letter).</summary>
        private static string Family(string name)
        {
            string s = name;
            int dot = s.IndexOf('.');
            if (dot > 0) s = s.Substring(0, dot);
            return s.TrimEnd('A', 'B', 'C', 'D', 'E');
        }
    }
}
