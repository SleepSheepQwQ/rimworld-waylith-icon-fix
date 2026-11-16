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

                // Diagnostics / candidate fix for world textures (held weapons):
                //   agenttouchfix.weapdiag = on  -> log one line per weapon def
                //   agenttouchfix.texkeep  = on  -> suppress Texture2D.Evict() entirely
                if (WorldTexDiag.Diag) WorldTexDiag.InstallWeaponProbe();
                if (WorldTexDiag.Keep) WorldTexDiag.Install();

                Refl.Info("BUILD v41-slim (2.4.0) - build-menu icon repair"
                          + " (uiIcon fallback + CutoutComplexUI material dropped at draw time)");
            }
            catch (Exception e) { Refl.ErrOnce("bootstrap failed: " + e); }
        }
    }
}
