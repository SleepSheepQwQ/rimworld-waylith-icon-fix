using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// Forces the ImGui texture sampler to mip level 0 for every texture the renderer would otherwise
    /// hand the mipped sampler to.
    ///
    /// Why: <c>ImGuiVulkanRenderer.SamplerFor(string sourcePath)</c> is
    /// <c>ImportDefaults.IsOfficialUiTexture(path) ? _texSampler : _texSamplerMipped</c>, and the two
    /// samplers differ only in <c>MaxLod</c> - <c>_texSampler</c> is built with 0, <c>_texSamplerMipped</c>
    /// with 1000 (<c>CreateSampler(float maxLod)</c>, same linear/linear/linear-mipmap filters). Every
    /// texture that is not an official <c>/Textures/UI/</c> file therefore samples across its whole mip
    /// chain, and mod icons - which ship as a single level, or as levels this port fills only partially -
    /// come back as noise at any minification. A UI icon is drawn at roughly 1:1 pixels, so it wants mip 0
    /// and nothing else.
    ///
    /// Two properties are deliberate:
    ///  - <b>fail-open</b>: every reflection step is checked, and anything missing leaves the shipped
    ///    renderer exactly as it is, with one Warn line. This mod is a rendering tweak, not a load
    ///    requirement, and a renamed field in a port update must not take the game down.
    ///  - <b>one log line</b>: no per-texture logging. A single summary is emitted once, after the first
    ///    few seconds of boot traffic, and says how the redirect works plus how many textures it hit.
    /// </summary>
    internal static class SamplerFix
    {
        private const string RendererTypeName = "RimWorldWaylith.Imgui.ImGuiVulkanRenderer";
        private const string SamplerForName = "SamplerFor";
        private const string NonMippedFieldName = "_texSampler";
        private const string MippedFieldName = "_texSamplerMipped";

        private const string SwitchPath =
            "/data/data/com.ports1de.waylith/files/game/agenttouchfix.mipsampler";

        /// <summary>Boot traffic settles well inside this window; after it, one line is enough.</summary>
        private const int SummaryDelayMs = 4000;

        /// <summary>Bound on the distinct-path set, so a pathological run cannot grow it forever.</summary>
        private const int MaxTrackedPaths = 4096;

        private static FieldInfo _nonMippedField;
        private static FieldInfo _mippedField;
        private static Type _samplerType;
        private static MethodInfo _pick;
        private static int _sites;
        private static long _armedAt;
        private static int _summaryLogged;
        private static int _redirects;
        private static int _uiPathsOnMippedBranch;
        private static int _pickFaults;

        private static readonly HashSet<string> _seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConditionalWeakTable<object, object> _perRenderer =
            new ConditionalWeakTable<object, object>();

        internal static void Install()
        {
            try
            {
                if (SwitchOff())
                {
                    Refl.Info("sampler: agenttouchfix.mipsampler says stock, mipped sampler left as shipped");
                    return;
                }

                Type renderer = Refl.T(RendererTypeName);
                if (renderer == null)
                {
                    Refl.Warn("sampler: " + RendererTypeName + " not loaded, sampler left as shipped");
                    return;
                }

                MethodInfo target = Refl.M(renderer, SamplerForName, 1);
                if (target == null || target.IsStatic)
                {
                    Refl.Warn("sampler: no instance " + SamplerForName + "(string) on " + RendererTypeName
                              + ", sampler left as shipped");
                    return;
                }
                if (target.ReturnType.Name != "VkSampler"
                    || target.GetParameters()[0].ParameterType != typeof(string))
                {
                    Refl.Warn("sampler: " + SamplerForName + " no longer looks like SamplerFor(string):VkSampler,"
                              + " sampler left as shipped");
                    return;
                }

                _nonMippedField = Field(renderer, NonMippedFieldName);
                _mippedField = Field(renderer, MippedFieldName);
                if (_nonMippedField == null || _mippedField == null)
                {
                    Refl.Warn("sampler: " + NonMippedFieldName + "/" + MippedFieldName
                              + " not reachable by reflection, sampler left as shipped");
                    return;
                }
                if (_nonMippedField.FieldType != _mippedField.FieldType)
                {
                    Refl.Warn("sampler: the two sampler fields have different types ("
                              + _nonMippedField.FieldType.Name + " vs " + _mippedField.FieldType.Name
                              + "), sampler left as shipped");
                    return;
                }

                _samplerType = _nonMippedField.FieldType;
                _pick = typeof(SamplerFix).GetMethod("Pick",
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo transpiler = typeof(SamplerFix).GetMethod("Transpile",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (_pick == null || transpiler == null)
                {
                    Refl.Warn("sampler: patch methods missing from the assembly, sampler left as shipped");
                    return;
                }

                Hardened(renderer, target, transpiler);
            }
            catch (Exception e)
            {
                // Fail-open: a throwing install must not cost the other repairs, let alone the load.
                Refl.Warn("sampler: install failed, fail-open: " + e.Message);
            }
        }

        private static void Hardened(Type renderer, MethodInfo target, MethodInfo transpiler)
        {
            var harmony = new Harmony("sleepsheep.waylith.glyphfix.mipsampler");
            harmony.Patch(target, transpiler: new HarmonyMethod(transpiler));

            if (_sites <= 0)
            {
                // The method exists but no longer reads _texSamplerMipped: an already-correct or already
                // changed port. Leave no detour behind.
                try { harmony.Unpatch(target, HarmonyPatchType.Transpiler, harmony.Id); } catch { }
                Refl.Warn("sampler: SamplerFor carries no " + MippedFieldName
                          + " load, nothing to redirect (unpatched)");
                return;
            }

            _armedAt = Environment.TickCount64;
        }

        /// <summary>
        /// Replaces <c>ldfld _texSamplerMipped</c> with a call into <see cref="Pick"/>. The site count is
        /// stored rather than logged: the summary line reports it once, at startup.
        /// </summary>
        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions,
                                                              MethodBase original)
        {
            var output = new List<CodeInstruction>();
            _sites = SamplerIl.Rewrite(output, instructions, _nonMippedField?.DeclaringType,
                                       MippedFieldName, _samplerType, original, _pick);
            return output;
        }

        /// <summary>
        /// Hands the IL the non-mipped sampler for this renderer instance. Runs only on the branch that
        /// used to read <c>_texSamplerMipped</c>, i.e. once per texture that gets a new descriptor set,
        /// not per frame.
        /// </summary>
        private static object Pick(object renderer, string sourcePath)
        {
            try
            {
                object sampler = NonMippedSampler(renderer);
                if (sampler != null)
                {
                    NoteRedirect(sourcePath);
                    MaybeLogSummary();
                    return sampler;
                }
            }
            catch (Exception e)
            {
                if (Interlocked.Increment(ref _pickFaults) == 1)
                    Refl.Warn("sampler: read failed, keeping the stock mipped sampler for this texture: "
                              + e.Message);
            }

            // Never hand null back to unbox.any: the stock value is always a better answer than a crash.
            try { return _mippedField.GetValue(renderer); }
            catch { return null; }
        }

        private static object NonMippedSampler(object renderer)
        {
            if (renderer == null) return null;

            object cached;
            if (_perRenderer.TryGetValue(renderer, out cached)) return cached;

            object value = _nonMippedField.GetValue(renderer);
            if (value == null) return null;
            _perRenderer.Add(renderer, value);
            return value;
        }

        private static void NoteRedirect(string sourcePath)
        {
            Interlocked.Increment(ref _redirects);

            // A path that names a UI folder but still reached the mipped branch is a mod-provided UI
            // texture: it fails ImportDefaults' official-root test. Worth counting, because it is exactly
            // the case a path-based fix would have missed.
            if (SamplerChoice.IsUiPath(sourcePath)) Interlocked.Increment(ref _uiPathsOnMippedBranch);

            HashSet<string> seen = _seen;
            if (seen.Count >= MaxTrackedPaths) return;
            lock (seen)
            {
                if (seen.Count < MaxTrackedPaths) seen.Add(sourcePath ?? "<null>");
            }
        }

        /// <summary>
        /// The one and only success log line: emitted once, after boot traffic has had a few seconds to
        /// bind textures, then never again. Says what changed and how many textures it hit.
        /// </summary>
        private static void MaybeLogSummary()
        {
            if (Volatile.Read(ref _summaryLogged) != 0) return;
            if (Environment.TickCount64 - _armedAt < SummaryDelayMs) return;
            if (Interlocked.Exchange(ref _summaryLogged, 1) != 0) return;

            try
            {
                Refl.Info("sampler: SamplerFor now returns " + NonMippedFieldName
                          + " (MaxLod 0) for every texture; the official-UI branch already did, so only the"
                          + " mipped branch changes. IL sites=" + _sites
                          + " textures=" + Volatile.Read(ref _redirects)
                          + " distinct=" + _seen.Count
                          + " ui_path_on_mipped_branch=" + Volatile.Read(ref _uiPathsOnMippedBranch)
                          + " (one-shot startup summary, no per-texture lines)");
            }
            catch { }
        }

        /// <summary><c>agenttouchfix.mipsampler</c> containing "stock"/"off"/"0"/"false" disables the patch.</summary>
        private static bool SwitchOff()
        {
            try
            {
                if (!File.Exists(SwitchPath)) return false;
                string value = (File.ReadAllText(SwitchPath) ?? "").Trim().ToLowerInvariant();
                return value == "stock" || value == "off" || value == "0" || value == "false";
            }
            catch { return false; }
        }

        /// <summary>Instance field by name, walking up the hierarchy.</summary>
        private static FieldInfo Field(Type type, string name)
        {
            while (type != null)
            {
                FieldInfo field = type.GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null) return field;
                type = type.BaseType;
            }
            return null;
        }
    }
}
