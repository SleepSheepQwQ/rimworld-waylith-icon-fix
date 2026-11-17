using System.Collections.Generic;
using Xunit;

namespace AgentTouchFix.Tests
{
    /// <summary>
    /// CollectionVariants.Filter - the variant list handed to Graphic_Random, with masks removed.
    ///
    /// Two things are asserted everywhere: the *decision* (which names survive) and the *restraint*
    /// (a list the filter does not fully understand comes back as the very same object, so nothing
    /// downstream is renumbered behind the caller's back).
    /// </summary>
    public class CollectionVariantsTests
    {
        /// <summary>
        /// Odyssey unique weapons, as this port names them: the inner ".png" survives into
        /// Texture2D.name, so Graphic_Collection's own EndsWith("_m") filter drops nothing and
        /// Graphic_Random ends up with [texture, mask, texture, mask].
        /// </summary>
        [Fact]
        public void OdysseyShapeKeepsOneVariantPerWeapon()
        {
            var portNames = new List<string>
            {
                "UniqueRevolverA.png", "UniqueRevolverA_m.png",
                "UniqueRevolverB.png", "UniqueRevolverB_m.png",
            };

            IReadOnlyList<string> kept = CollectionVariants.Filter(portNames);

            Assert.Equal(new[] { "UniqueRevolverA.png", "UniqueRevolverB.png" }, kept);
        }

        /// <summary>The same folder as vanilla would see it, and as .dds files on disk.</summary>
        [Fact]
        public void BareAndDoubleExtensionNamesGiveTheSameResult()
        {
            var bare = new List<string> { "UniqueRevolverA", "UniqueRevolverA_m" };
            var shipped = new List<string> { "UniqueRevolverA.png.dds", "UniqueRevolverA_m.png.dds" };

            Assert.Equal(new[] { "UniqueRevolverA" }, CollectionVariants.Filter(bare));
            Assert.Equal(new[] { "UniqueRevolverA.png.dds" }, CollectionVariants.Filter(shipped));
        }

        /// <summary>
        /// Animals: one mask per rotation ("_northm" / "_eastm" / "_southm" / "_westm", what
        /// Graphic_Multi.cs:210 asks for). The mask pairs with the directional texture of the same
        /// rotation, so four pairs collapse to the four directional textures.
        /// </summary>
        [Fact]
        public void AnimalDirectionMasksAreDropped()
        {
            var lynx = new List<string>
            {
                "Lynx_east.psd", "Lynx_eastm.psd",
                "Lynx_north.psd", "Lynx_northm.psd",
                "Lynx_south.psd", "Lynx_southm.psd",
                "Lynx_west.psd", "Lynx_westm.psd",
            };

            IReadOnlyList<string> kept = CollectionVariants.Filter(lynx);

            Assert.Equal(
                new[] { "Lynx_east.psd", "Lynx_north.psd", "Lynx_south.psd", "Lynx_west.psd" },
                kept);
        }

        /// <summary>Direction masks with the inner extension of the port, and a bare animal name.</summary>
        [Theory]
        [InlineData("Lynx_north.png", "Lynx_northm.png", "Lynx_north.png")]
        [InlineData("Lynx_west", "Lynx_westm", "Lynx_west")]
        [InlineData("Lynx_south.png.dds", "Lynx_southm.png.dds", "Lynx_south.png.dds")]
        public void DirectionMaskPairsAtAnyExtensionDepth(string main, string mask, string expected)
        {
            var names = new List<string> { main, mask };
            Assert.Equal(new[] { expected }, CollectionVariants.Filter(names));
        }

        /// <summary>
        /// The whole point of the shape check: [A, A_m, B, C] is not "texture plus mask" all the way
        /// down (B and C have no masks). Dropping A_m there would leave three variants where the game
        /// decided on four, so the list must come back untouched - the same instance.
        /// </summary>
        [Fact]
        public void IrregularShapeIsLeftUntouched()
        {
            var names = new List<string> { "A", "A_m", "B", "C" };
            var log = new List<string>();

            IReadOnlyList<string> kept = CollectionVariants.Filter(names, log.Add);

            Assert.Same(names, kept);
            Assert.Equal(new[] { "A", "A_m", "B", "C" }, kept);
            Assert.Equal(new[] { "collection: variant shape unexpected, left untouched (n=4)" }, log);
        }

        /// <summary>
        /// A collection with no masks at all - the common case - is not a shape this filter adopts, so
        /// it stays exactly as built and is reported once (the runtime side caps those lines at five).
        /// </summary>
        [Fact]
        public void PlainCollectionIsLeftUntouchedAndReported()
        {
            var names = new List<string> { "A", "B", "C" };
            var log = new List<string>();

            IReadOnlyList<string> kept = CollectionVariants.Filter(names, log.Add);

            Assert.Same(names, kept);
            Assert.Equal(new[] { "collection: variant shape unexpected, left untouched (n=3)" }, log);
        }

        /// <summary>Without a logger the same decision is silent - the postfix passes Refl.Info.</summary>
        [Fact]
        public void WithoutALoggerNothingIsWritten()
        {
            var names = new List<string> { "A", "B", "C" };
            Assert.Same(names, CollectionVariants.Filter(names));
        }

        /// <summary>Ambiguous shapes stay put: two textures under one key, and a mask with no texture.</summary>
        [Theory]
        [MemberData(nameof(Ambiguous))]
        public void AmbiguousShapesAreLeftUntouched(string[] names)
        {
            var list = new List<string>(names);
            var log = new List<string>();

            IReadOnlyList<string> kept = CollectionVariants.Filter(list, log.Add);

            Assert.Same(list, kept);
            Assert.Single(log);
            Assert.Equal("collection: variant shape unexpected, left untouched (n=" + names.Length + ")", log[0]);
        }

        public static IEnumerable<object[]> Ambiguous()
        {
            yield return new object[] { new[] { "X.png", "X.jpg" } };          // same key, both textures
            yield return new object[] { new[] { "X_m.png" } };                 // a mask with no texture
            yield return new object[] { new[] { "X", "X_m", "Y_m", "Y_m" } };  // two masks under one key
            yield return new object[] { new[] { "X", "X_m", null } };          // blank entry
        }

        [Fact]
        public void NullAndEmptyGiveAnEmptyList()
        {
            Assert.Empty(CollectionVariants.Filter(null));
            Assert.Empty(CollectionVariants.Filter(new List<string>()));
            Assert.Empty(CollectionVariants.Filter(null, _ => { }));
            Assert.Empty(CollectionVariants.Filter(new List<string>(), _ => { }));
        }

        /// <summary>
        /// Survivors is the index form the postfix rebuilds subGraphics from: indices ascend, and the
        /// result is null - not empty - when the shape check fails, so callers cannot mistake
        /// "refuse" for "drop everything".
        /// </summary>
        [Fact]
        public void SurvivorsReportsIndicesOrRefusal()
        {
            var good = new List<string> { "A.png", "A_m.png", "B.png", "B_m.png" };
            Assert.Equal(new[] { 0, 2 }, CollectionVariants.Survivors(good));

            var mixed = new List<string> { "A_m.png", "A.png", "B.png", "B_m.png" };
            Assert.Equal(new[] { 1, 2 }, CollectionVariants.Survivors(mixed));

            Assert.Null(CollectionVariants.Survivors(new List<string> { "A", "A_m", "B" }));
            Assert.Empty(CollectionVariants.Survivors(new List<string>()));
        }

        /// <summary>
        /// The regression this fix exists for: the game's own filter keeps every mask, so the variant
        /// count is double what it should be - and Graphic_Random divides by that count.
        /// </summary>
        [Fact]
        public void TheGamesOwnFilterKeepsMasksAndThisOneDoesNot()
        {
            string[] portNames =
            {
                "UniqueAssaultRifleA.png", "UniqueAssaultRifleA_m.png",
                "UniqueAssaultRifleB.png", "UniqueAssaultRifleB_m.png",
                "UniqueAssaultRifleC.png", "UniqueAssaultRifleC_m.png",
            };

            int naive = 0;
            foreach (string n in portNames)
            {
                if (!n.EndsWith("_m")) naive++;      // Graphic_Collection.cs:59
            }
            Assert.Equal(6, naive);

            Assert.Equal(3, CollectionVariants.Filter(portNames).Count);
        }
    }
}
