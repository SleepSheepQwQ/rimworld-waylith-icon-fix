using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

/// <summary>
/// Real-host gate for the Architect/construction menu icon path.
///
/// It runs inside the shipped Waylith process after PlayDataLoader completes, invokes GlyphFix's
/// StaticConstructorOnStartup exactly as RimWorld does, and measures both halves of the UI path:
///
///   Widgets.GetIconFor(ThingDef, out Material)
///   Widgets.ThingIcon(Rect, ThingDef) -> Widgets.ThingIconWorker(..., texture, material)
///
/// RoyalBed and NutrientPasteDispenser are the two device-confirmed CutoutComplexUI regressions.
/// PowerConduit is a buildable non-CutoutComplexUI control, so a gate cannot pass by clearing every
/// material or by returning a texture for every def regardless of its real state.
/// </summary>
internal static class StartupHook
{
    private const string T = "[BUILDICON]";
    private static readonly string[] ComplexDefs = { "RoyalBed", "NutrientPasteDispenser" };
    private const string ControlDef = "PowerConduit";

    private static readonly string Mode = (Environment.GetEnvironmentVariable("BUILDICON_MODE") ?? "patched")
        .Trim().ToLowerInvariant();
    private static readonly string OutDir = Environment.GetEnvironmentVariable("BUILDICON_OUT")
        ?? Path.Combine(Directory.GetCurrentDirectory(), "ci-buildingicon-out");
    private static readonly string EventsPath = Path.Combine(OutDir, "events.jsonl");
    private static readonly object WriteLock = new object();
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    private static int _started;
    private static int _done;
    private static int _workerCalls;
    private static string _workerDef = "-";
    private static string _workerTex = "-";
    private static string _workerMat = "-";
    private static string _workerPatched = "false";

    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(OutDir);
            File.WriteAllText(EventsPath, "");
            Event("probe", new Dictionary<string, object>
            {
                ["kind"] = "start",
                ["mode"] = Mode,
                ["out"] = OutDir,
                ["t"] = Sec()
            });
            if (Mode != "control" && Mode != "patched")
            {
                Event("probe", new Dictionary<string, object>
                {
                    ["kind"] = "invalid-mode",
                    ["mode"] = Mode,
                    ["expected"] = "control|patched"
                });
                Finish("invalid-mode");
                Environment.Exit(2);
                return;
            }
            PatchWorker();
        }
        catch (Exception e)
        {
            Event("probe", new Dictionary<string, object>
            {
                ["kind"] = "initialize-exception",
                ["error"] = Error(e)
            });
        }

        new Thread(WaitForBoot) { IsBackground = true, Name = "buildingicon-waiter" }.Start();
        new Thread(Watchdog) { IsBackground = true, Name = "buildingicon-watchdog" }.Start();
    }

    private static void PatchWorker()
    {
        MethodInfo worker = AccessTools.Method(typeof(Widgets), "ThingIconWorker");
        if (worker == null)
        {
            _workerPatched = "missing-method";
            Event("worker_patch", new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = "Widgets.ThingIconWorker not found"
            });
            return;
        }

        try
        {
            new Harmony("waylith.buildingicon.worker").Patch(
                worker,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(StartupHook), nameof(WorkerPrefixArgs))));
            _workerPatched = "true";
            Event("worker_patch", new Dictionary<string, object>
            {
                ["ok"] = true,
                ["signature"] = Signature(worker),
                ["params"] = worker.GetParameters().Length
            });
        }
        catch (Exception e)
        {
            _workerPatched = "failed";
            Event("worker_patch", new Dictionary<string, object>
            {
                ["ok"] = false,
                ["signature"] = Signature(worker),
                ["error"] = Error(e)
            });
        }
    }

    /// <summary>
    /// The shipped signature is:
    /// ThingIconWorker(Rect, ThingDef, Texture, float, float, Rot4?, Material, float).
    /// Positional binding avoids betting the gate on reflection parameter names.
    /// </summary>
    public static void WorkerPrefixArgs(object[] __args)
    {
        try
        {
            Interlocked.Increment(ref _workerCalls);
            ThingDef def = __args != null && __args.Length > 1 ? __args[1] as ThingDef : null;
            Texture tex = __args != null && __args.Length > 2 ? __args[2] as Texture : null;
            Material mat = __args != null && __args.Length > 6 ? __args[6] as Material : null;
            _workerDef = def == null ? "<null>" : def.defName;
            _workerTex = Name(tex);
            _workerMat = Name(mat);
        }
        catch { }
    }

    private static void Watchdog()
    {
        int ms = int.TryParse(Environment.GetEnvironmentVariable("PROBE_TIMEOUT_MS"), out int v) ? v : 300000;
        Thread.Sleep(ms);
        if (Volatile.Read(ref _done) != 0) return;
        Event("probe", new Dictionary<string, object>
        {
            ["kind"] = "watchdog",
            ["t"] = Sec()
        });
        Finish("watchdog-timeout");
        Environment.Exit(4);
    }

    private static void WaitForBoot()
    {
        while (Waylith.Runtime.WaylithRuntime.Current == null) Thread.Sleep(20);
        Waylith.Runtime.WaylithRuntime.Current.App.BootCompleted += Run;
        Console.Error.WriteLine($"{T} armed");
        Console.Error.Flush();
    }

    private static void Run()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        try { Phase(); }
        catch (Exception e)
        {
            Event("probe", new Dictionary<string, object>
            {
                ["kind"] = "phase-exception",
                ["error"] = Error(e),
                ["stack"] = e.StackTrace
            });
            Finish("phase-exception");
        }
        finally { Quit(); }
    }

    private static void Phase()
    {
        List<ThingDef> allDefs = DefDatabase<ThingDef>.AllDefsListForReading;
        Event("probe", new Dictionary<string, object>
        {
            ["kind"] = "booted",
            ["thingDefs"] = allDefs.Count,
            ["t"] = Sec()
        });

        try
        {
            Event("mods", new Dictionary<string, object>
            {
                ["items"] = ModsConfig.ActiveModsInLoadOrder
                    .Select(m => $"{m.PackageId}@{m.ModVersion}")
                    .ToArray()
            });
        }
        catch (Exception e)
        {
            Event("mods", new Dictionary<string, object>
            {
                ["error"] = Error(e)
            });
        }

        Assembly glyphFix = FindGlyphFix();
        Event("assembly", new Dictionary<string, object>
        {
            ["present"] = glyphFix != null,
            ["name"] = glyphFix == null ? null : glyphFix.GetName().Name,
            ["version"] = glyphFix == null ? null : glyphFix.GetName().Version.ToString(),
            ["location"] = glyphFix == null ? null : glyphFix.Location
        });

        bool startupInvoked = false;
        string startupError = null;
        if (glyphFix != null)
        {
            Type bootstrap = SafeTypes(glyphFix).FirstOrDefault(t => t.FullName == "AgentTouchFix.Bootstrap");
            if (bootstrap == null)
            {
                startupError = "AgentTouchFix.Bootstrap not found";
            }
            else
            {
                try
                {
                    RuntimeHelpers.RunClassConstructor(bootstrap.TypeHandle);
                    startupInvoked = true;
                }
                catch (Exception e) { startupError = Error(e); }
            }
        }

        Event("startup", new Dictionary<string, object>
        {
            ["assembly"] = glyphFix != null,
            ["invoked"] = startupInvoked,
            ["error"] = startupError
        });

        ReportHarmony();

        foreach (string defName in ComplexDefs)
            Probe(defName, expectComplex: true);
        Probe(ControlDef, expectComplex: false);

        Finish("done");
    }

    private static void Probe(string defName, bool expectComplex)
    {
        ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
        if (def == null)
        {
            Event("def", new Dictionary<string, object>
            {
                ["def"] = defName,
                ["found"] = false,
                ["expectComplex"] = expectComplex
            });
            return;
        }

        var rec = new Dictionary<string, object>
        {
            ["def"] = def.defName,
            ["found"] = true,
            ["expectComplex"] = expectComplex,
            ["category"] = def.category.ToString(),
            ["graphicData"] = def.graphicData == null ? null : def.graphicData.texPath,
            ["graphicClass"] = def.graphicData == null || def.graphicData.graphicClass == null
                ? null : def.graphicData.graphicClass.Name,
            ["uiIconPath"] = def.uiIconPath
        };

        Texture2D originalIcon = def.uiIcon;
        Material originalMat = def.uiIconMaterial;
        Material testMat = originalMat;
        CaptureIcon(def, "before", rec);
        CaptureMaterial(def, "before", rec);

        if (!expectComplex)
        {
            // PowerConduit normally has no UI material. Put a real, non-UI material on the def so the
            // test proves GlyphFix preserves it, not merely that "null stayed null".
            try
            {
                Material graphicMat = def.graphic == null
                    ? null : def.graphic.MatAt(def.defaultPlacingRot);
                if (graphicMat != null)
                {
                    testMat = graphicMat;
                    def.uiIconMaterial = testMat;
                    rec["controlMaterialInjected"] = true;
                    CaptureMaterial(def, "injected", rec);
                }
            }
            catch (Exception e) { rec["controlMaterialInjectionError"] = Error(e); }
        }

        Texture2D directTex = null;
        Material directMat = null;
        string directError = null;
        try
        {
            directTex = Widgets.GetIconFor(def, out directMat);
        }
        catch (Exception e) { directError = Error(e); }

        rec["getIconFor"] = new Dictionary<string, object>
        {
            ["tex"] = Name(directTex),
            ["badTex"] = ReferenceEquals(directTex, BaseContent.BadTex),
            ["size"] = Size(directTex),
            ["material"] = Name(directMat),
            ["complexUi"] = IsComplexUi(directMat),
            ["error"] = directError
        };

        CaptureIcon(def, "afterDirect", rec);
        CaptureMaterial(def, "afterDirect", rec);

        // The first GetIconFor repairs the def and places it in IconFieldFix's done cache. Restore the
        // exact pre-call state and clear that cache, otherwise the following ThingIcon is not a
        // first-call draw and can hide the Architect-menu regression.
        def.uiIcon = originalIcon;
        def.uiIconMaterial = testMat;
        ResetGlyphFixCache();

        _workerCalls = 0;
        _workerDef = _workerTex = _workerMat = "-";
        string drawError = null;
        try
        {
            Widgets.ThingIcon(new Rect(0f, 0f, 64f, 64f), def);
        }
        catch (Exception e) { drawError = Error(e); }

        rec["worker"] = new Dictionary<string, object>
        {
            ["calls"] = _workerCalls,
            ["def"] = _workerDef,
            ["tex"] = _workerTex,
            ["material"] = _workerMat,
            ["complexUi"] = _workerMat.StartsWith("CutoutComplexUI", StringComparison.Ordinal),
            ["error"] = drawError
        };
        CaptureIcon(def, "afterThingIcon", rec);
        CaptureMaterial(def, "afterThingIcon", rec);

        Event("def", rec);
        Console.WriteLine($"{T} def={defName} expectComplex={expectComplex} " +
                          $"uiBefore={Dot(rec, "uiIcon.before.tex")} uiAfterDraw={Dot(rec, "uiIcon.afterThingIcon.tex")} " +
                          $"matBefore={Dot(rec, "material.before.name")} matAfterDraw={Dot(rec, "material.afterThingIcon.name")} " +
                          $"getMat={Name(directMat)} workerTex={_workerTex} workerMat={_workerMat} calls={_workerCalls}");
        Console.Out.Flush();
    }

    /// <summary>
    /// Clear only the decision caches that make the second half of the A/B probe a true first call.
    /// The game data (def.uiIcon/uiIconMaterial) is restored separately.
    /// </summary>
    private static void ResetGlyphFixCache()
    {
        Assembly glyphFix = FindGlyphFix();
        Type iconField = glyphFix == null ? null
            : SafeTypes(glyphFix).FirstOrDefault(t => t.FullName == "AgentTouchFix.IconFieldFix");
        if (iconField == null) return;
        foreach (string name in new[] { "_done", "_names", "_nameProp" })
        {
            try
            {
                FieldInfo field = iconField.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
                object value = field == null ? null : field.GetValue(null);
                MethodInfo clear = value == null ? null : value.GetType().GetMethod("Clear");
                clear?.Invoke(value, null);
            }
            catch (Exception e)
            {
                Event("cache_reset", new Dictionary<string, object>
                {
                    ["field"] = name,
                    ["error"] = Error(e)
                });
            }
        }
    }

    private static void CaptureIcon(ThingDef def, string phase, Dictionary<string, object> rec)
    {
        Texture2D tex = def.uiIcon;
        rec["uiIcon." + phase] = new Dictionary<string, object>
        {
            ["tex"] = Name(tex),
            ["null"] = tex == null,
            ["badTex"] = ReferenceEquals(tex, BaseContent.BadTex),
            ["size"] = Size(tex),
            ["readable"] = Readable(tex),
            ["coverage"] = Coverage(tex)
        };
    }

    private static void CaptureMaterial(ThingDef def, string phase, Dictionary<string, object> rec)
    {
        Material mat = def.uiIconMaterial;
        rec["material." + phase] = new Dictionary<string, object>
        {
            ["name"] = Name(mat),
            ["null"] = mat == null,
            ["shader"] = mat == null || mat.shader == null ? null : mat.shader.name,
            ["mainTex"] = mat == null ? null : Name(mat.mainTexture),
            ["complexUi"] = IsComplexUi(mat)
        };
    }

    private static void ReportHarmony()
    {
        var methods = new List<Dictionary<string, object>>();
        try
        {
            foreach (MethodInfo m in typeof(Widgets).GetMethods(BindingFlags.Public | BindingFlags.Static)
                         .Where(m => m.Name == "ThingIcon" || m.Name == "GetIconFor"))
            {
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 0 || (ps[0].ParameterType != typeof(ThingDef) &&
                                       ps[0].ParameterType != typeof(Thing))) continue;
                Patches patches = Harmony.GetPatchInfo(m);
                string[] prefixes = patches == null ? Array.Empty<string>()
                    : patches.Prefixes.Select(p => $"{p.owner}:{p.PatchMethod.Name}").ToArray();
                string[] postfixes = patches == null ? Array.Empty<string>()
                    : patches.Postfixes.Select(p => $"{p.owner}:{p.PatchMethod.Name}").ToArray();
                methods.Add(new Dictionary<string, object>
                {
                    ["method"] = Signature(m),
                    ["first"] = ps[0].ParameterType.Name,
                    ["prefixes"] = prefixes,
                    ["postfixes"] = postfixes,
                    ["glyphfixThingIcon"] = ps[0].ParameterType == typeof(ThingDef) && m.Name == "ThingIcon" &&
                                            prefixes.Any(x => x.Contains("agenttouchfix.drawfix", StringComparison.Ordinal)),
                    ["glyphfixGetIconFor"] = ps[0].ParameterType == typeof(ThingDef) && m.Name == "GetIconFor" &&
                                             HasByRef(ps, typeof(Material)) &&
                                             prefixes.Any(x => x.Contains("agenttouchfix.drawfix.geticonfor", StringComparison.Ordinal))
                });
            }
        }
        catch (Exception e)
        {
            methods.Add(new Dictionary<string, object>
            {
                ["error"] = Error(e)
            });
        }
        Event("harmony", new Dictionary<string, object>
        {
            ["workerPatched"] = _workerPatched,
            ["methods"] = methods
        });
    }

    private static bool HasByRef(ParameterInfo[] ps, Type element)
    {
        foreach (ParameterInfo p in ps)
            if (p.ParameterType.IsByRef && p.ParameterType.GetElementType() == element) return true;
        return false;
    }

    private static Assembly FindGlyphFix()
    {
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (a.GetName().Name == "GlyphFix") return a;
            }
            catch { }
        }
        return null;
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        catch { return Array.Empty<Type>(); }
    }

    private static void Finish(string result)
    {
        Event("probe", new Dictionary<string, object>
        {
            ["kind"] = "result",
            ["result"] = result,
            ["mode"] = Mode,
            ["t"] = Sec()
        });
        Console.WriteLine($"{T} RESULT={(result == "done" ? "DONE" : "FAIL")} mode={Mode} reason={result}");
        Console.Out.Flush();
    }

    private static void Event(string type, Dictionary<string, object> data)
    {
        try
        {
            data["type"] = type;
            string json = JsonSerializer.Serialize(data);
            lock (WriteLock) { File.AppendAllText(EventsPath, json + Environment.NewLine); }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{T} event-write-failed {e.GetType().Name}: {e.Message}");
        }
    }

    private static string Dot(Dictionary<string, object> rec, string key)
    {
        object cur = rec;
        foreach (string part in key.Split('.'))
        {
            if (!(cur is Dictionary<string, object> d) || !d.TryGetValue(part, out cur)) return "-";
        }
        return cur == null ? "<null>" : Convert.ToString(cur, CultureInfo.InvariantCulture);
    }

    private static string Name(object value)
    {
        if (value == null) return "";
        try
        {
            UnityEngine.Object uo = value as UnityEngine.Object;
            if (uo != null) return uo.name ?? "";
            PropertyInfo p = value.GetType().GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
            return p == null ? value.ToString() : (p.GetValue(value) as string) ?? "";
        }
        catch { return value.GetType().Name; }
    }

    private static string Size(Texture2D tex)
    {
        if (tex == null) return "-";
        try { return tex.width + "x" + tex.height; }
        catch { return "?"; }
    }

    private static bool Readable(Texture2D tex)
    {
        if (tex == null) return false;
        try
        {
            Color32[] px = tex.GetPixels32();
            return px != null && px.Length > 0;
        }
        catch { return false; }
    }

    private static double Coverage(Texture2D tex)
    {
        if (tex == null) return 0;
        try
        {
            Color32[] px = tex.GetPixels32();
            if (px == null || px.Length == 0) return 0;
            int opaque = px.Count(p => p.a != 0);
            return Math.Round((double)opaque / px.Length, 6);
        }
        catch { return 0; }
    }

    private static bool IsComplexUi(Material mat)
    {
        string n = Name(mat);
        return n.StartsWith("CutoutComplexUI", StringComparison.Ordinal);
    }

    private static string Signature(MethodInfo m)
    {
        string[] parts = m.GetParameters().Select(p =>
        {
            Type t = p.ParameterType;
            string name = t.IsByRef ? t.GetElementType().Name + "&" : t.Name;
            return name + " " + p.Name;
        }).ToArray();
        return m.Name + "(" + string.Join(", ", parts) + ")";
    }

    private static string Error(Exception e)
    {
        string s = e.GetType().Name + ": " + e.Message;
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 500 ? s.Substring(0, 500) : s;
    }

    private static double Sec() => Math.Round(Clock.Elapsed.TotalSeconds, 3);

    private static void Quit()
    {
        if (Interlocked.Exchange(ref _done, 1) != 0) return;
        try { Root.Shutdown(); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{T} shutdown failed: {e.GetType().Name}: {e.Message}");
        }
    }
}
