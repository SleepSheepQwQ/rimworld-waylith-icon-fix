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

        /// <summary>
        /// Reload when the header says the shipped loader will mis-map the file, or when the current
        /// content is not drawable. A non-null texture is not enough evidence: a BC4 file mis-created
        /// as BC7 is non-null but unreadable. A texture already owned by the prefix is never rebuilt.
        /// </summary>
        internal static bool NeedsLoaderRepair(bool alreadyOwned, bool headerNeedsRepair,
                                                bool contentUsable)
        {
            return !alreadyOwned && (headerNeedsRepair || !contentUsable);
        }
    }
}
