using System;
using Xunit;

namespace AgentTouchFix.Tests
{
    public class IconVariantPathTests
    {
        [Fact]
        public void PicksFirstConcreteVariantInsteadOfCollectionFolder()
        {
            string[] variants =
            {
                "Things/Weapons/Folder/RevolverA.png",
                "Things/Weapons/Folder/RevolverB.png",
            };

            Assert.Equal(variants[0],
                IconVariantPath.Choose(variants, "Things/Weapons/Folder"));
        }

        [Fact]
        public void SkipsMasksEvenWhenTheySortFirst()
        {
            string[] variants =
            {
                "Things/Weapons/Folder/RevolverA_m.png",
                "Things/Weapons/Folder/RevolverA.png",
            };

            Assert.Equal(variants[1],
                IconVariantPath.Choose(variants, "Things/Weapons/Folder"));
        }

        [Fact]
        public void FallsBackToGraphicPathWhenNoDrawableVariantExists()
        {
            string[] variants =
            {
                null,
                "",
                "Things/Weapons/Folder/RevolverA_m.png",
            };

            Assert.Equal("Things/Weapons/Folder",
                IconVariantPath.Choose(variants, "Things/Weapons/Folder"));
            Assert.Equal("Things/Weapons/Folder",
                IconVariantPath.Choose(Array.Empty<string>(), "Things/Weapons/Folder"));
            Assert.Equal("Things/Weapons/Folder",
                IconVariantPath.Choose(null, "Things/Weapons/Folder"));
        }
    }
}
