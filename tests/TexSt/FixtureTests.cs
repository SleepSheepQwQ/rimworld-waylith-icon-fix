using Xunit;

namespace AgentTouchFix.Tests
{
    /// <summary>
    /// The fixture sample: which def is accepted as a stand-in for the unique weapon the user's save
    /// does not contain, and the exact line it has to print.
    /// </summary>
    public class FixtureTests
    {
        [Theory]
        // the shape that actually breaks: random variants, and the mask really exists
        [InlineData("Graphic_Random", true, true)]
        [InlineData("Graphic_RandomMulti", true, true)]     // any Graphics_Random* prefix
        // no mask: the def is expected to draw fine, so it would prove nothing
        [InlineData("Graphic_Random", false, false)]
        [InlineData("Graphic_RandomMulti", false, false)]
        // single/multi graphics have one texture and no random variants to compare
        [InlineData("Graphic_Single", true, false)]
        [InlineData("Graphic_Multi", true, false)]
        [InlineData("Graphic_Collection", true, false)]
        // nothing to go on
        [InlineData(null, true, false)]
        [InlineData("", true, false)]
        [InlineData(null, false, false)]
        public void OnlyRandomVariantsWithAMaskAreAccepted(string graphicClass, bool maskFound, bool expected)
        {
            Assert.Equal(expected, TestSpawnDecision.FixtureCandidate(graphicClass, maskFound));
        }

        [Fact]
        public void TheFixtureLineCarriesEveryFieldTheReportAskedFor()
        {
            string line = TestSpawnDecision.FixtureLine(
                "Gun_Revolver_Unique", 1234, "Graphic_Random", "UniqueRevolverA", 128, 128,
                "UniqueRevolverA", true, "Graphic_Random", "Things/Weapons/UniqueRevolver", 5);

            Assert.Equal(
                "test: fixture def=Gun_Revolver_Unique id=1234 graphic=Graphic_Random"
                + " tex=UniqueRevolverA size=128x128 uiIcon=UniqueRevolverA same=true"
                + " graphicClass=Graphic_Random texPath=Things/Weapons/UniqueRevolver subgraphics=5",
                line);
        }

        [Fact]
        public void AFixtureThatIsNotACollectionPrintsNoCount()
        {
            string line = TestSpawnDecision.FixtureLine(
                "Gun_Test", 7, "Graphic_Single", null, 0, 0, "BadTex", false,
                "Graphic_Random", "Things/Test", -1);

            // a null texture prints as null, an unusable size as null, and the count is absent
            Assert.Contains(" tex=null", line);
            Assert.Contains(" uiIcon=BadTex", line);
            Assert.Contains(" same=false", line);
            Assert.EndsWith(" subgraphics=-", line);
            // and the shared fields really are the origin/spawn ones, so one grep finds all three lines
            Assert.Contains(" def=Gun_Test id=7 graphic=Graphic_Single", line);
        }

        [Fact]
        public void MissingValuesNeverProduceAnEmptyField()
        {
            string line = TestSpawnDecision.FixtureLine(null, 0, null, null, 0, 0, null, false, null, null, -1);
            Assert.Equal(
                "test: fixture def=null id=0 graphic=null tex=null size=null uiIcon=null same=false"
                + " graphicClass=null texPath=null subgraphics=-",
                line);
        }
    }
}
