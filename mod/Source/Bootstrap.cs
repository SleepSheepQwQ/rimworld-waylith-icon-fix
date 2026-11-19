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

                Refl.Info("BUILD 2.6.2 iconfallback"
                          + " (uiIcon fallback + CutoutComplexUI material dropped at draw time"
                          + " + miss-only texture extension fallback"
                          + " + collection variant mask filter"
                          + " + def-level icon fallback when the instance texture is unusable"
                          + " + testspawn diagnostic: after a save loads, one copy of each held weapon"
                          + " is dropped at the pawn and both instances are logged"
                          + " + member lookup by shape: field-then-property, static-aware, both"
                          + " GetIconFor material overloads, static GenGrid.Standable"
                          + " + fixture sample: one unique-weapon def spawned on every load"
                          + " (by defName, else the first Graphic_Random def that has masks)");
            }
            catch (Exception e) { Refl.ErrOnce("bootstrap failed: " + e); }
        }
    }
}
