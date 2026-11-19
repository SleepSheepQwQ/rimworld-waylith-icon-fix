using Xunit;

namespace AgentTouchFix.Tests
{
    /// <summary>
    /// The three truth tables behind the 2.6.1 regressions. Every row here is a shape the shipped
    /// assembly actually has (or could have), taken from docs/REFLECTION-FACTS.md - so a wrong
    /// decision fails in CI instead of in the device log.
    /// </summary>
    public class MemberChoiceTests
    {
        // ------------------------------------------------------------------ field vs property

        [Theory]
        // field usable: field wins when it is preferred, property wins when it is
        [InlineData(true, true, true, MemberChoice.Field)]
        [InlineData(true, true, false, MemberChoice.Property)]
        // only a field: still the only answer in both orders
        [InlineData(true, false, true, MemberChoice.Field)]
        [InlineData(true, false, false, MemberChoice.Field)]
        // only a property: the fallback the device log asked for ("resolved as property")
        [InlineData(false, true, true, MemberChoice.Property)]
        [InlineData(false, true, false, MemberChoice.Property)]
        // neither: nothing, and the caller must leave the draw alone
        [InlineData(false, false, true, MemberChoice.None)]
        [InlineData(false, false, false, MemberChoice.None)]
        public void ChoosePicksFieldFirstWhenAsked(bool fieldUsable, bool propertyUsable, bool preferField, int expected)
        {
            Assert.Equal(expected, MemberChoice.Choose(fieldUsable, propertyUsable, preferField));
        }

        [Fact]
        public void EveryKindHasTheWordTheLogPrints()
        {
            Assert.Equal("field", MemberChoice.Name(MemberChoice.Field));
            Assert.Equal("property", MemberChoice.Name(MemberChoice.Property));
            Assert.Equal("none", MemberChoice.Name(MemberChoice.None));
            Assert.Equal("field", MemberChoice.Name(MemberChoice.Choose(true, false, true)));
            Assert.Equal("property", MemberChoice.Name(MemberChoice.Choose(false, true, true)));
        }

        // ------------------------------------------------------------------ GetIconFor overloads

        /// <summary>
        /// The three overloads of Verse.Widgets.GetIconFor in Assembly-CSharp 1.7.9795.32073, as
        /// (first parameter is Thing, first parameter is ThingDef, by-ref Material, by-ref Color):
        /// </summary>
        [Theory]
        // GetIconFor(ThingDef, ThingDef, ThingStyleDef, int?) - forwards to the 5-argument one,
        // hands out no material: patching it would run the fallback twice per draw.
        [InlineData(false, true, false, false, MemberChoice.Reject)]
        // GetIconFor(ThingDef, out Material, ThingDef, ThingStyleDef, int?)
        [InlineData(false, true, true, false, MemberChoice.DefOverload)]
        // GetIconFor(Thing, Vector2, Rot4?, bool, out float, out float, out Vector2, out Color, out Material)
        [InlineData(true, false, true, true, MemberChoice.ThingOverload)]
        // a Thing overload without the by-ref colour has no postfix shape to bind to
        [InlineData(true, false, true, false, MemberChoice.Reject)]
        // no by-ref material at all: nothing to replace
        [InlineData(true, false, false, true, MemberChoice.Reject)]
        [InlineData(false, true, false, true, MemberChoice.Reject)]
        [InlineData(false, false, true, true, MemberChoice.Reject)]
        [InlineData(false, false, false, false, MemberChoice.Reject)]
        public void GetIconForAcceptsExactlyTheTwoEntryPoints(bool firstIsThing, bool firstIsThingDef,
                                                              bool hasByRefMaterial, bool hasByRefColor, int expected)
        {
            Assert.Equal(expected, MemberChoice.GetIconFor(firstIsThing, firstIsThingDef, hasByRefMaterial, hasByRefColor));
        }

        [Fact]
        public void TheShippedOverloadSetPatchesTwo()
        {
            // counted, not eyeballed: this is the "patched 2" the startup log has to print.
            int patched = 0;
            patched += MemberChoice.GetIconFor(false, true, false, false) == MemberChoice.Reject ? 0 : 1;
            patched += MemberChoice.GetIconFor(false, true, true, false) == MemberChoice.Reject ? 0 : 1;
            patched += MemberChoice.GetIconFor(true, false, true, true) == MemberChoice.Reject ? 0 : 1;
            Assert.Equal(2, patched);
        }

        // ------------------------------------------------------------------ Standable

        [Theory]
        [InlineData(true, true, MemberChoice.StandableStatic)]    // the shipped shape wins
        [InlineData(true, false, MemberChoice.StandableStatic)]   // Verse.GenGrid.Standable(IntVec3, Map)
        [InlineData(false, true, MemberChoice.StandableInstance)] // IntVec3.Standable(Map)
        [InlineData(false, false, MemberChoice.StandableNone)]    // fail-open: every cell usable
        public void StandablePrefersTheStaticForm(bool staticFound, bool instanceFound, int expected)
        {
            Assert.Equal(expected, MemberChoice.Standable(staticFound, instanceFound));
        }

        [Fact]
        public void StandableNamesMatchTheLog()
        {
            Assert.Equal("static", MemberChoice.StandableName(MemberChoice.StandableStatic));
            Assert.Equal("instance", MemberChoice.StandableName(MemberChoice.StandableInstance));
            Assert.Equal("none", MemberChoice.StandableName(MemberChoice.StandableNone));
        }
    }
}
