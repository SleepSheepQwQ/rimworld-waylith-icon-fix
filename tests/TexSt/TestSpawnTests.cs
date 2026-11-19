using Xunit;

namespace AgentTouchFix.Tests
{
    /// <summary>
    /// TestSpawnDecision - the two log lines of the diagnostic module, asserted character for
    /// character: the whole point of the module is a log the user can grep, and the origin/spawn pair
    /// is only useful when both lines carry the same fields in the same order.
    /// </summary>
    public class TestSpawnLineTests
    {
        /// <summary>The pair as it appears in the log, with every field populated.</summary>
        [Fact]
        public void OriginAndSpawnLinesHaveTheDocumentedShape()
        {
            string origin = TestSpawnDecision.OriginLine(
                "UniqueRevolverA", 1234, "Graphic_Single", "UniqueRevolverA", 512, 512, "UniqueRevolverA", true);
            string spawn = TestSpawnDecision.SpawnLine(
                "UniqueRevolverA", 1235, "Graphic_Single", "UniqueRevolverA", 512, 512, "UniqueRevolverA", true);

            Assert.Equal("test: origin def=UniqueRevolverA id=1234 graphic=Graphic_Single" +
                         " tex=UniqueRevolverA size=512x512 uiIcon=UniqueRevolverA same=true", origin);
            Assert.Equal("test: spawn  def=UniqueRevolverA id=1235 graphic=Graphic_Single" +
                         " tex=UniqueRevolverA size=512x512 uiIcon=UniqueRevolverA same=true", spawn);
        }

        /// <summary>The two lines carry the same fields in the same order, so the only differences a
        /// reader (or a diff) sees are the prefix and the id.</summary>
        [Fact]
        public void TheTwoLinesDifferOnlyInTheirPrefixAndId()
        {
            // same id on purpose: then everything after the prefix must be identical
            string origin = TestSpawnDecision.OriginLine("X", 1, "Graphic_Single", "X", 64, 64, "X", true);
            string spawn = TestSpawnDecision.SpawnLine("X", 1, "Graphic_Single", "X", 64, 64, "X", true);

            Assert.Equal(origin.Substring("test: origin ".Length),
                         spawn.Substring("test: spawn  ".Length));

            // with different ids the id is the only further difference
            string other = TestSpawnDecision.SpawnLine("X", 2, "Graphic_Single", "X", 64, 64, "X", true);
            Assert.Equal(origin.Replace("test: origin ", "test: spawn  ").Replace(" id=1 ", " id=2 "), other);
            Assert.NotEqual(origin, other);
        }

        /// <summary>What a blank icon looks like on this port: the instance texture is the BadTex
        /// placeholder and nothing has a size.</summary>
        [Fact]
        public void ADeadTexturePrintsBadTexAndNoSize()
        {
            string line = TestSpawnDecision.OriginLine("UniqueRevolverA", 7, "Graphic_Random", "BadTex", 0, 0, "BadTex", true);

            Assert.Equal("test: origin def=UniqueRevolverA id=7 graphic=Graphic_Random" +
                         " tex=BadTex size=null uiIcon=BadTex same=true", line);
        }

        [Fact]
        public void MissingValuesPrintAsNullRatherThanBlanks()
        {
            const string expected = "test: spawn  def=null id=0 graphic=null tex=null size=null" +
                                    " uiIcon=null same=false";

            Assert.Equal(expected, TestSpawnDecision.SpawnLine(null, 0, null, null, 0, 0, null, false));
            Assert.Equal(expected, TestSpawnDecision.SpawnLine("", 0, "", "", 0, 0, "", false));
            Assert.Equal(expected, TestSpawnDecision.SpawnLine("   ", 0, " \t", " ", 0, 0, "  ", false));
        }

        /// <summary>One thing is one line: a defName or a label that arrives with a newline must not
        /// split the entry and break line-based counting.</summary>
        [Fact]
        public void ControlCharactersCollapseSoOneThingStaysOneLine()
        {
            string line = TestSpawnDecision.OriginLine("Weapon\nRanged\tA", 3, "Graphic_Single", "Tex", 128, 128, "Tex", false);

            Assert.DoesNotContain("\n", line);
            Assert.DoesNotContain("\t", line);
            Assert.StartsWith("test: origin def=Weapon Ranged A id=3", line);
            Assert.Single(line.Split('\n'));
        }

        [Fact]
        public void AVeryLongValueIsCutAndMarked()
        {
            string cell = TestSpawnDecision.Cell(new string('x', TestSpawnDecision.MaxCell + 50));

            Assert.Equal(TestSpawnDecision.MaxCell + 1, cell.Length);
            Assert.EndsWith("~", cell);
            // a value of exactly the cap is untouched
            Assert.Equal(TestSpawnDecision.MaxCell,
                         TestSpawnDecision.Cell(new string('y', TestSpawnDecision.MaxCell)).Length);
        }

        [Theory]
        [InlineData(512, 512, "512x512")]
        [InlineData(1, 1, "1x1")]
        [InlineData(0, 0, "null")]
        [InlineData(512, 0, "null")]
        [InlineData(0, 512, "null")]
        [InlineData(-1, 4, "null")]
        public void SizeNeedsBothDimensions(int width, int height, string expected)
        {
            Assert.Equal(expected, TestSpawnDecision.Size(width, height));
        }

        [Fact]
        public void SkipAndFallbackLinesHaveTheDocumentedShape()
        {
            Assert.Equal("test: skip no placeable cell at 12,34",
                         TestSpawnDecision.SkipLine("no placeable cell at 12,34"));
            Assert.Equal("test: skip null", TestSpawnDecision.SkipLine(null));
            Assert.Equal("test: fallback plasteel longsword from=12,34 to=13,34",
                         TestSpawnDecision.FallbackLine("plasteel longsword", "12,34", "13,34"));
        }

        [Fact]
        public void TheLineBudgetIsTwentyPerLoad()
        {
            Assert.Equal(20, TestSpawnDecision.LineCap);
        }
    }

    /// <summary>
    /// The <c>same</c> column, in full. It answers exactly one question - is the texture the draw path
    /// would use the very same object as def.uiIcon - because that is the bit that separates "the
    /// instance graphic is fine" from "the icon only looks fine because the def rescued it".
    ///
    /// Two absences answer false even though ReferenceEquals(null, null) is true: printing "same" for a
    /// thing with no texture at all would read as a healthy match.
    /// </summary>
    public class TestSpawnSameTests
    {
        [Theory]
        // no instance texture -> nothing can match
        [InlineData(true, true, true, false)]
        [InlineData(true, false, false, false)]
        [InlineData(true, false, true, false)]
        // no uiIcon -> nothing can match
        [InlineData(false, true, false, false)]
        [InlineData(false, true, true, false)]
        // both present: the reference comparison decides
        [InlineData(false, false, true, true)]
        [InlineData(false, false, false, false)]
        public void TruthTable(bool texNull, bool iconNull, bool referenceEqual, bool expected)
        {
            Assert.Equal(expected, TestSpawnDecision.Same(texNull, iconNull, referenceEqual));
        }

        [Fact]
        public void AbsenceIsNeverReportedAsAMatch()
        {
            Assert.False(TestSpawnDecision.Same(true, true, true));
            Assert.False(TestSpawnDecision.Same(true, false, true));
            Assert.False(TestSpawnDecision.Same(false, true, true));
        }

        [Fact]
        public void OnlyAnIdenticalPairOfObjectsMatches()
        {
            Assert.True(TestSpawnDecision.Same(false, false, true));
            Assert.False(TestSpawnDecision.Same(false, false, false));
        }
    }

    /// <summary>
    /// Where the copy goes: the pawn's own cell first, an adjacent cell only when the pawn's cell
    /// cannot hold a thing, and nowhere at all when neither can. The full four-case table, because each
    /// row is a different outcome the caller must log (here / fallback line / skip line).
    /// </summary>
    public class TestSpawnPlacementTests
    {
        [Theory]
        // cell usable: always here, whatever the neighbours look like
        [InlineData(true, true, "Here")]
        [InlineData(true, false, "Here")]
        // cell unusable: next door if possible, otherwise nothing
        [InlineData(false, true, "Adjacent")]
        [InlineData(false, false, "None")]
        public void ChoosesTheCellThenANeighbour(bool cellUsable, bool neighbourUsable, string expected)
        {
            Assert.Equal(expected, TestSpawnDecision.Choose(cellUsable, neighbourUsable).ToString());
        }

        [Fact]
        public void TheCellIsPreferredOverAnyNeighbour()
        {
            // an occupied cell (an item stack already lying there) is a fine place: things stack
            Assert.Equal("Here", TestSpawnDecision.Choose(true, true).ToString());
        }

        [Fact]
        public void NothingIsPlacedWhenThereIsNowhereToPutIt()
        {
            Assert.Equal("None", TestSpawnDecision.Choose(false, false).ToString());
        }
    }
}
