using System;

namespace AgentTouchFix
{
    /// <summary>What an eviction attempt should do, decided from facts only - no engine types, no I/O.</summary>
    public enum EvictAction
    {
        /// <summary>The engine may drop the GPU copy now.</summary>
        Evict,

        /// <summary>Pixels exist only in RAM: write them to the cache, then the engine drops the copy.</summary>
        PersistThenEvict,

        /// <summary>Nothing cheap to reopen from: keep the GPU copy.</summary>
        Keep,

        /// <summary>Nothing cheap to reopen from, but memory is tight: accept the engine's gamble.</summary>
        ForcedEvict
    }

    /// <summary>Everything the decision needs, captured by the caller.</summary>
    public readonly struct EvictFacts
    {
        public readonly bool HasSource;    // SourcePath != null
        public readonly bool FileExists;   // the loose file is readable
        public readonly bool Durable;      // we already persisted this texture once
        public readonly bool HasPixels;    // CpuData.HasData
        public readonly bool Pressured;    // kept textures are over budget

        public EvictFacts(bool hasSource, bool fileExists, bool durable, bool hasPixels, bool pressured)
        {
            HasSource = hasSource;
            FileExists = fileExists;
            Durable = durable;
            HasPixels = hasPixels;
            Pressured = pressured;
        }
    }

    /// <summary>
    /// The eviction policy as arithmetic. Kept free of Harmony, reflection and the filesystem so the
    /// CI can pin it: every branch below corresponds to a failure mode found by reading the port's
    /// texture residency code (see docs/EVICTION.md).
    /// </summary>
    public static class EvictRules
    {
        public static EvictAction Decide(in EvictFacts f)
        {
            // Cannot rebuild it from anything: dropping it would blank every draw that uses it.
            if (!f.HasSource) return EvictAction.Keep;

            // Cheap and certain: a loose file is re-decoded on demand, and a texture we already
            // persisted is served by the cache. No I/O at eviction time either way.
            if (f.FileExists || f.Durable) return EvictAction.Evict;

            // Pixels are still in RAM: make them durable once, then let the engine reclaim the image.
            if (f.HasPixels) return EvictAction.PersistThenEvict;

            // No pixels, no file, no cache. Keeping is free of I/O but not of RAM, so under pressure
            // we accept the engine's behaviour instead of letting the process be killed.
            return f.Pressured ? EvictAction.ForcedEvict : EvictAction.Keep;
        }

        /// <summary>Keep-budget check used by the caller to compute <see cref="EvictFacts.Pressured"/>.</summary>
        public static bool OverBudget(long keptBytes, long budgetBytes)
        {
            if (budgetBytes <= 0) return false;      // unknown budget: never claim pressure
            if (keptBytes < 0) return false;
            return keptBytes > budgetBytes;
        }

        /// <summary>
        /// Budget for textures kept resident instead of evicted: a quarter of physical RAM, floored at
        /// 256 MiB (so small phones do not thrash) and capped at 2 GiB (so big phones do not hoard).
        /// </summary>
        public static long KeepBudget(long totalRamBytes)
        {
            const long floor = 256L << 20;
            const long cap = 2L << 30;
            if (totalRamBytes <= 0) return floor;
            long quarter = totalRamBytes / 4;
            if (quarter < floor) return floor;
            if (quarter > cap) return cap;
            return quarter;
        }
    }
}
