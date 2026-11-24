using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Verse
{
    public static class Log
    {
        public static void Message(string text) { }
        public static void Warning(string text) { Console.Error.WriteLine("WARN " + text); }
        public static void Error(string text) { Console.Error.WriteLine("ERROR " + text); }
    }

    public struct Rect
    {
        public float x;
        public float y;
        public float width;
        public float height;

        public Rect(float x, float y, float width, float height)
        {
            this.x = x;
            this.y = y;
            this.width = width;
            this.height = height;
        }
    }

    public class Graphic
    {
        public string path;

        public Graphic() { }
        public Graphic(string path) { this.path = path; }
    }

    /// <summary>The one collection shape the real engine orders and indexes by folder contents.</summary>
    public class Graphic_Collection : Graphic
    {
        public Graphic[] subGraphics;
    }

    /// <summary>Building-shaped graphic: the texture path is on the graphic itself.</summary>
    public class Graphic_Multi : Graphic
    {
        public Graphic_Multi() { }
        public Graphic_Multi(string path) : base(path) { }
    }

    public class GraphicData
    {
        public string texPath;
    }

    public class ThingDef
    {
        public object uiIcon;
        public Material uiIconMaterial;
        public Graphic graphic;
        public GraphicData graphicData;
        public string defName;
    }

    public class Thing
    {
        public ThingDef def;
    }

    public sealed class Material
    {
        public Material(string name) { this.name = name; }
        public string name { get; private set; }
    }

    public static class BaseContent
    {
        public static readonly Waylith.Rendering.Texture2D BadTex =
            new Waylith.Rendering.Texture2D("BadTex", 2, 2);
    }

    public static class ContentFinder<T>
    {
        private static readonly Dictionary<string, T> Entries =
            new Dictionary<string, T>(StringComparer.Ordinal);

        public static void Reset() { Entries.Clear(); }

        public static void Add(string path, T value)
        {
            if (object.Equals(value, default(T))) throw new ArgumentNullException(nameof(value));
            Entries[path] = value;
        }

        public static T Get(string path, bool reportFailure = true)
        {
            T value;
            return Entries.TryGetValue(path ?? "", out value) ? value : default(T);
        }
    }

    /// <summary>
    /// The real call order from Verse.Widgets.ThingIcon(Rect, ThingDef):
    /// early BadTex return, GetIconFor, then ThingIconWorker.
    /// </summary>
    public static class Widgets
    {
        public static int Calls;
        public static int EarlyReturns;
        public static int WorkerCalls;
        public static Waylith.Rendering.Texture2D LastTexture;
        public static Material LastMaterial;
        public static object LastDef;

        public static void Reset()
        {
            Calls = 0;
            EarlyReturns = 0;
            WorkerCalls = 0;
            LastTexture = null;
            LastMaterial = null;
            LastDef = null;
        }

        public static void ThingIcon(Rect rect, ThingDef thingDef, float alpha = 1f)
        {
            MethodInfo self = (MethodInfo)MethodBase.GetCurrentMethod();
            TestPatchRegistry.InvokePrefixes(self, new object[] { rect, thingDef, alpha });

            Calls++;
            LastDef = thingDef;
            if (thingDef.uiIcon == null || ReferenceEquals(thingDef.uiIcon, BaseContent.BadTex))
            {
                EarlyReturns++;
                return;
            }

            Material material;
            Waylith.Rendering.Texture2D icon = GetIconFor(thingDef, out material);
            if (icon == null || ReferenceEquals(icon, BaseContent.BadTex)) return;
            ThingIconWorker(icon, material);
        }

        public static void ThingIcon(Rect rect, Thing thing)
        {
            MethodInfo self = (MethodInfo)MethodBase.GetCurrentMethod();
            TestPatchRegistry.InvokePrefixes(self, new object[] { rect, thing });

            Calls++;
            LastDef = thing == null ? null : thing.def;
            if (thing == null || thing.def == null) return;
            ThingIcon(rect, thing.def);
        }

        public static Waylith.Rendering.Texture2D GetIconFor(
            ThingDef thingDef,
            ThingDef stuffDef = null,
            object styleDef = null,
            int? graphicIndexOverride = null)
        {
            Material ignored;
            return GetIconFor(thingDef, out ignored, stuffDef, styleDef, graphicIndexOverride);
        }

        public static Waylith.Rendering.Texture2D GetIconFor(
            ThingDef thingDef,
            out Material material,
            ThingDef stuffDef = null,
            object styleDef = null,
            int? graphicIndexOverride = null)
        {
            MethodInfo self = (MethodInfo)MethodBase.GetCurrentMethod();
            TestPatchRegistry.InvokePrefixes(self, new object[] { thingDef, null, stuffDef, styleDef, graphicIndexOverride });

            material = thingDef == null ? null : thingDef.uiIconMaterial;
            return thingDef == null ? null : thingDef.uiIcon as Waylith.Rendering.Texture2D;
        }

        public static void ThingIconWorker(Waylith.Rendering.Texture2D icon, Material material)
        {
            WorkerCalls++;
            LastTexture = icon;
            LastMaterial = material;
        }
    }
}

namespace Waylith.Rendering
{
    public sealed class Texture2D
    {
        public Texture2D(string name, int width, int height)
        {
            this.name = name;
            this.width = width;
            this.height = height;
        }

        public string name { get; private set; }
        public int width { get; private set; }
        public int height { get; private set; }

        public override string ToString()
        {
            return name + "(" + width + "x" + height + ")";
        }
    }
}
