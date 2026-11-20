using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// Repairs loose mod DDS textures that Verse.ModDdsLoader files under the wrong format, or refuses
    /// outright.
    ///
    /// Two root causes, both in DdsPixelFormat.ToTextureFormat:
    ///   A. It maps *every* FourCC "DX10" file to TextureFormat.BC7 without reading the DXGI word that
    ///      follows the header. A BC5/BC4/BC1/BC3 texture behind a DX10 header is therefore created as
    ///      BC7 and fed 8-byte blocks where 16 were expected: the texture object exists, uploading
    ///      "works", and GetPixels32 later dies with IndexOutOfRangeException (measured on the device
    ///      and in CI with the bc5.dds fixture).
    ///   B. It knows only DXT1, DXT5 and DX10; every other compressed FourCC (DXT3, DXT2, DXT4, ATI1,
    ///      ATI2, BC4U, BC5U) throws NotSupportedException. ModContentLoader.LoadItem catches that,
    ///      logs one line and stores BaseContent.BadTex in the content list, so the art is a blank
    ///      square - and because ShadowedByDds drops a same-named .png, there is no fallback file.
    ///
    /// The fix is a postfix on the closed ContentFinder&lt;Texture2D&gt;.Get(string, bool): the original
    /// lookup runs untouched, and only when its answer is unusable (null / BadTex) or was built from a
    /// file the loader mis-mapped does the postfix substitute a texture this mod built from the file
    /// itself. Repair is decided from the header, never guessed:
    ///
    ///   BC1 / BC7 / plain uncompressed   -> left alone (the loader is right)
    ///   BC3 behind DX10                  -> native TextureFormat.DXT5
    ///   BC4 / BC5                        -> native BC4 / BC5 when the running engine can read them
    ///                                       back (probed once with a 4x4 block), else decoded to RGBA32
    ///   BC2 (DXT3/DXT2)                  -> decoded to RGBA32 (4-bit alpha + DXT1 colour block; the
    ///                                       TextureFormat enum has no DXT3/BC2 member at all)
    ///   BC6H                             -> recognised, logged, neutral RGBA32 fill (see BuildNeutral)
    ///
    /// Everything is reflection - this mod carries no reference to UnityEngine - and any failure leaves
    /// the loader's own result in place, so the worst case is the behaviour without this module.
    /// </summary>
    internal static class DdsFormatFix
    {
        internal const string SwitchPath =
            "/data/data/com.ports1de.waylith/files/game/agenttouchfix.dds";

        private const string HarmonyId = "sleepsheep.waylith.agenttouchfix.ddsformat";
        private const int LogCap = 8;
        private const int MaxEntries = 8192;
        private const long MaxFileBytes = 96L * 1024 * 1024;

        private static bool _off, _ready;
        private static int _repaired, _failed, _warned, _logged, _indexed;

        private static Type _texType, _fmtType, _filterType;
        private static object _badTex, _trilinear;
        private static Member _mName, _mFilter, _mAniso;
        private static ConstructorInfo _ctor;
        private static MethodInfo _loadRaw, _apply, _getPixels32;

        private static bool _nativeBc4, _nativeBc5;

        /// <summary>Loose .dds files of every active mod, keyed the way ContentFinder keys textures.</summary>
        private static Dictionary<string, string> _files;
        /// <summary>Per key: 0 = leave the loader's result alone, otherwise (int)codec + 1.</summary>
        private static Dictionary<string, int> _plans;
        /// <summary>The textures this module built, so a second lookup returns the same object.</summary>
        private static Dictionary<string, object> _built;

        internal static void Install()
        {
            try
            {
                _off = !IconFallbackDecision.Enabled(ReadSwitch());
                if (_off) { Refl.Info("ddsfix: disabled by " + SwitchPath); return; }

                Type finder = Refl.T("Verse.ContentFinder`1");
                _texType = Refl.T("UnityEngine.Texture2D");
                _fmtType = Refl.T("UnityEngine.TextureFormat");
                _filterType = Refl.T("UnityEngine.FilterMode");
                if (finder == null || _texType == null || _fmtType == null)
                {
                    Refl.Warn("ddsfix: types missing, skipped");
                    return;
                }

                MethodInfo get = finder.MakeGenericType(_texType)
                    .GetMethod("Get", new[] { typeof(string), typeof(bool) });
                _ctor = _texType.GetConstructor(new[]
                {
                    typeof(int), typeof(int), _fmtType, typeof(int), typeof(bool), typeof(bool),
                });
                _loadRaw = _texType.GetMethod("LoadRawTextureData", new[] { typeof(byte[]) });
                _apply = _texType.GetMethod("Apply", new[] { typeof(bool), typeof(bool) })
                         ?? _texType.GetMethod("Apply", new[] { typeof(bool) })
                         ?? _texType.GetMethod("Apply", Type.EmptyTypes);
                _getPixels32 = _texType.GetMethod("GetPixels32", Type.EmptyTypes);

                if (get == null || _ctor == null || _loadRaw == null)
                {
                    Refl.Warn("ddsfix: ContentFinder<Texture2D>.Get / Texture2D ctor unavailable, skipped");
                    return;
                }

                _badTex = ReadStaticField("Verse.BaseContent", "BadTex");
                _mName = Member.Resolve(_texType, "name", true, false);
                _mFilter = Member.Resolve(_texType, "filterMode", true, false);
                _mAniso = Member.Resolve(_texType, "anisoLevel", true, false);
                try { _trilinear = _filterType == null ? null : Enum.Parse(_filterType, "Trilinear"); }
                catch { _trilinear = null; }

                _plans = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                _built = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                BuildIndex();

                // Native BC4/BC5 is the cheap path (no CPU decode, no 4x memory): ask the engine rather
                // than assume. A 4x4 block is enough - either GetPixels32 can read the format back or
                // it cannot, and a silent upload failure is exactly what must not reach the screen.
                _nativeBc4 = ProbeNative("BC4");
                bool nativeBc5 = ProbeNative("BC5");
                _nativeBc5 = nativeBc5;

                MethodInfo postfix = typeof(DdsFormatFix)
                    .GetMethod(nameof(AfterGet), BindingFlags.Static | BindingFlags.NonPublic)
                    .MakeGenericMethod(_texType);
                new Harmony(HarmonyId).Patch(get, postfix: new HarmonyMethod(postfix));
                _ready = true;

                Refl.Info("ddsfix: dxgi-aware dds repair armed (loose .dds indexed=" + _indexed
                          + ", native bc4=" + (_nativeBc4 ? "yes" : "no")
                          + " bc5=" + (_nativeBc5 ? "yes" : "no")
                          + ", dxt3/bc6h decoded to rgba32)");
            }
            catch (Exception e) { Refl.Warn("ddsfix: install failed: " + e.Message); }
        }

        // ------------------------------------------------------------------ postfix

        /// <summary>
        /// Runs after Verse.ContentFinder&lt;Texture2D&gt;.Get decided there is nothing to return.
        ///
        /// The generic argument is checked by identity first: Harmony patches one closed instantiation,
        /// but the runtime shares one body across every reference type, so a ContentFinder&lt;Shader&gt;
        /// lookup can reach this code too (measured with the CaseFix postfix, which was hit for
        /// Texture2D, Shader and AudioClip alike). Anything that is not the live Texture2D type leaves
        /// through the first line.
        /// </summary>
        [HarmonyPriority(Priority.Low)]
        private static void AfterGet<T>(string itemPath, ref T __result) where T : class
        {
            if (!_ready || _off || _files == null) return;
            if (typeof(T) != _texType) return;
            if (string.IsNullOrEmpty(itemPath)) return;

            try
            {
                string key = KeyOf(itemPath);
                if (key.Length == 0) return;

                int plan;
                if (!_plans.TryGetValue(key, out plan))
                {
                    plan = Decide(key);
                    if (_plans.Count >= MaxEntries) _plans.Clear();
                    _plans[key] = plan;
                }
                if (plan == 0) return;

                object tex;
                if (!_built.TryGetValue(key, out tex) || tex == null)
                {
                    tex = Build(key, (DdsCodec)(plan - 1));
                    if (tex == null) return;
                    if (_built.Count >= MaxEntries) _built.Clear();
                    _built[key] = tex;
                }

                __result = (T)tex;
                int n = System.Threading.Interlocked.Increment(ref _repaired);
                if (n <= LogCap) Refl.Info("ddsfix: repaired #" + n + " '" + key + "'");
            }
            catch (Exception e) { WarnOnce("get " + itemPath + ": " + e.Message); }
        }

        private static string KeyOf(string itemPath)
        {
            string key = itemPath.Replace('\\', '/').Trim();
            if (key.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase))
                key = key.Substring("Textures/".Length);
            key = key.TrimStart('/');
            if (key.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                key = key.Substring(0, key.Length - 4);
            return key;
        }

        /// <summary>
        /// One header read per distinct texture path. 0 keeps the loader's answer; anything else is
        /// (codec + 1). The log line names the mapping the loader would have used ("dxgi=80 loader=BC7")
        /// so a repair can be read off the log instead of trusted.
        /// </summary>
        private static int Decide(string key)
        {
            string path;
            if (!_files.TryGetValue(key, out path)) return 0;

            DdsInfo info;
            if (!ReadHeader(path, out info)) return 0;

            bool repair = DdsFormat.NeedsRepair(info);
            bool punchThrough = false;
            if (!repair && info.Codec == DdsCodec.Bc1)
            {
                // DXT1 is mapped to VkFormat.Bc1RGBUNormBlock, which Vulkan defines as alpha == 1, so a
                // block in the three-colour mode renders its transparent texels as opaque white/black.
                // Such a file has to go through the RGBA32 decode even though the loader "handles" DXT1.
                punchThrough = HasPunchThrough(path, info);
                repair = punchThrough;
            }
            if (!repair) return 0;

            int n = System.Threading.Interlocked.Increment(ref _logged);
            if (n <= LogCap)
            {
                Refl.Info("ddsfix: '" + key + "' " + Describe(info)
                          + " loader=" + DdsFormat.LoaderFormatName(info)
                          + " -> " + CodecName(info.Codec)
                          + " (" + (punchThrough ? "managed, 1-bit alpha" : PathKind(info.Codec)) + ")");
            }
            return (int)info.Codec + 1;
        }

        /// <summary>Reads the header (and, for a DX10 file, its 20-byte extension).</summary>
        private static bool ReadHeader(string path, out DdsInfo info)
        {
            info = default(DdsInfo);
            try
            {
                var buf = new byte[DdsFormat.Dx10HeaderBytes];
                int read;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int at = 0;
                    while (at < buf.Length)
                    {
                        int n = fs.Read(buf, at, buf.Length - at);
                        if (n <= 0) break;
                        at += n;
                    }
                    read = at;
                }
                return DdsFormat.TryParse(buf, read, out info);
            }
            catch (Exception e)
            {
                WarnOnce("header " + path + ": " + e.Message);
                return false;
            }
        }

        /// <summary>Scan of the BC1 blocks of every mip level (see DdsFormat.Bc1HasPunchThrough).</summary>
        private static bool HasPunchThrough(string path, in DdsInfo info)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > MaxFileBytes) return false;
                byte[] all = File.ReadAllBytes(path);
                DdsInfo again;
                if (!DdsFormat.TryParse(all, all.Length, out again)) return false;
                return DdsFormat.Bc1HasPunchThrough(all, again.DataOffset, again.Width, again.Height,
                                                    Math.Max(1, again.MipCount));
            }
            catch (Exception e)
            {
                WarnOnce("punch-through scan " + path + ": " + e.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------ build

        private static object Build(string key, DdsCodec expected)
        {
            try
            {
                string path = _files[key];
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > MaxFileBytes || file.Length < DdsFormat.BaseHeaderBytes)
                    return null;

                byte[] all = File.ReadAllBytes(path);
                DdsInfo info;
                if (!DdsFormat.TryParse(all, all.Length, out info)) return null;
                if (info.Codec != expected) return null;

                int levels = Math.Max(1, info.MipCount);
                long need = DdsFormat.ChainBytes(info.Codec, info.Width, info.Height, levels);
                if (need <= 0 || all.Length - info.DataOffset < need)
                {
                    WarnOnce("truncated " + key + ": have " + Math.Max(0, all.Length - info.DataOffset)
                             + " bytes, need " + need);
                    return null;
                }

                bool punchThrough = info.Codec == DdsCodec.Bc1
                                    && DdsFormat.Bc1HasPunchThrough(all, info.DataOffset,
                                                                    info.Width, info.Height, levels);
                if (!punchThrough && !DdsFormat.NeedsRepair(info)) return null;

                object tex = info.Codec == DdsCodec.Bc6H ? BuildNeutral(info)
                           : punchThrough ? BuildManaged(all, info, levels)
                           : BuildNative(all, info, levels, need) ?? BuildManaged(all, info, levels);
                if (tex == null) return null;

                if (_mName != null) _mName.Set(tex, System.IO.Path.GetFileNameWithoutExtension(path));
                if (_mFilter != null && _trilinear != null) _mFilter.Set(tex, _trilinear);
                if (_mAniso != null) _mAniso.Set(tex, 0);
                Apply(tex);
                return tex;
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _failed);
                WarnOnce("build " + key + ": " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// The engine's own upload path: same bytes the loader would have handed over, with the format
        /// the header actually names. Zero decode cost and no 4x memory, which matters for the mask
        /// textures that are the common BC4/BC5 case.
        /// </summary>
        private static object BuildNative(byte[] all, in DdsInfo info, int levels, long need)
        {
            string format = NativeFormat(info);
            if (format == null) return null;

            object tex = Create(info, format, levels);
            if (tex == null) return null;

            var payload = new byte[need];
            Array.Copy(all, info.DataOffset, payload, 0, payload.Length);
            _loadRaw.Invoke(tex, new object[] { payload });
            return tex;
        }

        /// <summary>
        /// The managed path: decode every mip level to RGBA32 and upload the chain. Used for the formats
        /// the engine has no TextureFormat for (DXT3/DXT2), and for BC4/BC5 when the capability probe
        /// says the format cannot be read back.
        /// </summary>
        private static object BuildManaged(byte[] all, in DdsInfo info, int levels)
        {
            byte[][] mips = DdsFormat.DecodeMipChain(info.Codec, all, info.DataOffset,
                                                    info.Width, info.Height, levels);
            if (mips == null) return null;

            int total = 0;
            for (int i = 0; i < mips.Length; i++) total += mips[i].Length;

            var raw = new byte[total];
            int at = 0;
            for (int i = 0; i < mips.Length; i++)
            {
                Array.Copy(mips[i], 0, raw, at, mips[i].Length);
                at += mips[i].Length;
            }

            object tex = Create(info, "RGBA32", mips.Length);
            if (tex == null) return null;
            _loadRaw.Invoke(tex, new object[] { raw });
            return tex;
        }

        /// <summary>
        /// BC6H is the one block format this fix does not decode: it is an HDR format (14 modes, its own
        /// partition/weight tables) that no RimWorld content ships, and writing a half-right decoder
        /// would be worse than saying so. The texture is created with a neutral fill so the art is a
        /// visible grey square instead of a blank one, and the log names the file.
        /// </summary>
        private static object BuildNeutral(in DdsInfo info)
        {
            WarnOnce("bc6h " + info.Width + "x" + info.Height + ": no managed decoder, neutral fill");
            object tex = Create(info, "RGBA32", 1);
            if (tex == null) return null;
            var raw = new byte[info.Width * info.Height * 4];
            for (int i = 0; i < raw.Length; i += 4)
            {
                raw[i] = 128; raw[i + 1] = 128; raw[i + 2] = 128; raw[i + 3] = 255;
            }
            _loadRaw.Invoke(tex, new object[] { raw });
            return tex;
        }

        private static object Create(in DdsInfo info, string format, int levels)
        {
            try
            {
                object fmt = Enum.Parse(_fmtType, format);
                return _ctor.Invoke(new object[] { info.Width, info.Height, fmt, levels, false, true });
            }
            catch (Exception e)
            {
                WarnOnce("create " + format + " " + info.Width + "x" + info.Height + ": " + e.Message);
                return null;
            }
        }

        private static void Apply(object tex)
        {
            if (_apply == null) return;
            object[] args = _apply.GetParameters().Length == 2
                ? new object[] { false, false }
                : _apply.GetParameters().Length == 1 ? new object[] { false } : new object[0];
            _apply.Invoke(tex, args);
        }

        /// <summary>BC4/BC5 only when the engine can read the format back; DXT1/DXT5 always.</summary>
        private static string NativeFormat(in DdsInfo info)
        {
            switch (info.Codec)
            {
                case DdsCodec.Bc1: return "DXT1";
                case DdsCodec.Bc3: return "DXT5";
                case DdsCodec.Bc4: return _nativeBc4 ? "BC4" : null;
                case DdsCodec.Bc5: return _nativeBc5 ? "BC5" : null;
                case DdsCodec.Uncompressed: return info.Bgra ? "BGRA32" : "RGBA32";
                default: return null;
            }
        }

        private static string PathKind(DdsCodec codec)
        {
            switch (codec)
            {
                case DdsCodec.Bc2: return "managed";
                case DdsCodec.Bc4: return _nativeBc4 ? "native" : "managed";
                case DdsCodec.Bc5: return _nativeBc5 ? "native" : "managed";
                case DdsCodec.Bc6H: return "neutral";
                default: return "native";
            }
        }

        // ------------------------------------------------------------------ capability probe

        /// <summary>
        /// Creates a 4x4 texture in the given format from one all-zero block and reads it back. True
        /// means "create + LoadRawTextureData + GetPixels32 all work", which is what the repair needs
        /// from the running engine; a throw on any step drops to the managed decoder, the same code
        /// path DXT3 uses, so a platform that cannot upload BC5 still gets correct pixels.
        /// </summary>
        private static bool ProbeNative(string format)
        {
            if (_getPixels32 == null) return false;
            object tex = null;
            try
            {
                object fmt = Enum.Parse(_fmtType, format);
                tex = _ctor.Invoke(new object[] { 4, 4, fmt, 1, false, true });
                _loadRaw.Invoke(tex, new object[] { new byte[format == "BC5" ? 16 : 8] });
                _getPixels32.Invoke(tex, null);
                return true;
            }
            catch (Exception e)
            {
                Refl.Info("ddsfix: native " + format + " unavailable (" + Root(e).GetType().Name
                          + ": " + Root(e).Message + "), decoding to rgba32");
                return false;
            }
            finally
            {
                if (tex != null)
                {
                    try { (tex as IDisposable)?.Dispose(); } catch { }
                }
            }
        }

        private static Exception Root(Exception e)
        {
            var t = e as TargetInvocationException;
            return t != null && t.InnerException != null ? t.InnerException : e;
        }

        // ------------------------------------------------------------------ index

        /// <summary>
        /// One walk of every active mod's Textures/ tree collecting loose .dds files. This is the only
        /// per-mod cost (a few thousand directory entries from a subset that is already on disk) and it
        /// keeps the lookup itself to a dictionary hit, instead of a File.Exists per texture request.
        /// </summary>
        private static void BuildIndex()
        {
            _files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Type lmm = Refl.T("Verse.LoadedModManager");
                Member running = Member.Resolve(lmm, "RunningModsListForReading", false, false);
                IList mods = running == null ? null : running.Get(null) as IList;
                if (mods == null)
                {
                    Refl.Warn("ddsfix: LoadedModManager.RunningModsListForReading unavailable");
                    return;
                }

                for (int i = mods.Count - 1; i >= 0; i--)      // later mods win, like ContentFinder
                {
                    object mod = mods[i];
                    if (mod == null) continue;
                    Member rootMember = Member.Resolve(mod.GetType(), "rootDirInt", false, true);
                    DirectoryInfo root = rootMember == null ? null : rootMember.Get(mod) as DirectoryInfo;
                    if (root == null || !root.Exists) continue;

                    string textures = System.IO.Path.Combine(root.FullName, "Textures");
                    if (!Directory.Exists(textures)) continue;

                    foreach (string path in EnumerateDds(textures))
                    {
                        string rel = path.Substring(textures.Length).TrimStart('/', '\\').Replace('\\', '/');
                        if (rel.Length <= 4) continue;
                        string key = rel.Substring(0, rel.Length - 4);
                        if (_files.ContainsKey(key)) continue;
                        _files[key] = path;
                        _indexed++;
                    }
                }
            }
            catch (Exception e) { Refl.Warn("ddsfix: index failed: " + e.Message); }
        }

        private static List<string> EnumerateDds(string root)
        {
            var list = new List<string>();
            foreach (string pattern in new[] { "*.dds", "*.DDS" })
            {
                try { list.AddRange(Directory.GetFiles(root, pattern, SearchOption.AllDirectories)); }
                catch (Exception e) { WarnOnce("scan " + root + ": " + e.Message); }
            }
            return list;
        }

        // ------------------------------------------------------------------ misc

        private static object ReadStaticField(string typeName, string fieldName)
        {
            Type t = Refl.T(typeName);
            if (t == null) return null;
            FieldInfo f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.Static)
                          ?? t.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
            return f == null ? null : f.GetValue(null);
        }

        private static string ReadSwitch()
        {
            try
            {
                return File.Exists(SwitchPath) ? (File.ReadAllText(SwitchPath) ?? "") : "";
            }
            catch (Exception e)
            {
                Refl.Warn("ddsfix: switch unreadable: " + e.Message);
                return "";
            }
        }

        private static string Describe(in DdsInfo info)
        {
            return (info.Dx10 ? "fourcc=DX10 dxgi=" + info.Dxgi : "fourcc=" + (info.FourCc ?? ""))
                   + " " + info.Width + "x" + info.Height + " mips=" + info.MipCount;
        }

        private static string CodecName(DdsCodec codec)
        {
            switch (codec)
            {
                case DdsCodec.Bc1: return "BC1";
                case DdsCodec.Bc2: return "BC2/DXT3";
                case DdsCodec.Bc3: return "BC3/DXT5";
                case DdsCodec.Bc4: return "BC4";
                case DdsCodec.Bc5: return "BC5";
                case DdsCodec.Bc6H: return "BC6H";
                case DdsCodec.Bc7: return "BC7";
                case DdsCodec.Uncompressed: return "RGBA8";
                default: return "unknown";
            }
        }

        private static void WarnOnce(string message)
        {
            if (System.Threading.Interlocked.Increment(ref _warned) > 12) return;
            Refl.Warn("ddsfix: " + message);
        }
    }
}
