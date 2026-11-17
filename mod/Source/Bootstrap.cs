using System;
using System.IO;
using Verse;

namespace AgentTouchFix
{
    /// <summary>
    /// Entry point. RimWorld runs static constructors of types carrying this attribute, which is early
    /// enough for Harmony patches on the UI draw path.
    ///
    /// The very first thing this does is open the probe log and write the build marker, so that a run
    /// in which the mod was loaded at all is always distinguishable from a run in which it was not.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        private const string SwitchPath =
            "/data/data/com.ports1de.waylith/files/game/agenttouchfix.iconpath";

        static Bootstrap()
        {
            // Line 1 of the probe log: "BUILD <version>-<build stamp> log=<absolute path> ..."
            try { DrawProbe.Start(); } catch { }

            try
            {
                DrawProbe.NoteBoth("INIT-PRE bootstrap entered (harmony " + HarmonyVersion() + ")");
            }
            catch { DrawProbe.Note("INIT-PRE bootstrap entered (harmony version unavailable)"); }

            string mode = "";
            try
            {
                if (File.Exists(SwitchPath)) mode = (File.ReadAllText(SwitchPath) ?? "").Trim();
            }
            catch (Exception e)
            {
                DrawProbe.Note("INIT-PRE iconpath switch unreadable: " + e.GetType().Name + ": " + e.Message);
            }

            try
            {
                IconFieldFix.Init(mode);
                IconFieldFix.Install();
            }
            catch (Exception e)
            {
                DrawProbe.Note("INIT-PRE iconfix threw: " + e.ToString());
            }

            try { DrawProbe.Init(); }
            catch (Exception e) { DrawProbe.Note("INIT-POST probe init threw: " + e.ToString()); }

            try
            {
                DrawProbe.NoteBoth("INIT-POST DrawProbe.Init() returned, probe log = " + DrawProbe.LogPath);
            }
            catch (Exception e)
            {
                try { DrawProbe.Note("INIT-POST note failed: " + e.GetType().Name); } catch { }
            }
        }

        private static string HarmonyVersion()
        {
            try { return typeof(HarmonyLib.Harmony).Assembly.GetName().Version.ToString(); }
            catch { return "?"; }
        }
    }
}
