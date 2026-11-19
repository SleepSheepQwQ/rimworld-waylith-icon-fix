using Xunit;

namespace AgentTouchFix.Tests
{
    /// <summary>
    /// TestSpawnDecision - the log lines of the read-only diagnostic, asserted character for
    /// character. The whole point of the module is a log the user can grep, so the field order and
    /// the spelling of every column are part of the contract.
    /// </summary>
    public class TestSpawnLineTests
    {
        /// <summary>The line as it appears in the log, with every field populated.</summary>
        [Fact]
        public void ThingLineHasTheDocumentedShape()
        {
            string line = TestSpawnDecision.ThingLine(
                "Gun_Revolver_Unique", 1234, "Graphic_Collection", "UniqueRevolverA", 512, 512,
                "UniqueRevolverA", true, 5);

            Assert.Equal("test: thing def=Gun_Revolver_Unique id=1234 graphic=Graphic_Collection" +
                         " tex=UniqueRevolverA size=512x512 uiIcon=UniqueRevolverA same=true subgraphics=5",
                         line);
        }

        /// <summary>The eight columns in this order, because the report and the docs quote them in this
        /// order and a reader diffs two lines by eye.</summary>
        [Fact]
        public void TheColumnsAppearInTheDocumentedOrder()
        {
            string line = TestSpawnDecision.ThingLine("D", 1, "G", "T", 1, 1, "U", false, 2);

            int def = line.IndexOf(" def=", System.StringComparison.Ordinal);
            int id = line.IndexOf(" id=", System.StringComparison.Ordinal);
            int graphic = line.IndexOf(" graphic=", System.StringComparison.Ordinal);
            int tex = line.IndexOf(" tex=", System.StringComparison.Ordinal);
            int size = line.IndexOf(" size=", System.StringComparison.Ordinal);
            int icon = line.IndexOf(" uiIcon=", System.StringComparison.Ordinal);
            int same = line.IndexOf(" same=", System.StringComparison.Ordinal);
            int subs = line.IndexOf(" subgraphics=", System.StringComparison.Ordinal);

            Assert.StartsWith("test: thing", line);
            Assert.True(def > 0 && id > def && graphic > id && tex > graphic && size > tex
                        && icon > size && same > icon && subs > same,
                        "columns are out of order: " + line);
        }

        /// <summary>What a blank icon looks like on this port: the instance texture is the BadTex
        /// placeholder and nothing has a size.</summary>
        [Fact]
        public void ADeadTexturePrintsBadTexAndNoSize()
        {
            string line = TestSpawnDecision.ThingLine("UniqueRevolverA", 7, "Graphic_Random", "BadTex", 0, 0, "BadTex", true, -1);

            Assert.Equal("test: thing def=UniqueRevolverA id=7 graphic=Graphic_Random" +
                         " tex=BadTex size=null uiIcon=BadTex same=true subgraphics=-", line);
        }

        [Fact]
        public void MissingValuesPrintAsNullRatherThanBlanks()
        {
            const string expected = "test: thing def=null id=0 graphic=null tex=null size=null" +
                                    " uiIcon=null same=false subgraphics=-";

            Assert.Equal(expected, TestSpawnDecision.ThingLine(null, 0, null, null, 0, 0, null, false, -1));
            Assert.Equal(expected, TestSpawnDecision.ThingLine("", 0, "", "", 0, 0, "", false, -1));
            Assert.Equal(expected, TestSpawnDecision.ThingLine("   ", 0, " \t", " ", 0, 0, "  ", false, -1));
        }

        /// <summary>One thing is one line: a defName that arrives with a newline must not split the
        /// entry and break line-based counting.</summary>
        [Fact]
        public void ControlCharactersCollapseSoOneThingStaysOneLine()
        {
            string line = TestSpawnDecision.ThingLine("Weapon\nRanged\tA", 3, "Graphic_Single", "Tex", 128, 128, "Tex", false, -1);

            Assert.DoesNotContain("\n", line);
            Assert.DoesNotContain("\t", line);
            Assert.StartsWith("test: thing def=Weapon Ranged A id=3", line);
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

        /// <summary>The variant column: a real count, including a degenerate zero, or "-" when the
        /// graphic has no variants at all. A missing count must never read as "zero variants".</summary>
        [Theory]
        [InlineData(-1, "-")]
        [InlineData(0, "0")]
        [InlineData(1, "1")]
        [InlineData(5, "5")]
        [InlineData(10, "10")]
        public void SubGraphicsPrintsACountOrADash(int count, string expected)
        {
            Assert.Equal(expected, TestSpawnDecision.SubGraphicsText(count));
        }

        [Fact]
        public void SkipLineHasTheDocumentedShape()
        {
            Assert.Equal("test: skip line cap reached cap=20", TestSpawnDecision.SkipLine("line cap reached cap=20"));
            Assert.Equal("test: skip null", TestSpawnDecision.SkipLine(null));
        }

        /// <summary>The one line an empty load is allowed to write, spelled out in full - this exact
        /// string is what a reader greps for.</summary>
        [Fact]
        public void NoPlayerPawnsLineIsExactlyAsDocumented()
        {
            Assert.Equal("test: skip no-player-pawns",
                         TestSpawnDecision.SkipLine(TestSpawnDecision.NoPlayerPawns));
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
    /// The output budget: a hard cap per load, because the module writes into the player's own log.
    /// </summary>
    public class TestSpawnBudgetTests
    {
        [Fact]
        public void TheLineBudgetIsTwentyPerLoad()
        {
            Assert.Equal(20, TestSpawnDecision.LineCap);
        }

        [Theory]
        [InlineData(0, 20, true)]
        [InlineData(1, 20, true)]
        [InlineData(19, 20, true)]
        [InlineData(20, 20, false)]
        [InlineData(21, 20, false)]
        [InlineData(0, 0, false)]
        public void HasRoomStopsExactlyAtTheCap(int written, int cap, bool expected)
        {
            Assert.Equal(expected, TestSpawnDecision.HasRoom(written, cap));
        }
    }
}
