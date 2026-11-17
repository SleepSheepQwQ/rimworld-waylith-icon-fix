using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using AgentTouchFix;
using Verse;

namespace PrefixBench
{
    /// <summary>Material stand-in: only carries the "name" property the prefix reflects on.</summary>
    internal sealed class FakeMaterial
    {
        private readonly string _name;
        internal FakeMaterial(string name) { _name = name; }
        public string name { get { return _name; } }
    }

    internal sealed class RefIdentity : IEqualityComparer<object>
    {
        bool IEqualityComparer<object>.Equals(object a, object b) { return ReferenceEquals(a, b); }
        int IEqualityComparer<object>.GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); }
    }

    /// <summary>
    /// Headless micro-benchmark + contract check for the IconFieldFix prefix.
    ///
    /// Both implementations live in this assembly: <see cref="IconFieldFix"/> is the shipped source
    /// file, <see cref="LegacyPrefix"/> is the v40 prefix verbatim. Same inputs, same call shape (a
    /// reused object[] argument array, the way Harmony hands the prefix over), so what differs is the
    /// code under test. Exits non-zero if the contract broke or if the fast path is slower than the
    /// old prefix.
    /// </summary>
    internal static class Program
    {
        private const int Iters = 1000000;
        private delegate void Call(object[] args);

        private static readonly List<string> Fails = new List<string>();
        private static readonly List<string> Report = new List<string>();
        private static readonly HashSet<object> Baseline = new HashSet<object>(new RefIdentity());
        private static double _oldSteady, _newSteady;

        private static int Main()
        {
            Console.WriteLine("bench: " + Iters.ToString("N0") + " calls per pass, best of 3 passes, ns/call");
            Console.WriteLine("bench: runtime " + Environment.Version + " on " + Environment.ProcessorCount + " core(s)");
            Console.WriteLine();

            Contract();

            // The first full pass pays for JIT and tiering; the second one is the measurement.
            Bench(false);
            Floor();
            Console.WriteLine();
            Console.WriteLine("bench: case                                     old ns   new ns  speedup");
            Console.WriteLine("bench: ---------------------------------------- -------- -------- --------");
            Bench(true);

            Console.WriteLine();
            Console.WriteLine("bench: steady-state fast path " + _oldSteady.ToString("F1") + " -> " + _newSteady.ToString("F1")
                              + " ns/call (" + Ratio(_oldSteady, _newSteady).ToString("F1") + "x)");
            Check(_newSteady <= _oldSteady, "fast path is not slower than the old prefix");

            Console.WriteLine();
            if (Fails.Count == 0)
            {
                Console.WriteLine("BENCH OK (contract intact, fast path " + Ratio(_oldSteady, _newSteady).ToString("F1")
                                  + "x on steady state)");
                Summary("ok");
                return 0;
            }
            for (int i = 0; i < Fails.Count; i++) Console.WriteLine("BENCH FAIL: " + Fails[i]);
            Summary("FAILED");
            return 1;
        }

        // ---------------------------------------------------------------- cases

        private static void Bench(bool report)
        {
            Measure("steady: icon ok, no material", SteadyDef, null, report, true);
            Measure("steady: icon ok + foreign material", SteadyMatDef, null, report, false);
            Measure("broken: icon null + foreign material", BrokenDef, null, report, false);
            Measure("Thing overload (def unwrapped)", ThingArg, null, report, false);
            Measure("20000 defs, icon ok (rebuild at cap)", null, ManyDefs(), report, false);
        }

        /// <summary>The floor: what one hash lookup on the same comparer costs on this machine.</summary>
        private static void Floor()
        {
            for (int i = 0; i < 20000; i++) Baseline.Add(new object());
            var args = new object[] { null, new object() };
            Baseline.Add(args[1]);
            double ns = Min(BaselineCall, args, null);
            Console.WriteLine();
            Console.WriteLine("bench: reference: one HashSet.Contains = " + ns.ToString("F2") + " ns (floor for a cache hit)");
        }

        private static void BaselineCall(object[] args) { if (Baseline.Contains(args[1])) return; }

        private static ThingDef Healthy(string name)
        {
            var d = new ThingDef();
            d.defName = name;
            d.uiIcon = new object();                                    // a texture that is not BadTex
            d.uiIconMaterial = null;
            d.graphic = new Graphic { path = "Things/Building/" + name };
            d.graphicData = new GraphicData { texPath = "Things/Building/" + name };
            return d;
        }

        private static object[] Args(object def) { return new object[] { null, def }; }

        private static object[] SteadyDef() { return Args(Healthy("Steady")); }

        private static object[] SteadyMatDef()
        {
            ThingDef d = Healthy("SteadyMat");
            d.uiIconMaterial = new FakeMaterial("MatSingle_Steel");
            return Args(d);
        }

        private static object[] BrokenDef()
        {
            // uiIcon empty and unresolvable (this host has no ContentFinder), so the def is retried on
            // every call - the case the material-name cache exists for
            var d = new ThingDef();
            d.defName = "Broken";
            d.uiIcon = null;
            d.uiIconMaterial = new FakeMaterial("MatSingle_Steel");
            d.graphic = new Graphic { path = "Things/Building/Broken" };
            d.graphicData = new GraphicData { texPath = "Things/Building/Broken" };
            return Args(d);
        }

        private static object[] ThingArg()
        {
            var t = new Thing();
            t.def = Healthy("Wrapped");
            return Args(t);
        }

        private static ThingDef[] ManyDefs()
        {
            var defs = new ThingDef[20000];
            for (int i = 0; i < defs.Length; i++) defs[i] = Healthy("Def" + i);
            return defs;
        }

        // ---------------------------------------------------------------- timing

        /// <summary>Not inlined, so the loop always pays one real call per prefix invocation.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Invoke(Call f, object[] args) { f(args); }

        private static void Measure(string name, Func<object[]> factory, ThingDef[] many, bool report, bool steady)
        {
            ResetMod();
            object[] oldArgs = factory == null ? new object[2] : factory();
            object[] newArgs = factory == null ? new object[2] : factory();

            double oldNs = Min(LegacyPrefix.Prefix, oldArgs, many);
            double newNs = Min(IconFieldFix.Prefix, newArgs, many);
            if (steady) { _oldSteady = oldNs; _newSteady = newNs; }

            if (!report) return;
            double speedup = Ratio(oldNs, newNs);
            string line = "bench: " + name.PadRight(40) + " " + oldNs.ToString("F1").PadLeft(8) + " "
                          + newNs.ToString("F1").PadLeft(8) + " " + speedup.ToString("F1").PadLeft(7) + "x";
            Console.WriteLine(line);
            Report.Add(line);
        }

        private static double Min(Call f, object[] args, ThingDef[] many)
        {
            double best = double.MaxValue;
            Run(f, args, many, 100000);                 // warm the caches and the JIT
            for (int pass = 0; pass < 3; pass++)
            {
                double v = Run(f, args, many, Iters);
                if (v < best) best = v;
            }
            return best;
        }

        private static double Run(Call f, object[] args, ThingDef[] many, int iters)
        {
            Stopwatch sw = Stopwatch.StartNew();
            if (many == null || many.Length == 0)
            {
                for (int i = 0; i < iters; i++) Invoke(f, args);
            }
            else
            {
                int n = many.Length;
                for (int i = 0; i < iters; i++) { args[1] = many[i % n]; Invoke(f, args); }
            }
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds * 1e6 / iters;
        }

        private static double Ratio(double a, double b) { return b <= 0 ? 0 : a / b; }

        // ---------------------------------------------------------------- contract

        private static void Contract()
        {
            Console.WriteLine("contract:");

            // 1. a CutoutComplexUI material is dropped (the fix itself)
            ResetMod();
            ThingDef bad = Healthy("BadMat");
            bad.uiIconMaterial = new FakeMaterial("CutoutComplexUI_Thing");
            IconFieldFix.Prefix(Args(bad));
            Check(bad.uiIconMaterial == null, "CutoutComplexUI icon material is dropped");

            // 2. a foreign material is left alone (only that one shader path is suspect)
            ResetMod();
            ThingDef good = Healthy("GoodMat");
            object keep = new FakeMaterial("MatSingle_Steel");
            good.uiIconMaterial = keep;
            IconFieldFix.Prefix(Args(good));
            Check(ReferenceEquals(good.uiIconMaterial, keep), "foreign icon material is kept");

            // 3. the user switch still wins (files/game/agenttouchfix.iconpath = material)
            ResetMod();
            IconFieldFix.Init("material");
            ThingDef kept = Healthy("SwitchMode");
            object switchMat = new FakeMaterial("CutoutComplexUI_Thing");
            kept.uiIconMaterial = switchMat;
            IconFieldFix.Prefix(Args(kept));
            Check(ReferenceEquals(kept.uiIconMaterial, switchMat), "iconpath=material keeps the material (switch honoured)");
            IconFieldFix.Init("plain");

            // 4. a healthy def is remembered, so the next call is one lookup
            ResetMod();
            ThingDef steady = Healthy("Remembered");
            IconFieldFix.Prefix(Args(steady));
            Check(Done().Contains(steady), "healthy def is remembered");

            // 5. a def that could not be repaired is NOT remembered - it must be retried
            ResetMod();
            var broken = new ThingDef();
            broken.defName = "Unfixable";
            broken.graphic = new Graphic { path = "Things/Building/Unfixable" };
            IconFieldFix.Prefix(Args(broken));
            Check(broken.uiIcon == null && !Done().Contains(broken), "unfixable def stays untouched and is retried");

            // 6. no arguments / wrong arguments: fail open, no exception, no state change
            ResetMod();
            IconFieldFix.Prefix(null);
            IconFieldFix.Prefix(new object[] { null });
            IconFieldFix.Prefix(new object[] { null, null });
            Check(Done().Count == 0, "short/null arguments are ignored");

            // 7. the def set is bounded: past 4096 entries it is dropped, never grown
            ResetMod();
            for (int i = 0; i < 5000; i++) IconFieldFix.Prefix(Args(Healthy("Cap" + i)));
            int size = Done().Count;
            Check(size > 0 && size <= 4096, "def set stays within 4096 entries (was " + size + ")");

            // 8. the name cache is bounded too
            ResetMod();
            for (int i = 0; i < 1000; i++)
            {
                ThingDef d = Healthy("Name" + i);
                d.uiIconMaterial = new FakeMaterial("MatSingle_" + i);
                IconFieldFix.Prefix(Args(d));
            }
            int names = Names().Count;
            Check(names <= 512, "material name cache stays within 512 entries (was " + names + ")");

            // 9. Thing overload: the def behind the Thing is the one repaired/remembered
            ResetMod();
            var th = new Thing();
            th.def = Healthy("ViaThing");
            th.def.uiIconMaterial = new FakeMaterial("CutoutComplexUI_Thing");
            IconFieldFix.Prefix(Args(th));
            Check(th.def.uiIconMaterial == null, "Thing overload repairs the ThingDef behind it");
            Check(Done().Contains(th.def), "Thing overload remembers the ThingDef, not the Thing");
        }

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
            if (!ok) Fails.Add(what);
        }

        // ---------------------------------------------------------------- state (same assembly)

        private static HashSet<object> Done()
        {
            return (HashSet<object>)Static("_done").GetValue(null);
        }

        private static Dictionary<object, string> Names()
        {
            return (Dictionary<object, string>)Static("_names").GetValue(null);
        }

        private static FieldInfo Static(string name)
        {
            FieldInfo f = typeof(IconFieldFix).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null) throw new MissingFieldException("IconFieldFix." + name);
            return f;
        }

        private static void ResetMod()
        {
            Done().Clear();
            Names().Clear();
            ((Dictionary<Type, PropertyInfo>)Static("_nameProp").GetValue(null)).Clear();
            Static("_mode").SetValue(null, IconPath.Plain);
        }

        // ---------------------------------------------------------------- CI summary

        private static void Summary(string status)
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
                if (string.IsNullOrEmpty(path)) return;
                var sb = new StringBuilder();
                sb.AppendLine("### prefix benchmark - " + status + " (" + Iters.ToString("N0") + " calls/pass, best of 3)");
                sb.AppendLine("```");
                foreach (string l in Report) sb.AppendLine(l.Replace("bench: ", ""));
                sb.AppendLine("```");
                File.AppendAllText(path, sb.ToString());
            }
            catch { }
        }
    }
}
