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
                SamplerFix.Install();

                Refl.Info("BUILD 2.6.0 samplermipfix"
                          + " (uiIcon fallback + CutoutComplexUI material dropped at draw time"
                          + " + miss-only texture extension fallback"
                          + " + collection variant mask filter"
                          + " + non-UI textures forced to the mip-0 sampler)");
            }
            catch (Exception e) { Refl.ErrOnce("bootstrap failed: " + e); }
        }
    }
}
