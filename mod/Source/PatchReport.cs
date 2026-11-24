using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// Prints, for every method we care about, how many of *our* patches Harmony actually holds.
    ///
    /// Three releases in a row shipped a guard that looked installed but never fired (the last one was a
    /// property-only lookup for a field). "armed" in our own log only proves that Install() ran - not
    /// that Harmony attached anything. GetPatchInfo is the authoritative answer, so it is logged once,
    /// right after installation, with the count per kind.
    /// </summary>
    internal static class PatchReport
    {
        private const string Owned = "sleepsheep.waylith";

        internal static void Log(Type type, string methodName)
        {
            if (type == null) { Refl.Warn("patchreport: type missing for " + methodName); return; }
            try
            {
                int methods = 0;
                var lines = new List<string>();
                foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                {
                    if (m.Name != methodName) continue;
                    methods++;
                    Patches info = Harmony.GetPatchInfo(m);
                    if (info == null) { lines.Add(Signature(m) + "=none"); continue; }

                    int pre = Count(info.Prefixes), post = Count(info.Postfixes), fin = Count(info.Finalizers), tr = Count(info.Transpilers);
                    lines.Add(Signature(m) + "=pre" + pre + "/post" + post + "/fin" + fin + "/tr" + tr);
                }

                if (methods == 0) Refl.Warn("patchreport: " + type.Name + "." + methodName + " not found");
                else Refl.Info("patchreport: " + type.Name + "." + methodName + " -> " + string.Join(" | ", lines));
            }
            catch (Exception e)
            {
                Refl.Warn("patchreport: " + methodName + " failed: " + e.Message);
            }
        }

        private static int Count(IEnumerable<Patch> patches)
        {
            int n = 0;
            if (patches == null) return 0;
            foreach (Patch p in patches)
                if (p.owner != null && p.owner.StartsWith(Owned, StringComparison.Ordinal)) n++;
            return n;
        }

        private static string Signature(MethodInfo m)
        {
            ParameterInfo[] ps = m.GetParameters();
            return "(" + ps.Length + "p:" + (ps.Length > 1 ? ps[1].ParameterType.Name : "-") + ")";
        }
    }
}
