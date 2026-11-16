using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace AgentTouchFix
{
    /// <summary>
    /// Read-only instrumentation for "textures do not appear".
    ///
    /// It answers exactly one question per failing texture: which step refused to make it resident?
    ///   resfail:  Texture2D.EnsureResident() returned false  -> we see SourcePath / pixels / cache state
    ///   decodefail: the port's software BC->RGBA path refused the entry
    ///   uploadfail: resident but the upload produced no image
    ///
    /// Nothing is changed: every hook is a postfix that only logs, so the game keeps its own behaviour.
    /// Rate limited (first 40 of each kind) because this port's Warning/Error path can raise dialogs.
    /// </summary>
    internal static class TexProbe
    {
        private const string Log = "texprobe: ";
        private static int _resFail, _decodeFail, _uploadFail, _resident;
        private static readonly System.Collections.Generic.HashSet<string> _seen =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        internal static void Init()
        {
            try
            {
                var h = new HarmonyLib.Harmony("sleepsheep.waylith.agenttouchfix.texprobe");

                // Patch the override, not the virtual base: Harmony patches the method body it is given,
                // and every draw samples a Texture2D.
                Type tex = Refl.T("Waylith.Rendering.Texture2D") ?? Refl.T("Waylith.Rendering.Texture");
                MethodInfo ensure = tex?.GetMethod("EnsureResident",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (ensure != null)
                    h.Patch(ensure, null, new HarmonyMethod(typeof(TexProbe).GetMethod("Ensure_Postfix",
                        BindingFlags.Static | BindingFlags.NonPublic)));
                else Refl.Warn(Log + "Texture.EnsureResident not found");

                Type pack = Refl.T("Waylith.Graphics.Textures.PackTextureSource")
                            ?? Refl.T("Waylith.Rendering.PackTextureSource");
                MethodInfo create = pack?.GetMethod("TryCreate", BindingFlags.Public | BindingFlags.Static);
                if (create != null)
                    h.Patch(create, null, new HarmonyMethod(typeof(TexProbe).GetMethod("Create_Postfix",
                        BindingFlags.Static | BindingFlags.NonPublic)));
                else Refl.Warn(Log + "PackTextureSource.TryCreate not found");

                Refl.Info(Log + "installed (ensure=" + (ensure != null) + " create=" + (create != null) + ")");
            }
            catch (Exception e) { Refl.Warn(Log + "install failed: " + e.Message); }
        }

        // Residency: the single place where "why is this texture not there" becomes visible.
        private static void Ensure_Postfix(object __instance, bool __result)
        {
            try
            {
                if (__result) { Interlocked.Increment(ref _resident); return; }

                object cpu = Prop(__instance, "CpuData");
                string name = Prop(__instance, "Name") as string ?? "?";
                string path = Prop(__instance, "SourcePath") as string ?? "-";
                string key = name + "|" + path;
                lock (_seen) { if (!_seen.Add(key)) return; }

                int n = Interlocked.Increment(ref _resFail);
                if (n <= 40)
                    Refl.Info(Log + "resfail #" + n + " name=" + name + " path=" + path
                              + " gpu=" + (Prop(__instance, "GpuResource") != null)
                              + " cpu=" + (cpu != null) + " hasPixels=" + Truthy(Prop(cpu, "HasData"))
                              + " w=" + Prop(__instance, "width") + " h=" + Prop(__instance, "height"));
                Report();
            }
            catch { }
        }

        // The port's software BC decode (AndroidFixes.PackTextureDecodePrefix) calls this.
        private static void Create_Postfix(object[] __args, bool __result)
        {
            try
            {
                if (__result) return;
                int n = Interlocked.Increment(ref _decodeFail);
                if (n <= 40)
                {
                    object buf = __args != null && __args.Length > 0 ? __args[0] : null;
                    object len = __args != null && __args.Length > 1 ? __args[1] : null;
                    Refl.Info(Log + "decodefail #" + n + " bytes=" + len + " bufLen="
                              + (buf is byte[] b ? b.Length : -1));
                }
                Report();
            }
            catch { }
        }

        private static void Report()
        {
            long now = Environment.TickCount;
            if (now - _lastMs < 30000) return;
            _lastMs = now;
            Refl.Info(Log + "resident=" + _resident + " resfail=" + _resFail
                      + " decodefail=" + _decodeFail + " uploadfail=" + _uploadFail);
        }

        private static long _lastMs;
        private static bool Truthy(object o) { return o is bool && (bool)o; }

        private static object Prop(object o, string name)
        {
            if (o == null) return null;
            try
            {
                for (Type c = o.GetType(); c != null; c = c.BaseType)
                {
                    PropertyInfo p = c.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p != null) return p.GetValue(o, null);
                    FieldInfo f = c.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null) return f.GetValue(o);
                }
            }
            catch { }
            return null;
        }
    }
}
