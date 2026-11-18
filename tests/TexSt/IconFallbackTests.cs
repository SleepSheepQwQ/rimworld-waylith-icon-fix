using Xunit;

namespace AgentTouchFix.Tests
{
    /// <summary>
    /// The fallback decision table: (original result) x (def.uiIcon usable) x (def graphic usable).
    /// A drawable result must never be replaced, uiIcon must beat the graphic, and "nothing usable"
    /// must leave the original alone so Widgets keeps its own behaviour.
    /// </summary>
    public class IconFallbackTests
    {
        private const string Ok = "ok";
        private const string Null = "null";
        private const string Bad = "bad";

        private static bool Drawable(string state)
        {
            // mirrors what the mod computes from the live texture: present && != BadTex
            return state == Ok;
        }

        [Theory]
        // result drawable: never touched, whatever the def has
        [InlineData(Ok, Ok, Ok, "")]
        [InlineData(Ok, Ok, Bad, "")]
        [InlineData(Ok, Bad, Ok, "")]
        [InlineData(Ok, Bad, Bad, "")]
        // result null: uiIcon first, then graphic
        [InlineData(Null, Ok, Ok, "uiIcon")]
        [InlineData(Null, Ok, Bad, "uiIcon")]
        [InlineData(Null, Bad, Ok, "graphic")]
        [InlineData(Null, Bad, Bad, "")]
        // result BadTex: identical table - BadTex is as unusable as null
        [InlineData(Bad, Ok, Ok, "uiIcon")]
        [InlineData(Bad, Ok, Bad, "uiIcon")]
        [InlineData(Bad, Bad, Ok, "graphic")]
        [InlineData(Bad, Bad, Bad, "")]
        public void ChoosesInOrder(string result, string uiIcon, string graphic, string expected)
        {
            string source = IconFallbackDecision.Choose(Drawable(result), Drawable(uiIcon), Drawable(graphic));
            Assert.Equal(expected, source);
        }

        [Fact]
        public void UiIconBeatsTheGraphicWhenBothAreUsable()
        {
            Assert.Equal(IconFallbackDecision.UiIcon,
                IconFallbackDecision.Choose(false, true, true));
            // and the fallback still fires for a BadTex result, not only for null
            Assert.Equal(IconFallbackDecision.Graphic,
                IconFallbackDecision.Choose(false, false, true));
            Assert.Equal(IconFallbackDecision.None,
                IconFallbackDecision.Choose(false, false, false));
        }

        [Fact]
        public void ADrawableResultIsLeftExactlyAsItIs()
        {
            Assert.Equal(IconFallbackDecision.None, IconFallbackDecision.Choose(true, true, true));
            Assert.Equal(IconFallbackDecision.None, IconFallbackDecision.Choose(true, false, false));
        }

        [Theory]
        [InlineData(false, false, true)]    // present, not BadTex
        [InlineData(false, true, false)]    // BadTex
        [InlineData(true, false, false)]    // null
        [InlineData(true, true, false)]     // null and "bad" - still unusable
        public void DrawableIsPresentAndNotBadTex(bool isNull, bool isBadTex, bool expected)
        {
            Assert.Equal(expected, IconFallbackDecision.Drawable(isNull, isBadTex));
        }

        [Fact]
        public void SourceStringsAreTheOnesTheLogEmits()
        {
            // the first-hit log line is "icon fallback: <defName> via <source>"
            Assert.Equal("uiIcon", IconFallbackDecision.UiIcon);
            Assert.Equal("graphic", IconFallbackDecision.Graphic);
            Assert.Equal("", IconFallbackDecision.None);
        }

        [Theory]
        [InlineData(null, true)]            // no switch file
        [InlineData("", true)]
        [InlineData("   ", true)]
        [InlineData("off", false)]
        [InlineData("OFF", false)]
        [InlineData(" off ", false)]
        [InlineData("0", false)]
        [InlineData("false", false)]
        [InlineData("on", true)]            // unrecognised -> stay on
        [InlineData("??", true)]
        public void SwitchTurnsTheFixOffOnlyWhenAsked(string switchText, bool expected)
        {
            Assert.Equal(expected, IconFallbackDecision.Enabled(switchText));
        }
    }
}
