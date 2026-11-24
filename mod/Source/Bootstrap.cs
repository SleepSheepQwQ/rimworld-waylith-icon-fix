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
                DdsFormatFix.Install();
                DdsPrefixAlt.Install();
                DdsPrefixAlt.SweepLoadedContent();
                AtlasBakeFix.Init();
                AtlasBakeFix.Install();
                ShadowFix.Install();
                ShadowFix.Repair();
                CollectionFix.Install();
                CollectionFix.RepairLoaded();
                IconFallbackFix.Init();
                IconFallbackFix.Install();
                DrawGuardFix.Install();

                PatchReport.Log(Refl.T("Verse.Widgets"), "ThingIcon");
                PatchReport.Log(Refl.T("Verse.Widgets"), "GetIconFor");
                PatchReport.Log(Refl.T("Verse.Graphic_Collection"), "Init");
                TestSpawn.Install();

                Refl.Info("BUILD " + typeof(Bootstrap).Assembly.GetName().Version
                          + " iconfallback+ddsformat+casefix+shadowfix+atlasbake+drawguard"
                          + " (uiIcon fallback + CutoutComplexUI material dropped at draw time"
                          + " + miss-only texture extension fallback"
                          + " + collection variant mask filter"
                          + " + def-level icon fallback when the instance texture is unusable"
                          + " + testspawn diagnostic: read-only, after a save loads it logs one"
                          + " `test: thing` line per player pawn (and its primary weapon), creating"
                          + " nothing"
                          + " + member lookup by shape: field-then-property, static-aware, both"
                          + " GetIconFor material overloads, static GenGrid.Standable"
                          + " + dds: DXGI word honoured (BC4/BC5/BC1/BC3 no longer BC7), DXT3/DXT2/DXT4"
                          + " decoded to rgba32, BC4/BC5 native when readable, anyReloaded-gated case index");
            }
            catch (Exception e) { Refl.ErrOnce("bootstrap failed: " + e); }
        }
    }
}
