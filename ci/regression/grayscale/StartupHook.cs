using System;
using System.Collections.Generic;
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

internal static class StartupHook
{
    private const string T = "[GRAYVERIFY]";
    private const string TargetDef = "Gun_Revolver_Unique";

    private static readonly string Mode = (Environment.GetEnvironmentVariable("GRAY_MODE") ?? "patched")
        .Trim().ToLowerInvariant();
    private static readonly string OutDir = Environment.GetEnvironmentVariable("GRAY_OUT")
        ?? Path.Combine(Directory.GetCurrentDirectory(), "grayscale-verify");
    private static readonly string EventsPath = Path.Combine(OutDir, "events.jsonl");
    private static readonly object WriteLock = new object();

    private static int _started, _done, _workerCalls;
    private static bool? _grayAfterMod;
    private static string _workerDef = "";
    private static string _workerTexture = "";
    private static string _workerMaterial = "";

    public static void Initialize()
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(EventsPath, "");
        Event("start", new Dictionary<string, object>
        {
            ["mode"] = Mode
        });

        try
        {
            MethodInfo thingIcon = FindThingIcon();
            MethodInfo worker = AccessTools.Method(typeof(Widgets), "ThingIconWorker");
            if (thingIcon == null) throw new MissingMethodException("ThingIcon(Rect,Thing,...,grayscale)");
            if (worker == null) throw new MissingMethodException("Widgets.ThingIconWorker");

            var harmony = new Harmony("waylith.grayverify");
            harmony.Patch(
                thingIcon,
                prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(StartupHook), nameof(ObserveGrayAfterMod)))
                {
                    priority = HarmonyLib.Priority.Last
                });
            harmony.Patch(
                worker,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(StartupHook), nameof(WorkerPrefix))));

            Event("hooks", new Dictionary<string, object>
            {
                ["ok"] = true,
                ["thingIcon"] = Signature(thingIcon),
                ["worker"] = Signature(worker)
            });
        }
        catch (Exception e)
        {
            Event("hooks", new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = Error(e)
            });
        }

        new Thread(WaitForBoot) { IsBackground = true, Name = "grayverify-waiter" }.Start();
        new Thread(Watchdog) { IsBackground = true, Name = "grayverify-watchdog" }.Start();
    }

    /// <summary>
    /// Priority.Last runs after UvFix's normal-priority prefix. It observes the exact value the
    /// original ThingIcon will receive, without changing it.
    /// </summary>
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    public static void ObserveGrayAfterMod(object[] __args)
    {
        if (__args != null && __args.Length > 6 && __args[6] is bool value)
            _grayAfterMod = value;
    }

    public static void WorkerPrefix(object[] __args)
    {
        Interlocked.Increment(ref _workerCalls);
        ThingDef def = __args != null && __args.Length > 1 ? __args[1] as ThingDef : null;
        Texture texture = __args != null && __args.Length > 2 ? __args[2] as Texture : null;
        Material material = __args != null && __args.Length > 6 ? __args[6] as Material : null;
        _workerDef = def == null ? "" : def.defName;
        _workerTexture = Name(texture);
        _workerMaterial = Name(material);
    }

    private static void WaitForBoot()
    {
        while (Waylith.Runtime.WaylithRuntime.Current == null) Thread.Sleep(20);
        Waylith.Runtime.WaylithRuntime.Current.App.BootCompleted += Run;
    }

    private static void Watchdog()
    {
        Thread.Sleep(300000);
        if (Volatile.Read(ref _done) != 0) return;
        Event("result", new Dictionary<string, object> { ["result"] = "timeout" });
        Environment.Exit(4);
    }

    private static void Run()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        try
        {
            Assembly glyphfix = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "GlyphFix");
            Type bootstrap = glyphfix == null ? null
                : SafeTypes(glyphfix).FirstOrDefault(t => t.FullName == "AgentTouchFix.Bootstrap");
            bool bootstrapInvoked = false;
            string bootstrapError = null;
            if (bootstrap != null)
            {
                try
                {
                    RuntimeHelpers.RunClassConstructor(bootstrap.TypeHandle);
                    bootstrapInvoked = true;
                }
                catch (Exception e) { bootstrapError = Error(e); }
            }

            Event("mod", new Dictionary<string, object>
            {
                ["assembly"] = glyphfix != null,
                ["version"] = glyphfix?.GetName().Version.ToString(),
                ["bootstrap"] = bootstrap != null,
                ["bootstrapInvoked"] = bootstrapInvoked,
                ["bootstrapError"] = bootstrapError
            });

            MethodInfo target = FindThingIcon();
            Patches patches = target == null ? null : Harmony.GetPatchInfo(target);
            string[] owners = patches == null ? Array.Empty<string>()
                : patches.Prefixes.Select(p => p.owner).ToArray();
            Event("harmony", new Dictionary<string, object>
            {
                ["target"] = target == null ? "" : Signature(target),
                ["prefixOwners"] = owners,
                ["uvFixAttached"] = owners.Any(o =>
                    o == "sleepsheep.waylith.agenttouchfix.grayfix")
            });

            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(TargetDef);
            if (def == null) throw new InvalidOperationException("missing def " + TargetDef);
            Thing thing = ThingMaker.MakeThing(def, (ThingDef)null);
            if (thing == null) throw new InvalidOperationException("ThingMaker returned null");

            _workerCalls = 0;
            _workerDef = _workerTexture = _workerMaterial = "";
            _grayAfterMod = null;
            string drawError = null;
            try
            {
                Widgets.ThingIcon(new Rect(0f, 0f, 32f, 32f), thing, grayscale: true);
            }
            catch (Exception e) { drawError = Error(e); }

            Event("grayscale", new Dictionary<string, object>
            {
                ["def"] = def.defName,
                ["grayAfterMod"] = _grayAfterMod,
                ["workerCalls"] = _workerCalls,
                ["workerDef"] = _workerDef,
                ["workerTexture"] = _workerTexture,
                ["workerMaterial"] = _workerMaterial,
                ["drawError"] = drawError
            });
            Event("result", new Dictionary<string, object> { ["result"] = "done" });
            Console.WriteLine($"{T} mode={Mode} grayAfterMod={_grayAfterMod} workerCalls={_workerCalls} " +
                              $"tex='{_workerTexture}' mat='{_workerMaterial}' error='{drawError}'");
            Console.Out.Flush();
            Environment.Exit(0);
        }
        catch (Exception e)
        {
            Event("result", new Dictionary<string, object>
            {
                ["result"] = "phase-exception",
                ["error"] = Error(e),
                ["stack"] = e.StackTrace
            });
            Environment.Exit(5);
        }
    }

    private static MethodInfo FindThingIcon()
    {
        foreach (MethodInfo method in typeof(Widgets).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name != "ThingIcon") continue;
            ParameterInfo[] ps = method.GetParameters();
            if (ps.Length != 7 || ps[0].ParameterType.Name != "Rect" ||
                ps[1].ParameterType != typeof(Thing) || ps[6].ParameterType != typeof(bool)) continue;
            return method;
        }
        return null;
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        catch { return Array.Empty<Type>(); }
    }

    private static string Signature(MethodInfo method)
    {
        return method.DeclaringType?.FullName + "." + method.Name + "(" +
               string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name)) + ")";
    }

    private static string Name(object value)
    {
        if (value == null) return "";
        try
        {
            UnityEngine.Object unityObject = value as UnityEngine.Object;
            if (unityObject != null) return unityObject.name ?? "";
            PropertyInfo property = value.GetType().GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
            return property == null ? value.ToString() : (property.GetValue(value) as string) ?? "";
        }
        catch { return value.GetType().Name; }
    }

    private static string Error(Exception e)
    {
        string value = e.GetType().Name + ": " + e.Message;
        value = value.Replace("\r", " ").Replace("\n", " ");
        return value.Length > 500 ? value.Substring(0, 500) : value;
    }

    private static void Event(string type, Dictionary<string, object> data)
    {
        try
        {
            data["type"] = type;
            lock (WriteLock)
                File.AppendAllText(EventsPath, JsonSerializer.Serialize(data) + Environment.NewLine);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{T} event-write-failed {Error(e)}");
        }
    }
}
