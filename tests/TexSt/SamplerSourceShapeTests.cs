using System;
using System.IO;
using Xunit;

namespace AgentTouchFix.Tests
{
    /// <summary>
    /// Shape checks on the patch sources that behaviour tests cannot reach: the mod cannot be detoured on a
    /// CI runner, so what is asserted here is that the two promises in the review of this patch still hold -
    /// it is wired into bootstrap, and it writes exactly one line.
    ///
    /// This reads the sources as text on purpose. A per-texture log line is a real regression that compiles
    /// and passes every other test, and it only shows up as noise on a device.
    /// </summary>
    public class SamplerSourceShapeTests
    {
        private static string Source(string name)
        {
            DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "mod", "Source", name);
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
                dir = dir.Parent;
            }
            throw new FileNotFoundException("mod/Source/" + name + " not found above " + AppContext.BaseDirectory);
        }

        [Fact]
        public void BootstrapInstallsTheSamplerRedirect()
        {
            Assert.Contains("SamplerFix.Install()", Source("Bootstrap.cs"));
        }

        [Fact]
        public void TheSuccessLineIsASingleOneShotSummary()
        {
            string src = Source("SamplerFix.cs");
            string summary = src.Substring(src.IndexOf("private static void MaybeLogSummary", StringComparison.Ordinal));

            Assert.Equal(1, Occurrences(summary, "Refl.Info("));
            Assert.Contains("SummaryDelayMs", summary);
            Assert.Contains("Interlocked.Exchange(ref _summaryLogged, 1)", summary);
        }

        [Fact]
        public void ThePerTexturePathDoesNotLog()
        {
            string src = Source("SamplerFix.cs");
            int start = src.IndexOf("private static object Pick(", StringComparison.Ordinal);
            int end = src.IndexOf("private static object NonMippedSampler", StringComparison.Ordinal);
            string pick = src.Substring(start, end - start);

            Assert.DoesNotContain("Refl.Info(", pick);
            Assert.Contains("MaybeLogSummary()", pick);
        }

        [Fact]
        public void EveryReflectionStepFailsOpen()
        {
            string src = Source("SamplerFix.cs");

            // one Warn per way the reflection can come up short, and no throw out of Install
            Assert.Contains("not loaded, sampler left as shipped", src);
            Assert.Contains("no instance ", src);
            Assert.Contains("not reachable by reflection, sampler left as shipped", src);
            Assert.Contains("have different types", src);
            Assert.Contains("install failed, fail-open", src);

            int install = src.IndexOf("internal static void Install()", StringComparison.Ordinal);
            int hardened = src.IndexOf("private static void Hardened", StringComparison.Ordinal);
            Assert.DoesNotContain("throw new", src.Substring(install, hardened - install));
            Assert.Contains("catch (Exception e)", src.Substring(install, hardened - install));
        }

        private static int Occurrences(string haystack, string needle)
        {
            int count = 0;
            for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            {
                count++;
            }
            return count;
        }
    }
}
