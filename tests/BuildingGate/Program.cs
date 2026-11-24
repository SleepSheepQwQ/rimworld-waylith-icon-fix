using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AgentTouchFix;
using HarmonyLib;
using Verse;
using Texture2D = Waylith.Rendering.Texture2D;

namespace BuildingGate
{
    /// <summary>
    /// Build-menu regression gate with a positive control and a deliberate negative control.
    ///
    /// It measures the real current IconFieldFix source after Harmony installation. The legacy
    /// oracle is a literal implementation of the v2.5.0 narrow strategy (graphic.path -> uiIcon,
    /// clear only the CutoutComplexUI material) and is used only to reject cases where the newer
    /// repair chooses a worse path than the version the player confirmed.
    /// </summary>
    internal static class Program
    {
        private const string T = "[BUILDGATE]";
        private static readonly List<string> Failures = new List<string>();
        private static int _assertions;

        public static int Main()
        {
            Console.WriteLine(T + " start assembly=" + typeof(Program).Assembly.FullName);
            Console.WriteLine(T + " current-source=mod/Source/IconFieldFix.cs");
            Console.WriteLine(T + " legacy-oracle=v2.5.0 narrow path");

            try
            {
                IconFieldFix.Init(IconPath.Plain);
                IconFieldFix.Install();
                Console.WriteLine(T + " harmony-targets=" + HarmonyLib.TestPatchRegistry.Targets());
                AssertPatch("Verse.Widgets", "ThingIcon", typeof(ThingDef), 1);
                AssertPatch("Verse.Widgets", "GetIconFor", typeof(ThingDef), 2);

                RunCase(
                    "royal-bed",
                    D("RoyalBed", null, Bad(), M("CutoutComplexUI_RoyalBed_south"),
                      new Graphic_Multi("Things/Building/Furniture/Bed/RoyalBed")),
                    P("Things/Building/Furniture/Bed/RoyalBed"));

                RunCase(
                    "nutrient-dispenser-graphicdata",
                    D("NutrientPasteDispenser", "Things/Building/Production/NutrientPasteDispenser",
                      Bad(), M("CutoutComplexUI_NutrientPasteDispenser_north"), null),
                    P("Things/Building/Production/NutrientPasteDispenser"));

                RunCase(
                    "collection-folder",
                    D("UniqueRevolverFixture", null, Bad(), null,
                      Collection("Things/Item/Equipment/WeaponRanged/UniqueRevolver",
                                "Things/Item/Equipment/WeaponRanged/UniqueRevolver/UniqueRevolverA")),
                    P("Things/Item/Equipment/WeaponRanged/UniqueRevolver/UniqueRevolverA"));

                // A collection-like graphic whose first variant path does not resolve, while the
                // graphic's own path does. v2.5.0 still repaired this through graphic.path. A newer
                // "variant first, never retry fallback" policy leaves uiIcon as BadTex.
                RunCase(
                    "variant-miss-falls-back-to-graphic-path",
                    D("RoyalBedVariantMiss", null, Bad(), M("CutoutComplexUI_RoyalBed_south"),
                      Collection("Things/Building/Furniture/Bed/RoyalBed",
                                 "Things/Building/Furniture/Bed/RoyalBed_north_missing")),
                    P("Things/Building/Furniture/Bed/RoyalBed"));

                RunCase(
                    "ordinary-material-is-not-erased",
                    D("Bed", null, Tx("Bed"), M("Cutout"), new Graphic_Multi("Things/Building/Furniture/Bed/Bed")),
                    P("Bed"),
                    expectMaterial: "Cutout");

                DirectGetIconForCase();
                NegativeControl();
            }
            catch (Exception e)
            {
                Fail("uncaught exception " + e.GetType().Name + ": " + OneLine(e.Message));
                Console.Error.WriteLine(e);
            }

            Console.WriteLine();
            Console.WriteLine(T + " assertions=" + _assertions + " failures=" + Failures.Count);
            if (Failures.Count == 0)
            {
                Console.WriteLine(T + " RESULT=PASS");
                return 0;
            }

            foreach (string f in Failures) Console.WriteLine(T + " FAIL " + f);
            Console.WriteLine(T + " RESULT=FAIL");
            return 1;
        }

        private static void RunCase(string name, ThingDef def, Texture2D expected,
                                    string expectMaterial = null)
        {
            ResetCurrentStatics();
            Widgets.Reset();
            InstallTextures(def, expected);
            // For the healthy control the fixture's existing icon is the one ContentFinder serves.
            // Reuse that exact instance so identity assertions test the draw path, not allocation.
            if (def.uiIcon is Texture2D current && !ReferenceEquals(current, BaseContent.BadTex))
                expected = current;
            object originalIcon = def.uiIcon;
            Material originalMaterial = def.uiIconMaterial;

            bool legacyOk = LegacyRepair(def);
            Assert(name + ".legacy-oracle", legacyOk, "v2.5.0 narrow path did not repair the case");
            Assert(name + ".legacy-icon", ReferenceEquals(def.uiIcon, expected),
                   "legacy oracle chose " + N(def.uiIcon));

            // Restore the exact pre-repair shape before invoking the current source.
            def.uiIcon = originalIcon;
            def.uiIconMaterial = originalMaterial;

            ResetCurrentStatics();
            Widgets.ThingIcon(new Rect(0, 0, 34, 34), def);

            Assert(name + ".worker-called", Widgets.WorkerCalls == 1,
                   "workerCalls=" + Widgets.WorkerCalls + " earlyReturns=" + Widgets.EarlyReturns);
            Assert(name + ".worker-texture", ReferenceEquals(Widgets.LastTexture, expected),
                   "expected " + N(expected) + " got " + N(Widgets.LastTexture));
            Assert(name + ".uiIcon", ReferenceEquals(def.uiIcon, expected),
                   "def.uiIcon=" + N(def.uiIcon));

            if (expectMaterial == null)
            {
                Assert(name + ".material-dropped", def.uiIconMaterial == null,
                       "def.uiIconMaterial=" + N(def.uiIconMaterial));
                Assert(name + ".worker-material-dropped", Widgets.LastMaterial == null,
                       "worker material=" + N(Widgets.LastMaterial));
            }
            else
            {
                Assert(name + ".material-preserved", def.uiIconMaterial != null &&
                       def.uiIconMaterial.name == expectMaterial,
                       "def.uiIconMaterial=" + N(def.uiIconMaterial));
                Assert(name + ".worker-material-preserved",
                       Widgets.LastMaterial != null && Widgets.LastMaterial.name == expectMaterial,
                       "worker material=" + N(Widgets.LastMaterial));
            }
        }

        private static void DirectGetIconForCase()
        {
            const string name = "direct-geticonfor";
            Texture2D expected = P("Things/Building/Furniture/Bed/RoyalBed");
            ThingDef def = D("RoyalBedDirect", null, Bad(), M("CutoutComplexUI_RoyalBed_south"),
                             new Graphic_Multi("Things/Building/Furniture/Bed/RoyalBed"));
            InstallTextures(def, expected);
            ResetCurrentStatics();

            Material material;
            Texture2D got = Widgets.GetIconFor(def, out material);
            Assert(name + ".texture", ReferenceEquals(got, expected),
                   "expected " + N(expected) + " got " + N(got));
            Assert(name + ".material", material == null, "material=" + N(material));
        }

        /// <summary>
        /// Proves the assertions can fail. Re-attaching the exact bad material after a successful
        /// repair must make the same predicate red; otherwise the gate is only printing green text.
        /// </summary>
        private static void NegativeControl()
        {
            const string name = "negative-control";
            Texture2D expected = P("Things/Building/Furniture/Bed/RoyalBed");
            ThingDef def = D("RoyalBedNegative", null, Bad(), M("CutoutComplexUI_RoyalBed_south"),
                             new Graphic_Multi("Things/Building/Furniture/Bed/RoyalBed"));
            InstallTextures(def, expected);
            ResetCurrentStatics();
            Widgets.ThingIcon(new Rect(0, 0, 34, 34), def);

            int before = Failures.Count;
            def.uiIconMaterial = M("CutoutComplexUI_reintroduced");
            Assert(name + ".must-detect-reattached-material", def.uiIconMaterial == null,
                   "fault injection was not detected");
            bool detected = Failures.Count > before;
            if (!detected)
                Fail(name + ".detector-did-not-fire");
            else
                Failures.RemoveAt(Failures.Count - 1); // the injected failure is expected

            Console.WriteLine(T + " CASE " + name + " expected-failure-detected=" + detected);
        }

        private static bool LegacyRepair(ThingDef def)
        {
            bool ok = def.uiIcon != null && !ReferenceEquals(def.uiIcon, BaseContent.BadTex);
            if (!ok)
            {
                string path = def.graphic == null ? null : def.graphic.path;
                if (string.IsNullOrEmpty(path) && def.graphicData != null)
                    path = def.graphicData.texPath;
                Texture2D tex = null;
                if (!string.IsNullOrEmpty(path))
                {
                    tex = ContentFinder<Texture2D>.Get(path, false);
                    if (tex == null) tex = ContentFinder<Texture2D>.Get(path + "_south", false);
                }
                if (tex != null)
                {
                    def.uiIcon = tex;
                    ok = true;
                }
            }

            if (def.uiIconMaterial != null &&
                IconPath.UsePlain(def.uiIconMaterial.name, IconPath.Plain))
                def.uiIconMaterial = null;
            return ok;
        }

        private static void InstallTextures(ThingDef def, Texture2D expected)
        {
            ContentFinder<Texture2D>.Reset();
            string path = expected.name;
            ContentFinder<Texture2D>.Add(path, expected);
            if (def.graphic != null)
            {
                string basePath = def.graphic.path;
                if (!string.IsNullOrEmpty(basePath))
                {
                    ContentFinder<Texture2D>.Add(basePath, expected);
                    ContentFinder<Texture2D>.Add(basePath + "_south", expected);
                }
            }
            if (def.graphicData != null && !string.IsNullOrEmpty(def.graphicData.texPath))
            {
                ContentFinder<Texture2D>.Add(def.graphicData.texPath, expected);
                ContentFinder<Texture2D>.Add(def.graphicData.texPath + "_south", expected);
            }
            if (def.graphic is Graphic_Collection)
            {
                Graphic[] subs = ((Graphic_Collection)def.graphic).subGraphics;
                if (subs != null)
                    foreach (Graphic g in subs)
                        if (g != null && !string.IsNullOrEmpty(g.path))
                            ContentFinder<Texture2D>.Add(g.path, expected);
            }
        }

        private static void ResetCurrentStatics()
        {
            ClearSet("_done");
            ClearDictionary("_names");
            ClearDictionary("_nameProp");
        }

        private static void ClearSet(string field)
        {
            object value = StaticField(field);
            MethodInfo clear = value == null ? null : value.GetType().GetMethod("Clear");
            clear?.Invoke(value, null);
        }

        private static void ClearDictionary(string field)
        {
            ClearSet(field);
        }

        private static object StaticField(string name)
        {
            return typeof(IconFieldFix).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
                       ?.GetValue(null);
        }

        private static void AssertPatch(string typeName, string methodName, Type first, int minimum)
        {
            string needle = typeName + "::" + methodName + "(";
            int patched = TestPatchRegistry.Targets()
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Count(t => t.StartsWith(needle, StringComparison.Ordinal));
            Assert("harmony." + methodName + "(" + first.Name + ")",
                   patched >= minimum, "patched=" + patched + " expected>=" + minimum);
        }

        private static ThingDef D(string name, string texPath, Texture2D icon, Material material,
                                  Graphic graphic)
        {
            return new ThingDef
            {
                defName = name,
                graphicData = string.IsNullOrEmpty(texPath) ? null : new GraphicData { texPath = texPath },
                uiIcon = icon,
                uiIconMaterial = material,
                graphic = graphic
            };
        }

        private static Graphic Collection(string folder, params string[] variants)
        {
            return new Graphic_Collection
            {
                path = folder,
                subGraphics = variants.Select(p => (Graphic)new Graphic(p)).ToArray()
            };
        }

        private static Texture2D Bad()
        {
            return BaseContent.BadTex;
        }

        private static Texture2D Tx(string path)
        {
            Texture2D tex = ContentFinder<Texture2D>.Get(path, false);
            return tex ?? new Texture2D(path, 128, 128);
        }

        private static Texture2D P(string path)
        {
            return new Texture2D(path, 128, 128);
        }

        private static Material M(string name)
        {
            return new Material(name);
        }

        private static string N(object value)
        {
            if (value == null) return "<null>";
            Texture2D tex = value as Texture2D;
            if (tex != null) return "<" + tex.name + ">";
            Material mat = value as Material;
            if (mat != null) return "<" + mat.name + ">";
            return "<" + value + ">";
        }

        private static void Assert(string name, bool ok, string detail)
        {
            _assertions++;
            Console.WriteLine(T + " ASSERT " + name + " ok=" + (ok ? "true" : "false") +
                              (string.IsNullOrEmpty(detail) ? "" : " " + detail));
            if (!ok) Failures.Add(name + ": " + detail);
        }

        private static void Fail(string message)
        {
            Failures.Add(message);
            Console.WriteLine(T + " FAIL " + message);
        }

        private static string OneLine(string value)
        {
            return (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
