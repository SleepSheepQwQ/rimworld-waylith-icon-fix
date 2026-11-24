using System;

namespace AgentTouchFix
{
    /// <summary>
    /// Pure decisions for the post-load DDS sweep. Kept separate from reflection so the path/key and
    /// unusable-entry rules can be asserted without the game assembly.
    /// </summary>
    internal static class ContentSweepDecision
    {
        /// <summary>
        /// "Textures/Things/Foo.png.dds" -> "Things/Foo.png", which is the key
        /// ModContentHolder&lt;Texture2D&gt; stores after stripping one extension.
        /// </summary>
        internal static string KeyFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string rel = path.Replace('\\', '/').TrimStart('/');
            if (rel.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring("Textures/".Length);
            if (!rel.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) || rel.Length <= 4) return null;
            string key = rel.Substring(0, rel.Length - 4);
            return key.Length == 0 || key.EndsWith("/", StringComparison.Ordinal) ? null : key;
        }

        /// <summary>Missing, null, and BaseContent.BadTex all need one loader retry.</summary>
        internal static bool Retry(bool present, bool isNull, bool isBadTex)
        {
            return !present || isNull || isBadTex;
        }
    }
}
