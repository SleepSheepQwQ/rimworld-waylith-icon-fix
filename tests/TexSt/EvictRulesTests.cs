using System;
using System.Collections.Generic;
using AgentTouchFix;
using Xunit;

namespace TexSt
{
    /// <summary>
    /// Pins the eviction policy. Each case maps to a failure mode observed in the port's texture
    /// residency code or to a scenario the design review called out (docs/EVICTION.md).
    /// </summary>
    public class EvictRulesTests
    {
        private static EvictAction Decide(bool hasSource, bool file, bool durable, bool pixels, bool pressured)
            => EvictRules.Decide(new EvictFacts(hasSource, file, durable, pixels, pressured));

        // ---- the four outcomes, one at a time ------------------------------------------------

        [Fact]
        public void NoSourcePath_IsNeverEvicted()
        {
            // Procedural textures, whiteTexture/redTexture, render targets: nothing to reopen from.
            Assert.Equal(EvictAction.Keep, Decide(hasSource: false, file: false, durable: false, pixels: true, pressured: false));
            Assert.Equal(EvictAction.Keep, Decide(false, true, true, true, true));   // even "durable" is meaningless
        }

        [Fact]
        public void LooseFile_EvictsWithoutAnyWrite()
        {
            Assert.Equal(EvictAction.Evict, Decide(true, file: true, durable: false, pixels: false, pressured: false));
        }

        [Fact]
        public void AlreadyPersisted_EvictsWithoutWritingAgain()
        {
            // The durable memo is what stops "write the whole texture on every eviction".
            Assert.Equal(EvictAction.Evict, Decide(true, file: false, durable: true, pixels: true, pressured: false));
            Assert.Equal(EvictAction.Evict, Decide(true, false, true, false, false));
        }

        [Fact]
        public void PixelsInRam_ArePersistedOnceThenDropped()
        {
            Assert.Equal(EvictAction.PersistThenEvict, Decide(true, false, false, pixels: true, pressured: false));
            Assert.Equal(EvictAction.PersistThenEvict, Decide(true, false, false, true, pressured: true));
        }

        [Fact]
        public void NothingToReopenFrom_IsKeptWhenThereIsRoom()
        {
            Assert.Equal(EvictAction.Keep, Decide(true, false, false, pixels: false, pressured: false));
        }

        [Fact]
        public void NothingToReopenFrom_IsDroppedOnlyUnderPressure()
        {
            Assert.Equal(EvictAction.ForcedEvict, Decide(true, false, false, pixels: false, pressured: true));
        }

        // ---- ordering: the cheap facts win before any work happens ---------------------------

        [Fact]
        public void FileCheckBeatsPixels_SoNoWriteEverHappensForLooseFiles()
        {
            var withPixels = Decide(true, file: true, durable: false, pixels: true, pressured: false);
            Assert.Equal(EvictAction.Evict, withPixels);
        }

        [Fact]
        public void DurableBeatsPixels_SoASecondEvictionNeverWrites()
        {
            Assert.Equal(EvictAction.Evict, Decide(true, false, durable: true, pixels: true, pressured: false));
        }

        // ---- repeated eviction of the same texture (the "每次都要读写" concern) --------------

        [Fact]
        public void RepeatedEvictions_WriteAtMostOncePerTexture()
        {
            bool durable = false;
            int writes = 0;

            for (int i = 0; i < 25; i++)
            {
                var action = Decide(true, file: false, durable: durable, pixels: true, pressured: false);
                if (action == EvictAction.PersistThenEvict)
                {
                    writes++;
                    durable = true;                  // MarkDurable() after a successful write
                    continue;                        // WriteTextureCache queues the eviction itself
                }
                Assert.Equal(EvictAction.Evict, action);
            }

            Assert.Equal(1, writes);
        }

        [Fact]
        public void PixelsDisappearingAfterTheFirstEviction_StillEvicts()
        {
            // Once persisted, the CPU pixels may be released; the cache covers the texture from then on.
            bool durable = true;
            Assert.Equal(EvictAction.Evict, Decide(true, false, durable, pixels: false, pressured: false));
            Assert.True(durable);
        }

        [Theory]
        [InlineData(true, true, true, true)]      // everything available: Evict
        [InlineData(true, false, true, true)]     // durable: Evict
        [InlineData(false, true, true, true)]     // no source: Keep, whatever else is true
        public void DecisionIsAlwaysOneOfTheFourKnownActions(bool hasSource, bool file, bool durable, bool pixels)
        {
            var action = Decide(hasSource, file, durable, pixels, pressured: false);
            Assert.True(Enum.IsDefined(typeof(EvictAction), action));
        }

        // ---- budget arithmetic --------------------------------------------------------------

        [Fact]
        public void OverBudget_IsStrictlyGreater()
        {
            Assert.False(EvictRules.OverBudget(1024, 1024));
            Assert.True(EvictRules.OverBudget(1025, 1024));
            Assert.False(EvictRules.OverBudget(0, 1024));
        }

        [Fact]
        public void UnknownBudget_NeverReportsPressure()
        {
            // A failed /proc/meminfo read must not look like "lots of room" *or* "no room".
            Assert.False(EvictRules.OverBudget(1L << 30, 0));
            Assert.False(EvictRules.OverBudget(-1, 1024));
        }

        [Theory]
        [InlineData(2L * 1024 * 1024 * 1024, 512L * 1024 * 1024)]      // 2 GiB -> 512 MiB
        [InlineData(8L * 1024 * 1024 * 1024, 2L * 1024 * 1024 * 1024)] // 8 GiB -> capped at 2 GiB
        [InlineData(512L * 1024 * 1024, 256L * 1024 * 1024)]           // 512 MiB -> floored at 256 MiB
        [InlineData(0, 256L * 1024 * 1024)]                            // unknown -> floor
        public void KeepBudget_IsAQuarterOfRamWithinBounds(long total, long expected)
        {
            Assert.Equal(expected, EvictRules.KeepBudget(total));
        }

        // ---- the scenario matrix from the design review -------------------------------------

        public static IEnumerable<object[]> Scenarios()
        {
            // name, hasSource, file, durable, pixels, pressured, expected
            yield return new object[] { "pack texture, pixels released, no cache", true, false, false, false, false, EvictAction.Keep };
            yield return new object[] { "pack texture, pixels released, pressured", true, false, false, false, true, EvictAction.ForcedEvict };
            yield return new object[] { "pack texture, first eviction", true, false, false, true, false, EvictAction.PersistThenEvict };
            yield return new object[] { "loose mod texture", true, true, false, false, false, EvictAction.Evict };
            yield return new object[] { "loose file deleted by a cleaner", true, false, true, false, false, EvictAction.Evict };
            yield return new object[] { "procedural / solid texture", false, false, false, true, false, EvictAction.Keep };
            yield return new object[] { "atlas temp file gone", true, false, false, false, false, EvictAction.Keep };
        }

        [Theory]
        [MemberData(nameof(Scenarios))]
        public void ScenarioMatrix(string name, bool hasSource, bool file, bool durable, bool pixels, bool pressured, EvictAction expected)
        {
            Assert.True(expected == Decide(hasSource, file, durable, pixels, pressured), name);
        }
    }
}
