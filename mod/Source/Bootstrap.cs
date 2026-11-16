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
        static Bootstrap()
        {
            try
            {
                Refl.Info("AgentTouchFix " + typeof(Bootstrap).Assembly.GetName().Version
                          + " bootstrapping (harmony " + typeof(HarmonyLib.Harmony).Assembly.GetName().Version + ")");

                IconFieldFix.Init("");
                IconFieldFix.Install();

                TexVerify.Init();

                Refl.Info("BUILD v2.6.0 - icon repair + verified eviction (no configuration)");
            }
            catch (Exception e) { Refl.ErrOnce("bootstrap failed: " + e); }
        }
    }
}
