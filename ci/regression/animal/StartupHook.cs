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
    private const string T = "[ANIMALVERIFY]";
    private static readonly string[] Species = { "Ibex", "Boomalope", "Squirrel", "Cow" };
    private static readonly string OutDir = Environment.GetEnvironmentVariable("ANIMAL_OUT")
        ?? Path.Combine(Directory.GetCurrentDirectory(), "animal-verify");
    private static readonly string EventsPath = Path.Combine(OutDir, "events.jsonl");
    private static readonly object WriteLock = new object();
    private static int _started, _done, _workerCalls;
    private static string _workerDef = "-", _workerTex = "-", _workerMat = "-";

    public static void Initialize()
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(EventsPath, "");
        Event("start", new Dictionary<string, object> { ["out"] = OutDir });

        try
        {
            MethodInfo worker = AccessTools.Method(typeof(Widgets), "ThingIconWorker");
            if (worker == null) throw new MissingMethodException("Widgets.ThingIconWorker");
            new Harmony("waylith.animalverify.worker").Patch(
                worker,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(StartupHook), nameof(WorkerPrefix))));
            Event("worker_patch", new Dictionary<string, object> { ["ok"] = true });
        }
        catch (Exception e)
        {
            Event("worker_patch", new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = Error(e)
            });
        }

        new Thread(WaitForBoot) { IsBackground = true, Name = "animalverify-waiter" }.Start();
        new Thread(Watchdog) { IsBackground = true, Name = "animalverify-watchdog" }.Start();
    }

    public static void WorkerPrefix(object[] __args)
    {
        Interlocked.Increment(ref _workerCalls);
        ThingDef def = __args != null && __args.Length > 1 ? __args[1] as ThingDef : null;
        Texture tex = __args != null && __args.Length > 2 ? __args[2] as Texture : null;
        Material mat = __args != null && __args.Length > 6 ? __args[6] as Material : null;
        _workerDef = def == null ? "<null>" : def.defName;
        _workerTex = Name(tex);
        _workerMat = Name(mat);
    }

    private static void Watchdog()
    {
        Thread.Sleep(300000);
        if (Volatile.Read(ref _done) != 0) return;
        Event("result", new Dictionary<string, object>
        {
            ["result"] = "timeout"
        });
        Environment.Exit(4);
    }

    private static void WaitForBoot()
    {
        while (Waylith.Runtime.WaylithRuntime.Current == null) Thread.Sleep(20);
        Waylith.Runtime.WaylithRuntime.Current.App.BootCompleted += Run;
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
            string startupError = null;
            bool startup = false;
            if (bootstrap != null)
            {
                try
                {
                    RuntimeHelpers.RunClassConstructor(bootstrap.TypeHandle);
                    startup = true;
                }
                catch (Exception e) { startupError = Error(e); }
            }

            Event("mod", new Dictionary<string, object>
            {
                ["assembly"] = glyphfix != null,
                ["version"] = glyphfix?.GetName().Version.ToString(),
                ["bootstrap"] = bootstrap != null,
                ["bootstrapInvoked"] = startup,
                ["bootstrapError"] = startupError
            });

            foreach (string species in Species) Probe(species);
            Event("result", new Dictionary<string, object> { ["result"] = "done" });
            Console.WriteLine($"{T} RESULT=DONE");
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

    private static void Probe(string species)
    {
        var rec = new Dictionary<string, object> { ["species"] = species };
        try
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(species);
            PawnKindDef kind = def?.race?.AnyPawnKind;
            rec["defFound"] = def != null;
            rec["kind"] = kind?.defName;
            if (def == null || kind == null) throw new InvalidOperationException("animal def/kind missing");

            Pawn pawn = PawnGenerator.GeneratePawn(kind, (Faction)null);
            if (pawn == null) throw new InvalidOperationException("PawnGenerator returned null");
            pawn.Drawer?.renderer?.EnsureGraphicsInitialized();
            Graphic body = pawn.Drawer?.renderer?.BodyGraphic;
            Material bodyMat = body?.MatAt(Rot4.East);
            rec["bodyGraphic"] = body?.GetType().Name;
            rec["bodyTexture"] = Name(bodyMat?.mainTexture);
            rec["bodyMaterial"] = Name(bodyMat);

            float scale = 0, angle = 0;
            Vector2 proportions = Vector2.zero;
            Color color = Color.magenta;
            Material material = null;
            Texture icon = Widgets.GetIconFor(pawn, new Vector2(128f, 128f), Rot4.East, false,
                                              out scale, out angle, out proportions, out color, out material);
            Texture2D tex = icon as Texture2D;
            rec["iconTexture"] = Name(tex);
            rec["iconSize"] = tex == null ? "-" : tex.width + "x" + tex.height;
            rec["badTex"] = tex == null || ReferenceEquals(tex, BaseContent.BadTex);
            rec["iconMaterial"] = Name(material);
            rec["iconComplexUi"] = IsComplexUi(material);
            rec["iconColor"] = color.ToString();

            _workerCalls = 0;
            _workerDef = _workerTex = _workerMat = "-";
            string drawError = null;
            try { Widgets.ThingIcon(new Rect(0f, 0f, 32f, 32f), pawn); }
            catch (Exception e) { drawError = Error(e); }
            rec["workerCalls"] = _workerCalls;
            rec["workerDef"] = _workerDef;
            rec["workerTexture"] = _workerTex;
            rec["workerMaterial"] = _workerMat;
            rec["workerComplexUi"] = _workerMat.StartsWith("CutoutComplexUI", StringComparison.Ordinal);
            rec["drawError"] = drawError;
        }
        catch (Exception e)
        {
            rec["error"] = Error(e);
        }
        Event("animal", rec);
        Console.WriteLine($"{T} " + JsonSerializer.Serialize(rec));
        Console.Out.Flush();
    }

    private static bool IsComplexUi(Material material)
    {
        return Name(material).StartsWith("CutoutComplexUI", StringComparison.Ordinal);
    }

    private static string Name(object value)
    {
        if (value == null) return "";
        try
        {
            UnityEngine.Object uo = value as UnityEngine.Object;
            if (uo != null) return uo.name ?? "";
            PropertyInfo property = value.GetType().GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
            return property == null ? value.ToString() : (property.GetValue(value) as string) ?? "";
        }
        catch { return value.GetType().Name; }
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        catch { return Array.Empty<Type>(); }
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
            lock (WriteLock) File.AppendAllText(EventsPath, JsonSerializer.Serialize(data) + Environment.NewLine);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{T} event-write-failed {Error(e)}");
        }
    }
}
