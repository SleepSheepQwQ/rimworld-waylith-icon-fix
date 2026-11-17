using System;

namespace Verse
{
    /// <summary>
    /// Game-shaped stand-ins: the benchmark needs objects with the same member names and kinds the
    /// prefix resolves at runtime (fields on the def, "def" as a property on Thing, "name" as a
    /// property on the material), and with the same type names, since the fast path keys on them.
    /// </summary>
    public sealed class Graphic
    {
        public string path;
    }

    public sealed class GraphicData
    {
        public string texPath;
    }

    public sealed class ThingDef
    {
        public object uiIcon;
        public object uiIconMaterial;
        public Graphic graphic;
        public GraphicData graphicData;
        public string defName;
    }

    public class Thing
    {
        public ThingDef def { get; set; }
    }
}
