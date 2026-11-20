using System;

namespace AgentTouchFix
{
    /// <summary>
    /// Pure decisions for the lookup-layer texture fallback - no game types, so CI unit-tests them.
    ///
    /// The port loses art in two distinct ways, and they need two different retries:
    ///
    ///   A. The content key carries the inner extension of a double-extension texture.
    ///      ModContentHolder.ReloadAll strips exactly one extension, so "Foo_m.png.dds" is filed under
    ///      "Foo_m.png" while Graphic_Single asks for "Foo_m" (the name it has on desktop, where the
    ///      index has no extension at all). The key that was asked for does not exist, but a key that
    ///      is the same path *plus a texture extension* does - a content-list retry answers it, no
    ///      file IO involved.
    ///
    ///   B. The key exists but its value is unusable. ModContentLoader.LoadItem catches a loader
    ///      failure and stores an item carrying BaseContent.BadTex (null on the CI host, a 2x2 blank
    ///      on the device), and ModContentLoader.ShadowedByDds has already deleted the same-named
    ///      .png from the content set - so the list claims the asset and every retry through it
    ///      fails. The only copy left is the .png on disk; the retry has to read the file.
    ///
    /// Distinguishing A from B by "does any content list contain this exact key" keeps the file IO
    /// on the B path only: an ordinary miss (a def asking for art that no mod ships) never touches
    /// the disk.
    /// </summary>
    internal static class ExtFallbackDecision
    {
        /// <summary>No retry: the lookup either succeeded or nothing can be done.</summary>
        internal const int None = 0;

        /// <summary>Retry the same path with a texture extension against the content list.</summary>
        internal const int ContentKey = 1;

        /// <summary>Reload the same-named image from the owning mod's Textures/ tree.</summary>
        internal const int SameNameImage = 2;

        /// <summary>
        /// The order the content-list retry appends. ".png" first: it is the only format the port's
        /// encoded-image path is exercised with by shipped art, and the one a shadowed file is
        /// expected to be. ".dds" is deliberately absent - a ".dds" candidate is the file whose
        /// failure sent us here.
        /// </summary>
        internal static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".psd" };

        private static readonly string[] Known = { ".png", ".jpg", ".jpeg", ".psd", ".dds" };

        /// <summary>
        /// A result the engine produced but cannot draw: absent, or the BadTex placeholder. The
        /// placeholder is compared by reference - it is a single shared Texture2D instance, and two
        /// different textures with the same pixels are not the same thing.
        /// </summary>
        internal static bool Unusable(object result, object badTex)
        {
            return result == null || ReferenceEquals(result, badTex);
        }

        /// <summary>
        /// Which retry a failed lookup gets. <paramref name="keyPresent"/> is "some mod's content
        /// list already contains this exact key": that means the entry exists and its value is bad
        /// (otherwise the lookup would have succeeded), i.e. case B. When the key is absent the
        /// lookup is case A - or a genuine miss, which the candidate list answers with a miss.
        /// </summary>
        internal static int Choose(bool keyPresent, bool usable)
        {
            if (usable) return None;
            return keyPresent ? SameNameImage : ContentKey;
        }

        /// <summary>
        /// Candidate content keys for <paramref name="key"/>, in a stable order. Empty when there is
        /// nothing to append to: blank input, or a path whose leaf already carries a texture
        /// extension ("Foo.png" must not become "Foo.png.png").
        /// </summary>
        internal static string[] Candidates(string key)
        {
            string p = string.IsNullOrEmpty(key) ? null : key.Trim();
            if (string.IsNullOrEmpty(p)) return Array.Empty<string>();
            if (HasExtension(p)) return Array.Empty<string>();

            var result = new string[Extensions.Length];
            for (int i = 0; i < Extensions.Length; i++) result[i] = p + Extensions[i];
            return result;
        }

        /// <summary>
        /// The file the case-B retry reads: the same path with ".png". One extension only - the
        /// shadowed source file is an image, and a ".psd" cannot be decoded by the port's encoded
        /// image path.
        /// </summary>
        internal static string ImageName(string key)
        {
            string p = string.IsNullOrEmpty(key) ? null : key.Trim();
            if (string.IsNullOrEmpty(p)) return null;
            if (HasExtension(p)) return null;
            return p + ".png";
        }

        /// <summary>Extension test on the leaf only, so "Mods/Some.Mod/Textures/Foo" still appends.</summary>
        private static bool HasExtension(string path)
        {
            int slash = path.LastIndexOfAny(new[] { '/', '\\' });
            string leaf = slash < 0 ? path : path.Substring(slash + 1);
            if (leaf.Length == 0) return false;

            foreach (string ext in Known)
            {
                if (leaf.Length > ext.Length && leaf.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
