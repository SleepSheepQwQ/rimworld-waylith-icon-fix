using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace AgentTouchFix
{
    /// <summary>
    /// The earliest managed hook a mod assembly gets, and the one this module needs.
    ///
    /// Measured in CI (run 44753193398): this port resolves Def graphics while the Defs are being
    /// loaded - ThingDef.PostLoad -> Graphic -> ContentFinder -> ModContentLoader.LoadItem ->
    /// ModDdsLoader.TryLoadDds - and that happens BEFORE any mod's StaticConstructorOnStartup runs
    /// (the log shows "Unsupported compressed DDS texture format: 33545844" at 14:25:44 and the mod's
    /// own "bootstrapping" line at 14:25:44.995). A prefix installed from a static constructor
    /// therefore only ever sees textures loaded after the first pass, i.e. nothing.
    ///
    /// [ModuleInitializer] runs when the assembly is loaded, which is before Def parsing, so the patch
    /// is in place for that first pass. Install() is idempotent and retry-able: if some type it needs
    /// is not loaded yet, the StaticConstructorOnStartup attempt at the end of startup tries again.
    /// </summary>
    internal static class DdsPrefixAltEarly
    {
        [ModuleInitializer]
        internal static void Init()
        {
            try { DdsPrefixAlt.Install(); } catch { }
        }
    }
    /// <summary>
    /// Repairs loose mod DDS textures one level below ContentFinder: a prefix on
    /// Verse.ModDdsLoader.TryLoadDds(VirtualFile).
    ///
    /// Why this seam rather than a ContentFinder postfix: TryLoadDds is the single place where a loose
    /// .dds is turned into a Texture2D, it sees the real file (so the header can be read from disk),
    /// and returning false from a prefix means the shipped method never runs - no texture cache entry,
    /// no double upload, no second Harmony patch competing with CaseFix's postfix on
    /// ContentFinder&lt;Texture2D&gt;.Get. The module is deliberately the mirror image of
    /// DdsFormatFix (which repairs afterwards, from the ContentFinder side) so the two can be measured
    /// against each other.
    ///
    /// What it repairs, and why (all of it measured, not assumed):
    ///   A. DdsPixelFormat.ToTextureFormat maps every FourCC "DX10" file to TextureFormat.BC7 without
    ///      reading the DXGI word - IsBc7 compares FourCC against 808540228 ('DX10'), not 'BC7'. A BC4
    ///      or BC5 mask behind a DX10 header is therefore created as BC7 and fed 8-byte blocks where
    ///      16 were expected: the texture object exists, the upload "succeeds", and GetPixels32 dies
    ///      later with IndexOutOfRangeException (CI run 44746260732).
    ///   B. Every compressed FourCC other than DXT1/DXT5/DX10 throws NotSupportedException inside
    ///      ToTextureFormat (DXT2, DXT3, DXT4, ATI1, ATI2, BC4U, BC5U). ModContentLoader.LoadItem
    ///      catches it and stores BaseContent.BadTex, so the art is a blank square.
    ///   C. Texture2D.LoadRawTextureData(byte[]) feeds ConvertUnityLevels with the texture's own mip
    ///      count. A payload with fewer levels than the texture has leaves the rest empty, and a
    ///      texture drawn smaller than 1:1 (every icon) samples exactly those. Anything built here
    ///      carries as many levels as the texture is created with; one-level (and partial) sources get
    ///      a box-filtered RGBA32 chain on the CPU.
    ///
    /// Safety: a file this module will not or cannot handle returns true, so the original method runs
    /// exactly as before. Every reflection step is guarded; an exception anywhere in the prefix is
    /// caught and the original is allowed to continue - the worst case is the behaviour without this
    /// module.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class DdsPrefixAlt
    {
        /// <summary>Kill switch, same shape as the mod's other switches: "off"/"0"/"false"/"disable".</summary>
        internal const string SwitchPath =
            "/data/data/com.ports1de.waylith/files/game/agenttouchfix.ddsalt";

        private const int LogCap = 8;
        private const int MaxCache = 512;
        private const long MaxFileBytes = 32L * 1024 * 1024;

        private static bool _ready, _off, _patched;
        private static int _attempts;

        private static Type _texType, _fmtType, _fsType;
        private static ConstructorInfo _ctor;
        private static MethodInfo _loadRaw, _apply, _getPixels;
        private static Member _mPath, _mName, _mFilter, _mAniso;
        private static object _trilinear;

        private static Dictionary<string, object> _built;
        private static Dictionary<string, bool> _native;

        private static int _repaired, _declined, _skipped, _warned;

        // ------------------------------------------------------------------ entry

        /// <summary>
        /// RimWorld runs the static constructor of every type carrying this attribute, the same way it
        /// runs Bootstrap's. Install() is idempotent, so Bootstrap may call it too (that is the one
        /// line the parent module needs) and the module still works when it does not.
        /// </summary>
        static DdsPrefixAlt()
        {
            try { Install(); }
            catch (Exception e) { try { Refl.Warn("ddsalt: static ctor: " + e); } catch { } }
        }

        internal static void Install()
        {
            if (_ready || _off) return;
            if (_attempts >= 4) return;   // the early attempt may run before every type is loaded
            _attempts++;
            try
            {
                _off = !SwitchOn(ReadSwitch());
                if (_off) { Refl.Info("ddsalt: disabled by " + SwitchPath); return; }

                Type mod = Refl.T("Verse.ModDdsLoader");
                _fmtType = Refl.T("UnityEngine.TextureFormat");
                _texType = Refl.T("UnityEngine.Texture2D");
                Type filterType = Refl.T("UnityEngine.FilterMode");
                _fsType = Refl.T("Verse.FilesystemFile");

                if (mod == null || _fmtType == null || _texType == null)
                {
                    Refl.Warn("ddsalt: Verse.ModDdsLoader / UnityEngine.Texture2D not loaded yet (attempt "
                              + _attempts + "), the static-ctor attempt retries");
                    return;
                }

                MethodInfo target = null;
                foreach (MethodInfo m in mod.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "TryLoadDds") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 1 && m.ReturnType == _texType) { target = m; break; }
                }
                if (target == null)
                {
                    Refl.Warn("ddsalt: ModDdsLoader.TryLoadDds(VirtualFile) not found, skipped");
                    return;
                }

                _ctor = _texType.GetConstructor(new[]
                {
                    typeof(int), typeof(int), _fmtType, typeof(int), typeof(bool), typeof(bool),
                });
                _loadRaw = _texType.GetMethod("LoadRawTextureData", new[] { typeof(byte[]) });
                _apply = _texType.GetMethod("Apply", new[] { typeof(bool), typeof(bool) })
                         ?? _texType.GetMethod("Apply", new[] { typeof(bool) });
                _getPixels = _texType.GetMethod("GetPixels32", Type.EmptyTypes);

                if (_ctor == null || _loadRaw == null)
                {
                    Refl.Warn("ddsalt: Texture2D ctor / LoadRawTextureData unavailable, skipped");
                    return;
                }

                _mFilter = Member.Resolve(_texType, "filterMode", true, false);
                _mAniso = Member.Resolve(_texType, "anisoLevel", true, false);
                try { _trilinear = filterType == null ? null : Enum.Parse(filterType, "Trilinear"); }
                catch { _trilinear = null; }

                _built = new Dictionary<string, object>(StringComparer.Ordinal);
                _native = new Dictionary<string, bool>(StringComparer.Ordinal);

                // The patch method is generic over (file type, return type) and closed here: the mod
                // compiles against a stub assembly, so it cannot name UnityEngine.Texture2D itself,
                // and a plain `ref object __result` would be an IL type mismatch on a Texture2D
                // return. Closing the generic yields exactly the signature the target has.
                Type fileType = target.GetParameters()[0].ParameterType;
                MethodInfo named = typeof(DdsPrefixAlt).GetMethod("Prefix",
                    BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo byArgs = typeof(DdsPrefixAlt).GetMethod("PrefixArgs",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (named == null || byArgs == null) { Refl.Warn("ddsalt: patch methods missing"); return; }

                var harmony = new Harmony("sleepsheep.waylith.agenttouchfix.ddsprefix");
                _mPath = Member.Resolve(fileType, "FullPath", false, true)
                         ?? Member.Resolve(_fsType, "FullPath", false, true);
                _mName = Member.Resolve(fileType, "Name", false, true)
                         ?? Member.Resolve(_fsType, "Name", false, true);

                string bound = "named";
                if (!_patched)
                {
                    try
                    {
                        harmony.Patch(target, prefix: new HarmonyMethod(
                            named.MakeGenericMethod(fileType, target.ReturnType)));
                    }
                    catch (Exception e)
                    {
                        // Harmony binds a prefix parameter by name; a port that renames `file` would
                        // make the named form unbindable. __args only needs the argument array.
                        Refl.Info("ddsalt: named prefix refused (" + Short(e) + "), binding through __args");
                        bound = "args";
                        harmony.Patch(target, prefix: new HarmonyMethod(
                            byArgs.MakeGenericMethod(target.ReturnType)));
                    }
                    _patched = true;
                }

                _ready = true;
                Refl.Info("ddsalt: armed - prefix on " + mod.Name + ".TryLoadDds(" + fileType.Name + ")"
                          + " <- " + target.ReturnType.Name + " via " + bound
                          + "; dxgi word honoured, bc4/bc5/dxt3 decoded to rgba32, full cpu mip chain,"
                          + " unhandled formats fall through to the original");
            }
            catch (Exception e) { Refl.Warn("ddsalt: install failed: " + Short(e)); }
        }

        // ------------------------------------------------------------------ prefix

        /// <summary>
        /// Runs before ModDdsLoader.TryLoadDds. Returns false only when a texture was built here, so
        /// the shipped method (and its exception behaviour) is untouched for everything else.
        /// </summary>
        private static bool Prefix<TFile, TResult>(TFile file, ref TResult __result)
            where TFile : class
            where TResult : class
        {
            if (!_ready || _off) return true;
            object tex;
            if (!TryReplace(file, out tex)) return true;
            __result = (TResult)tex;
            return false;
        }

        /// <summary>
        /// Same prefix, bound through __args instead of a named parameter. Harmony matches a patch
        /// parameter by name, so the first form depends on the port keeping `file` as the parameter
        /// name; this one only needs the argument array. Install() patches with the named form and
        /// falls back to this one when Harmony refuses it, so a renamed parameter cannot silently
        /// disable the module.
        /// </summary>
        private static bool PrefixArgs<TResult>(object[] __args, ref TResult __result)
            where TResult : class
        {
            if (!_ready || _off) return true;
            object tex;
            if (!TryReplace(__args == null || __args.Length == 0 ? null : __args[0], out tex)) return true;
            __result = (TResult)tex;
            return false;
        }

        /// <summary>
        /// The whole decision for one file. Returns true when <paramref name="texture"/> replaced the
        /// loader's answer; false means "let the original run", which is the answer for every failure
        /// inside this module.
        /// </summary>
        private static bool TryReplace(object file, out object texture)
        {
            texture = null;
            if (!_ready || _off || file == null) return false;

            try
            {
                // ModDdsLoader only supports FilesystemFile (it throws NotSupportedException otherwise).
                // Anything else is let through so the original behaviour, error included, is unchanged.
                if (_fsType == null || !_fsType.IsInstanceOfType(file)) { _declined++; return false; }

                string path = _mPath == null ? null : _mPath.Get(file) as string;
                if (string.IsNullOrEmpty(path)) { _declined++; return false; }

                object cached;
                if (_built.TryGetValue(path, out cached))
                {
                    if (cached == null) return false;
                    texture = cached;
                    return true;
                }

                // Header first: the common case (BC7, legacy DXT1/DXT5, plain RGB, BC6H) is decided
                // from 148 bytes and the file is never read again. That matters at startup, where
                // every shipped .dds is asked for once and most of them are already right.
                byte[] head = ReadHeader(path);
                AltHeader h;
                if (head == null || !DdsAltFormat.TryParse(head, head.Length, out h) || !h.Valid)
                {
                    Note("skip '" + Leaf(path) + "': header not understood");
                    return false;
                }

                if (h.Codec != AltCodec.Bc1 && DdsAltFormat.Decide(h, false, true).Plan == AltPlan.Skip)
                {
                    Note("skip '" + Leaf(path) + "' " + DdsAltFormat.Describe(h)
                         + " -> " + DdsAltFormat.Decide(h, false, true).Why);
                    return false;
                }

                byte[] all = ReadAll(path);
                if (all == null) { _declined++; return false; }
                if (!DdsAltFormat.TryParse(all, all.Length, out h) || !h.Valid)
                {
                    Note("skip '" + Leaf(path) + "': header not understood");
                    return false;
                }

                bool punchThrough = h.Codec == AltCodec.Bc1
                                    && DdsAltFormat.Bc1PunchThrough(all, h.DataOffset,
                                                                    h.Width, h.Height,
                                                                    Math.Max(1, h.MipCount));

                string native = DdsAltFormat.NativeName(h.Codec);
                bool nativeUsable = DdsAltFormat.NativeCandidate(h) && native != null
                                    && NativeUsable(native, DdsAltFormat.BlockBytes(h.Codec));

                AltChoice choice = DdsAltFormat.Decide(h, punchThrough, nativeUsable);
                if (choice.Plan == AltPlan.Skip)
                {
                    Note("skip '" + Leaf(path) + "' " + DdsAltFormat.Describe(h)
                         + " -> " + choice.Why);
                    return false;
                }

                object built = Build(all, h, choice, file);
                if (built == null) { _declined++; return false; }

                if (_built.Count >= MaxCache) _built.Clear();
                _built[path] = built;

                int n = System.Threading.Interlocked.Increment(ref _repaired);
                if (n <= LogCap)
                {
                    Refl.Info("ddsalt: repaired #" + n + " '" + Leaf(path) + "' "
                              + DdsAltFormat.Describe(h) + " -> "
                              + (choice.Plan == AltPlan.Native ? choice.Format + " native" : "RGBA32 managed")
                              + " levels=" + choice.Levels + " (" + choice.Why + ")");
                }

                texture = built;
                return true;
            }
            catch (Exception e)
            {
                // A prefix must never take the game down: log once and let the loader do its job.
                WarnOnce("prefix: " + Short(e));
                return false;
            }
        }

        // ------------------------------------------------------------------ build

        private static object Build(byte[] all, AltHeader h, AltChoice choice, object file)
        {
            byte[] payload;
            int levels;
            string format;

            if (choice.Plan == AltPlan.Native)
            {
                long need = DdsAltFormat.ChainBytes(choice.Codec, h.Width, h.Height, choice.Levels);
                if (need <= 0 || all.Length - (long)h.DataOffset < need)
                {
                    WarnOnce("truncated native '" + h.Width + "x" + h.Height + "' need " + need);
                    return null;
                }
                payload = new byte[(int)need];
                Array.Copy(all, h.DataOffset, payload, 0, payload.Length);
                levels = choice.Levels;
                format = choice.Format;
            }
            else
            {
                byte[][] decoded = DdsAltFormat.DecodeChain(choice.Codec, all, h.DataOffset,
                                                            h.Width, h.Height, choice.Levels);
                if (decoded == null)
                {
                    WarnOnce("decode failed " + DdsAltFormat.CodecName(choice.Codec)
                             + " " + h.Width + "x" + h.Height + " mips=" + choice.Levels);
                    return null;
                }

                byte[][] chain = DdsAltFormat.FitChain(decoded, h.Width, h.Height, choice.Levels);
                if (chain == null) { WarnOnce("chain build failed"); return null; }

                payload = DdsAltFormat.Join(chain);
                if (payload == null) { WarnOnce("chain join failed"); return null; }
                levels = chain.Length;
                format = "RGBA32";
            }

            // The one invariant that matters to the engine: the payload must carry exactly the levels
            // the texture is created with, or the sampler reads empty levels at icon size.
            if (!DdsAltFormat.PayloadFits(choice.Plan, choice.Codec, h.Width, h.Height, levels, payload.Length))
            {
                WarnOnce("payload " + payload.Length + " bytes does not fill " + levels
                         + " level(s) of " + h.Width + "x" + h.Height + " " + format);
                return null;
            }

            object fmt = Format(format);
            if (fmt == null)
            {
                WarnOnce("TextureFormat." + format + " not available in this port");
                return null;
            }

            object tex = _ctor.Invoke(new object[] { h.Width, h.Height, fmt, levels, false, true });
            if (tex == null) { WarnOnce("Texture2D ctor returned null"); return null; }

            _loadRaw.Invoke(tex, new object[] { payload });

            // updateMipmaps: false - every level is already in the payload, and this port's
            // GenerateMipmaps refuses block formats, so a true here would be a no-op at best.
            // makeNoLongerReadable: false - GetPixels32 must keep working (the loader passes true,
            // which is why its own textures are at the mercy of the port's read-back path).
            Apply(tex, false, false);

            Decorate(tex, file);
            return tex;
        }

        private static void Decorate(object tex, object file)
        {
            try
            {
                string name = _mName == null ? null : _mName.Get(file) as string;
                if (!string.IsNullOrEmpty(name))
                    _mName.Set(tex, Path.GetFileNameWithoutExtension(name));
            }
            catch { }
            try { if (_mFilter != null && _trilinear != null) _mFilter.Set(tex, _trilinear); } catch { }
            try { if (_mAniso != null) _mAniso.Set(tex, 0); } catch { }
        }

        private static void Apply(object tex, bool updateMipmaps, bool noLongerReadable)
        {
            if (_apply == null) return;
            try
            {
                int argc = _apply.GetParameters().Length;
                object[] args = argc >= 2 ? new object[] { updateMipmaps, noLongerReadable }
                               : argc == 1 ? new object[] { updateMipmaps }
                               : new object[0];
                _apply.Invoke(tex, args);
            }
            catch (Exception e) { WarnOnce("Apply: " + Short(e)); }
        }

        private static object Format(string name)
        {
            if (_fmtType == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                if (!Enum.IsDefined(_fmtType, name)) return null;
                return Enum.Parse(_fmtType, name);
            }
            catch { return null; }
        }

        /// <summary>
        /// "Can the running engine really carry this format?" - asked once per format with a 4x4
        /// block: create, LoadRawTextureData, GetPixels32. A format the CPU decoder cannot read back
        /// (the BC5-as-BC7 defect in one sentence) counts as unusable, and the file goes down the
        /// managed path instead.
        /// </summary>
        private static bool NativeUsable(string format, int blockBytes)
        {
            if (string.IsNullOrEmpty(format) || blockBytes <= 0) return false;
            bool known;
            if (_native.TryGetValue(format, out known)) return known;

            bool ok = false;
            object tex = null;
            try
            {
                object fmt = Format(format);
                if (fmt != null && _getPixels != null)
                {
                    tex = _ctor.Invoke(new object[] { 4, 4, fmt, 1, false, true });
                    _loadRaw.Invoke(tex, new object[] { new byte[blockBytes] });
                    _getPixels.Invoke(tex, null);
                    ok = true;
                }
            }
            catch (Exception e)
            {
                Refl.Info("ddsalt: native " + format + " unusable (" + Root(e).GetType().Name
                          + ": " + Root(e).Message + "), decoding to rgba32 instead");
            }
            finally
            {
                try { (tex as IDisposable)?.Dispose(); } catch { }
            }

            if (_native.Count >= 32) _native.Clear();
            _native[format] = ok;
            return ok;
        }

        // ------------------------------------------------------------------ io / misc

        /// <summary>The fixed header plus the DX10 extension - enough to decide, never the whole file.</summary>
        private static byte[] ReadHeader(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var buf = new byte[DdsAltFormat.Dx10HeaderBytes];
                    int at = 0;
                    while (at < buf.Length)
                    {
                        int n = fs.Read(buf, at, buf.Length - at);
                        if (n <= 0) break;
                        at += n;
                    }
                    if (at < DdsAltFormat.HeaderBytes) return null;
                    if (at == buf.Length) return buf;
                    var cut = new byte[at];
                    Array.Copy(buf, cut, at);
                    return cut;
                }
            }
            catch (Exception e) { WarnOnce("header " + path + ": " + Short(e)); return null; }
        }

        private static byte[] ReadAll(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                if (info.Length < DdsAltFormat.HeaderBytes || info.Length > MaxFileBytes)
                {
                    _declined++;
                    return null;
                }
                return File.ReadAllBytes(path);
            }
            catch (Exception e)
            {
                WarnOnce("read " + path + ": " + Short(e));
                return null;
            }
        }

        private static string ReadSwitch()
        {
            try { return File.Exists(SwitchPath) ? (File.ReadAllText(SwitchPath) ?? "") : ""; }
            catch (Exception e) { Refl.Warn("ddsalt: switch unreadable: " + Short(e)); return ""; }
        }

        /// <summary>Only an explicit off turns the module off - same rule as the mod's other switches.</summary>
        internal static bool SwitchOn(string text)
        {
            if (text == null) return true;
            string s = text.Trim().ToLowerInvariant();
            if (s.Length == 0) return true;
            return !(s == "off" || s == "0" || s == "false" || s == "disable" || s == "disabled");
        }

        private static string Leaf(string path)
        {
            try { return Path.GetFileName(path); } catch { return path; }
        }

        private static void Note(string message)
        {
            int n = System.Threading.Interlocked.Increment(ref _skipped);
            if (n <= LogCap) Refl.Info("ddsalt: " + message);
        }

        private static void WarnOnce(string message)
        {
            if (System.Threading.Interlocked.Increment(ref _warned) > 12) return;
            Refl.Warn("ddsalt: " + message);
        }

        private static Exception Root(Exception e)
        {
            var tie = e as TargetInvocationException;
            return tie != null && tie.InnerException != null ? Root(tie.InnerException) : e;
        }

        private static string Short(Exception e)
        {
            Exception r = Root(e);
            return r.GetType().Name + ": " + r.Message;
        }
    }
}
