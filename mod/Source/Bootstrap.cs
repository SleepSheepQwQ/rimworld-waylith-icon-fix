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
                CaseFix.Install();
                ShadowFix.Install();
                CollectionFix.Install();
                IconFallbackFix.Init();
                IconFallbackFix.Install();
                TestSpawn.Install();

                Refl.Info("BUILD 2.6.6 shadow"
                          + " (uiIcon fallback + CutoutComplexUI material dropped at draw time"
                          + " + miss-only texture extension fallback"
                          + " + collection variant mask filter"
                          + " + def-level icon fallback when the instance texture is unusable"
                          + " + testspawn diagnostic: read-only, after a save loads it logs one"
                          + " `test: thing` line per player pawn (and its primary weapon), creating"
                          + " nothing"
                          + " + member lookup by shape: field-then-property, static-aware, both"
                          + " GetIconFor material overloads, static GenGrid.Standable"
                          + " + anyReloaded-gated case-insensitive texture fallback"
                          + " + shadowfix: a .dds that cannot be decoded no longer deletes the same-named"
                          + " png/jpg/psd (148-byte header read, per-path mtime+length cache)");
            }
            catch (Exception e) { Refl.ErrOnce("bootstrap failed: " + e); }
        }
    }
}
