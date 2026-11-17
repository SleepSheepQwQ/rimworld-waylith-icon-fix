using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AgentTouchFix
{
    /// <summary>
    /// Held / worn items never appear, while ground items and pawn bodies do.
    ///
    /// Everything drawn through Verse.Graphic goes
    ///     Graphic.DrawWorker -> DrawMeshInt -> Graphics.DrawMesh(mesh, loc, quat, mat, layer)
    /// i.e. through the deferred mesh queue. Pawn bodies instead use
    ///     GenDraw.DrawMeshNowOrLater(mesh, loc, quat, mat, drawNow, properties)
    /// and those render. Carried things and equipment are the only content that needs the deferred
    /// route, and on this port it draws nothing.
    ///
    /// Fix: send Graphic's mesh draws through the same entry point the pawn renderer uses.
    /// Reflection only - the mod compiles without any game assembly.
    /// </summary>
    internal static class DrawPathFix
    {
        private static MethodInfo _drawNowOrLater;
        private static int _rerouted;
        private static bool _warned;

        internal static void Init()
        {
            try
            {
                Type genDraw = Refl.T("Verse.GenDraw");
                if (genDraw != null)
                {
                    foreach (MethodInfo m in genDraw.GetMethods(BindingFlags.Static | BindingFlags.Public))
                    {
                        if (m.Name != "DrawMeshNowOrLater") continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length == 5 && ps[4].ParameterType == typeof(bool)) { _drawNowOrLater = m; break; }
                        if (ps.Length == 6 && ps[4].ParameterType == typeof(bool)) { _drawNowOrLater = m; break; }
                    }
                }
                if (_drawNowOrLater == null) { Refl.Warn("drawpath: GenDraw.DrawMeshNowOrLater not found"); return; }

                Type graphic = Refl.T("Verse.Graphic");
                MethodInfo target = graphic?.GetMethod("DrawMeshInt",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                MethodInfo prefix = typeof(DrawPathFix).GetMethod("Prefix",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (target == null || prefix == null) { Refl.Warn("drawpath: Graphic.DrawMeshInt not found"); return; }

                new HarmonyLib.Harmony("sleepsheep.waylith.agenttouchfix.drawpath")
                    .Patch(target, new HarmonyMethod(prefix));
                Refl.Info("drawpath: Graphic.DrawMeshInt -> " + _drawNowOrLater + " (" + _drawNowOrLater.GetParameters().Length + " args)");
            }
            catch (Exception e) { Refl.Warn("drawpath: install failed: " + e.Message); }
        }

        private static bool Prefix(object mesh, object loc, object quat, object mat)
        {
            try
            {
                if (mesh == null || mat == null || _drawNowOrLater == null) return true;

                ParameterInfo[] ps = _drawNowOrLater.GetParameters();
                // drawNow MUST be true: the false branch of DrawMeshNowOrLater calls Graphics.DrawMesh
                // again, i.e. the same deferred queue that renders nothing here (confirmed by IL scan).
                object[] args = ps.Length == 6
                    ? new object[] { mesh, loc, quat, mat, true, null }
                    : new object[] { mesh, loc, quat, mat, true };
                _drawNowOrLater.Invoke(null, args);
                _rerouted++;
                return false;
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Refl.Warn("drawpath: reroute failed, falling back: " + e.Message); }
                return true;
            }
        }
    }
}
