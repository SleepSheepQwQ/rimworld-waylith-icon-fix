using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// Makes one broken icon cost one icon instead of the whole table.
    ///
    /// Verse.Widgets.ThingIcon(Rect, Thing) writes the global GUI.color, draws, then restores it:
    ///
    ///     GUI.color = color;
    ///     ThingIconWorker(...);      // anything thrown here skips the restore
    ///     GUI.color = Color.white;
    ///
    /// RimWorld's PawnTable (the Animals / Wildlife tabs) calls DoCell with no try/catch, so an
    /// exception here also skips the scroll view's EndScrollView, and the rest of the frame draws with a
    /// tinted GUI.color. Camera+ solves the same class of problem with a Harmony finalizer: restore the
    /// state, then decide what to do with the exception.
    ///
    /// This prefix/finalizer pair does exactly that for both ThingIcon overloads:
    ///   * the saved GUI.color is always restored (even when the draw throws),
    ///   * a NullReferenceException is swallowed and logged once, because in this port the draw path can
    ///     throw for a single broken material (observed: Widgets.DrawTextureFitted with no GUI context),
    ///   * every other exception is rethrown untouched, so real defects are not hidden.
    /// </summary>
    internal static class DrawGuardFix
    {
        private static PropertyInfo _guiColor;      // UnityEngine.GUI.color (static)
        private static Type _colorType;
        private static int _swallowed, _warned;

        internal static void Install()
        {
            try
            {
                _guiColor = Refl.T("UnityEngine.GUI")?.GetProperty("color", BindingFlags.Public | BindingFlags.Static);
                _colorType = Refl.T("UnityEngine.Color");
                if (_guiColor == null || _colorType == null)
                {
                    Refl.Warn("drawguard: GUI.color not reachable, state restore skipped");
                    return;
                }

                MethodInfo prefix = typeof(DrawGuardFix).GetMethod(nameof(Save), BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo finalizer = typeof(DrawGuardFix).GetMethod(nameof(Restore), BindingFlags.Static | BindingFlags.NonPublic);

                int patched = 0;
                foreach (MethodInfo m in Refl.T("Verse.Widgets").GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "ThingIcon") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length < 2) continue;
                    if (ps[0].ParameterType.Name != "Rect") continue;
                    if (ps[1].ParameterType.Name != "Thing" && ps[1].ParameterType.Name != "ThingDef") continue;

                    new Harmony("sleepsheep.waylith.agenttouchfix.drawguard." + patched)
                        .Patch(m,
                               prefix: new HarmonyMethod(prefix) { priority = Priority.First },
                               finalizer: new HarmonyMethod(finalizer) { priority = Priority.Last });
                    patched++;
                }

                if (patched == 0) Refl.Warn("drawguard: no ThingIcon overload found");
                else Refl.Info("drawguard: GUI.color restore + NRE guard on " + patched + " ThingIcon overload(s)");
            }
            catch (Exception e)
            {
                Refl.Warn("drawguard: install failed: " + e.Message);
            }
        }

        /// <summary>Saves the current GUI.color so the finalizer can put it back.</summary>
        private static void Save(ref object __state)
        {
            try { __state = _guiColor.GetValue(null, null); }
            catch { __state = null; }
        }

        /// <summary>
        /// Restores GUI.color always; swallows only NullReferenceException, and only once per process in
        /// the log, so a broken icon degrades to "nothing drawn" instead of a tinted, half-drawn frame.
        /// </summary>
        private static Exception Restore(object __state, Exception __exception)
        {
            if (__state != null)
            {
                try { _guiColor.SetValue(null, __state, null); } catch { }
            }

            if (__exception is NullReferenceException)
            {
                int n = Interlocked.Increment(ref _swallowed);
                if (Interlocked.Exchange(ref _warned, 1) == 0)
                    Refl.Warn("drawguard: swallowed a NullReferenceException in ThingIcon (" + n + " so far)");
                return null;
            }

            return __exception;
        }
    }
}
