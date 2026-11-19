using System;
using System.IO;
using Verse;

namespace AgentTouchFix
{
    /// <summary>
    /// Entry point. RimWorld runs static constructors of types carrying this attribute, which is early
    /// enough for Harmony patches on the UI draw path.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        private const string SwitchPath =
            "/data/data/com.ports1de.waylith/files/game/agenttouchfix.iconpath";

        static Bootstrap()
        {
            try
            {
                Refl.Info("AgentTouchFix " + typeof(Bootstrap).Assembly.GetName().Version
                          + " bootstrapping (harmony " + typeof(HarmonyLib.Harmony).Assembly.GetName().Version + ")");

                string mode = "";
                try
                {
                    if (File.Exists(SwitchPath)) mode = (File.ReadAllText(SwitchPath) ?? "").Trim();
                }
                catch (Exception e) { Refl.Warn("iconpath switch: " + e.Message); }

                IconFieldFix.Init(mode);
                IconFieldFix.Install();
                UvFix.Init();
                ContentFallbackFix.Install();
                CollectionFix.Install();
                IconFallbackFix.Init();
                IconFallbackFix.Install();
                TestSpawn.Install();

                Refl.Info("BUILD 2.6.1 iconfallback"
                          + " (uiIcon fallback + CutoutComplexUI material dropped at draw time"
                          + " + miss-only texture extension fallback"
                          + " + collection variant mask filter"
                          + " + def-level icon fallback when the instance texture is unusable"
                          + " + testspawn diagnostic: after a save loads, one copy of each held weapon"
                          + " is dropped at the pawn and both instances are logged)");
            }
            catch (Exception e) { Refl.ErrOnce("bootstrap failed: " + e); }
        }
    }
}
