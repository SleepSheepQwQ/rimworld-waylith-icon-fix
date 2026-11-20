using System;
using System.Collections.Generic;
using System.Text;

namespace AgentTouchFix
{
    /// <summary>
    /// The block codec a loose mod .dds really carries - decided from the header, not from FourCC.
    ///
    /// Kept separate from DdsFormat (the ContentFinder-side repair) on purpose: this module works
    /// below it, as a prefix on Verse.ModDdsLoader.TryLoadDds, and the two implementations are meant
    /// to be checkable against each other. Nothing here touches UnityEngine, so tests/DdsAltSt
    /// compiles this exact file and asserts it against hand-computed blocks.
    /// </summary>
    internal enum AltCodec
    {
        Unknown = 0,
        Bc1,        // DXT1 / DXGI 70-72
        Bc2,        // DXT2 (premultiplied) / DXT3 / DXGI 73-75
        Bc3,        // DXT4 (premultiplied) / DXT5 / DXGI 76-78
        Bc4,        // ATI1 / BC4U / DXGI 79-81
        Bc5,        // ATI2 / BC5U / DXGI 82-84
        Bc6H,       // DXGI 94-96 (HDR - recognised, not decoded here)
        Bc7,        // DXGI 97-99
        Rgba8,      // uncompressed RGB/BGR 888, 565, 4444, DXGI 28/29/87/91
    }

    /// <summary>What this module intends to do with one file.</summary>
    internal enum AltPlan
    {
        /// <summary>Leave the loader alone - it already does the right thing, or the file is out of scope.</summary>
        Skip = 0,
        /// <summary>Upload the file's own blocks under the format the header names (block size must match).</summary>
        Native = 1,
        /// <summary>Decode to RGBA32 and upload a full CPU-built mip chain.</summary>
        Managed = 2,
    }

    /// <summary>What one DDS header says. Plain fields only, so a test can build one by hand.</summary>
    internal struct AltHeader
    {
        internal bool Valid;
        internal int Width;
        internal int Height;
        internal int MipCount;
        internal int DataOffset;      // 128, or 148 when a DX10 header follows
        internal bool Compressed;
        internal bool Dx10;
        internal bool Bgra;
        internal uint Dxgi;
        internal string FourCc;
        internal AltCodec Codec;
    }

    /// <summary>The decision for one file: plan, format name, and how many levels the texture gets.</summary>
    internal struct AltChoice
    {
        internal AltPlan Plan;
        internal AltCodec Codec;
        internal string Format;       // native TextureFormat name; null for Managed/Skip
        internal int Levels;          // levels to create the texture with == levels in the payload
        internal string Why;
    }

    /// <summary>
    /// Header parsing, the format decision table, the block decoders and the mip-chain builder.
    ///
    /// Two facts about this port drive the design:
    ///   * DdsPixelFormat.ToTextureFormat maps every FourCC "DX10" file to BC7 without reading the
    ///     DXGI word (IsBc7 compares FourCC against 'DX10'), and throws on every compressed FourCC but
    ///     DXT1/DXT5. So BC4/BC5/BC1/BC3 behind a DX10 header are created as BC7 and fed 8-byte blocks
    ///     where 16 were expected (GetPixels32 dies with IndexOutOfRangeException later), and
    ///     DXT3/DXT2/DXT4/ATI1/ATI2/BC4U/BC5U throw inside the loader and become BaseContent.BadTex.
    ///   * Texture2D.LoadRawTextureData(byte[]) hands the payload to ConvertUnityLevels with the
    ///     texture's own mip count, so a payload that carries fewer levels than the texture has leaves
    ///     the remaining levels empty - a texture drawn smaller than 1:1 samples those and disappears.
    ///     Everything built here therefore carries exactly as many levels as the texture is created
    ///     with (AltChoice.Levels), and one-level sources get a box-filtered chain on the CPU.
    /// </summary>
    internal static class DdsAltFormat
    {
        internal const uint Magic = 0x20534444;      // "DDS "
        internal const int HeaderBytes = 128;
        internal const int Dx10HeaderBytes = 148;
        internal const int MaxDimension = 16384;

        private const uint PfAlphaPixels = 0x1;
        private const uint PfFourCc = 0x4;
        private const uint PfRgb = 0x40;
        private const uint PfLuminance = 0x20000;
        private const uint FlagsMipCount = 0x20000;

        // ------------------------------------------------------------------ header

        /// <summary>
        /// Parse the fixed header (plus the DX10 extension when FourCC says so). False for anything
        /// this module will not reason about: wrong magic, truncated buffer, nonsense dimensions, a
        /// texture array or a 3D volume.
        /// </summary>
        internal static bool TryParse(byte[] buf, int length, out AltHeader h)
        {
            h = default(AltHeader);
            if (buf == null || length < HeaderBytes) return false;
            if (BitConverter.ToUInt32(buf, 0) != Magic) return false;
            if (BitConverter.ToUInt32(buf, 4) != 124u) return false;

            uint flags = BitConverter.ToUInt32(buf, 8);
            int height = BitConverter.ToInt32(buf, 12);
            int width = BitConverter.ToInt32(buf, 16);
            if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension) return false;

            int mips = (flags & FlagsMipCount) != 0 ? (int)BitConverter.ToUInt32(buf, 28) : 1;
            if (mips <= 0 || mips > 32) return false;

            if (BitConverter.ToUInt32(buf, 76) != 32u) return false;    // DDS_PIXELFORMAT.dwSize
            uint pf = BitConverter.ToUInt32(buf, 80);
            bool hasFourCc = (pf & PfFourCc) != 0;
            string fourCc = hasFourCc ? Encoding.ASCII.GetString(buf, 84, 4).TrimEnd('\0', ' ') : "";

            bool dx10 = hasFourCc && string.Equals(fourCc, "DX10", StringComparison.Ordinal);
            if (dx10 && length < Dx10HeaderBytes) return false;

            uint dxgi = 0;
            bool bgra = false;
            AltCodec codec;

            if (dx10)
            {
                dxgi = BitConverter.ToUInt32(buf, 128);
                uint dimension = BitConverter.ToUInt32(buf, 132);
                uint arraySize = BitConverter.ToUInt32(buf, 136);
                if (arraySize > 1) return false;        // texture arrays are out of scope
                if (dimension == 4) return false;       // 3D volume
                codec = MapDxgi(dxgi);
                bgra = dxgi == 87 || dxgi == 91;        // B8G8R8A8_UNORM(_SRGB)
            }
            else if (hasFourCc)
            {
                codec = MapFourCc(fourCc);
            }
            else if ((pf & (PfRgb | PfLuminance)) != 0)
            {
                codec = AltCodec.Rgba8;
            }
            else
            {
                codec = AltCodec.Unknown;
            }

            h.Valid = true;
            h.Width = width;
            h.Height = height;
            h.MipCount = mips;
            h.DataOffset = dx10 ? Dx10HeaderBytes : HeaderBytes;
            h.Compressed = hasFourCc;
            h.Dx10 = dx10;
            h.Bgra = bgra;
            h.Dxgi = dxgi;
            h.FourCc = fourCc;
            h.Codec = codec;
            return true;
        }

        /// <summary>
        /// The DXGI word the shipped loader never reads. Values follow dxgiformat.h: BC4_UNORM is 80
        /// and BC5_UNORM is 83, which is why the CI fixture called "bc5.dds" is a single-channel BC4
        /// (10936 bytes of 8-byte blocks for 128x128 with 8 levels) and still lands on Bc4 here.
        /// </summary>
        internal static AltCodec MapDxgi(uint dxgi)
        {
            switch (dxgi)
            {
                case 70: case 71: case 72: return AltCodec.Bc1;
                case 73: case 74: case 75: return AltCodec.Bc2;
                case 76: case 77: case 78: return AltCodec.Bc3;
                case 79: case 80: case 81: return AltCodec.Bc4;
                case 82: case 83: case 84: return AltCodec.Bc5;
                case 94: case 95: case 96: return AltCodec.Bc6H;
                case 97: case 98: case 99: return AltCodec.Bc7;
                case 28: case 29:                    // R8G8B8A8_UNORM(_SRGB)
                case 87: case 91:                    // B8G8R8A8_UNORM(_SRGB)
                    return AltCodec.Rgba8;
                default: return AltCodec.Unknown;
            }
        }

        /// <summary>Legacy FourCCs. DXT2/DXT4 carry the same blocks as DXT3/DXT5.</summary>
        internal static AltCodec MapFourCc(string fourCc)
        {
            switch ((fourCc ?? "").TrimEnd('\0', ' '))
            {
                case "DXT1": return AltCodec.Bc1;
                case "DXT2": return AltCodec.Bc2;
                case "DXT3": return AltCodec.Bc2;
                case "DXT4": return AltCodec.Bc3;
                case "DXT5": return AltCodec.Bc3;
                case "ATI1": case "BC4U": case "BC4S": return AltCodec.Bc4;
                case "ATI2": case "BC5U": case "BC5S": return AltCodec.Bc5;
                default: return AltCodec.Unknown;
            }
        }

        /// <summary>
        /// The engine's TextureFormat member for a codec, or null when the enum has no such member.
        /// This port's enum carries Alpha8/RGB24/RGBA32/ARGB32/DXT1/DXT5/RGB565/RGBA4444/BC6H/BC7 and
        /// the half/float formats - there is no DXT3, BC4 or BC5 member, so those codecs can only ever
        /// be uploaded as RGBA32 here. The mapping is still spelled out so a port that grows the
        /// members needs no decision change.
        /// </summary>
        internal static string NativeName(AltCodec codec)
        {
            switch (codec)
            {
                case AltCodec.Bc1: return "DXT1";
                case AltCodec.Bc3: return "DXT5";
                case AltCodec.Bc4: return "BC4";
                case AltCodec.Bc5: return "BC5";
                case AltCodec.Bc7: return "BC7";
                case AltCodec.Rgba8: return "RGBA32";
                default: return null;    // Bc2 (no member at all), Bc6H (handled separately)
            }
        }

        private static bool IsFourCc(in AltHeader h, string expected)
        {
            return string.Equals((h.FourCc ?? "").TrimEnd('\0', ' '), expected, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ sizes

        /// <summary>Bytes per 4x4 block, or 0 when the codec is not block compressed.</summary>
        internal static int BlockBytes(AltCodec codec)
        {
            switch (codec)
            {
                case AltCodec.Bc1:
                case AltCodec.Bc4: return 8;
                case AltCodec.Bc2:
                case AltCodec.Bc3:
                case AltCodec.Bc5:
                case AltCodec.Bc6H:
                case AltCodec.Bc7: return 16;
                default: return 0;
            }
        }

        /// <summary>Bytes one level occupies in the file.</summary>
        internal static int MipBytes(AltCodec codec, int width, int height)
        {
            if (codec == AltCodec.Rgba8) return width * height * 4;
            int block = BlockBytes(codec);
            if (block == 0) return 0;
            return ((width + 3) / 4) * ((height + 3) / 4) * block;
        }

        /// <summary>Bytes the whole chain occupies - the truncation check before decoding.</summary>
        internal static long ChainBytes(AltCodec codec, int width, int height, int levels)
        {
            if (codec == AltCodec.Rgba8) return 0;   // not used: uncompressed files are left alone
            if (BlockBytes(codec) == 0) return 0;
            long total = 0;
            for (int i = 0; i < Math.Max(1, levels); i++)
            {
                int w = Math.Max(1, width >> i);
                int h = Math.Max(1, height >> i);
                total += MipBytes(codec, w, h);
                if (w == 1 && h == 1) break;
            }
            return total;
        }

        /// <summary>1 + floor(log2(max(w,h))) - a 128x128 asset gets 8 levels, a 256x256 gets 9.</summary>
        internal static int MipChainLength(int width, int height)
        {
            int size = Math.Max(width, height);
            int levels = 1;
            while (size > 1) { size >>= 1; levels++; }
            return levels;
        }

        /// <summary>
        /// Bytes an RGBA32 payload of this chain occupies. Used to prove the invariant the engine
        /// cares about: payload levels == levels the texture was created with.
        /// </summary>
        internal static long RgbaChainBytes(int width, int height, int levels)
        {
            long total = 0;
            for (int i = 0; i < Math.Max(1, levels); i++)
            {
                int w = Math.Max(1, width >> i);
                int h = Math.Max(1, height >> i);
                total += (long)w * h * 4;
            }
            return total;
        }

        // ------------------------------------------------------------------ decision

        /// <summary>
        /// What to do with one file.
        ///
        ///   * Skip    - the loader is already right (BC7, legacy DXT1/DXT5, plain RGB/BGR) or the file
        ///               is out of scope (unknown, BC6H, DX10-uncompressed). Skipping means the original
        ///               method runs unchanged, which is the whole safety story of this module.
        ///   * Native  - the blocks go up as the format the header names. Only for codecs whose block
        ///               size equals the native format's (DXT1/DXT5/BC7) and only when the file carries
        ///               a full chain, because compressed levels cannot be generated on the CPU. This is
        ///               the DX10-BC1 / DX10-BC3 case, which the loader files under BC7.
        ///   * Managed - decode to RGBA32 and fill the chain on the CPU. DXT3/DXT2, BC4/BC5 (no
        ///               TextureFormat members in this port), one-level sources of any codec, and BC1
        ///               with punch-through alpha (BC1_RGB has no alpha in Vulkan).
        /// </summary>
        internal static AltChoice Decide(in AltHeader h, bool punchThrough, bool nativeUsable)
        {
            var c = default(AltChoice);
            c.Codec = h.Codec;
            c.Plan = AltPlan.Skip;

            if (!h.Valid) { c.Why = "no header"; return c; }

            int full = MipChainLength(h.Width, h.Height);
            int levels = Math.Max(1, h.MipCount);
            if (levels > full) levels = full;
            c.Levels = levels;

            switch (h.Codec)
            {
                case AltCodec.Unknown:
                    c.Why = "unknown pixel format";
                    return c;

                case AltCodec.Bc6H:
                    // HDR, 14 modes, its own partition/weight tables. Not decoded here: a half-right
                    // decoder would be worse than saying so, and no RimWorld content ships BC6H.
                    c.Why = "bc6h: no managed decoder, left to the loader";
                    return c;

                case AltCodec.Rgba8:
                    c.Why = h.Dx10 ? "dx10 uncompressed: left to the loader" : "rgb/bgr: loader handles it";
                    return c;

                case AltCodec.Bc7:
                    c.Why = "bc7: loader mapping is already right";
                    return c;

                case AltCodec.Bc1:
                    if (punchThrough)
                    {
                        c.Plan = AltPlan.Managed;
                        c.Levels = full;
                        c.Why = "bc1 punch-through alpha";
                        return c;
                    }
                    if (!h.Dx10 && IsFourCc(h, "DXT1") && levels == full)
                    {
                        c.Why = "dxt1: loader handles it";
                        return c;
                    }
                    break;      // DX10 BC1 (the loader calls it BC7), or a chain the loader would leave short

                case AltCodec.Bc3:
                    if (!h.Dx10 && IsFourCc(h, "DXT5") && levels == full)
                    {
                        c.Why = "dxt5: loader handles it";
                        return c;
                    }
                    break;      // DX10 BC3, DXT4, or a chain the loader would leave short

                case AltCodec.Bc2:
                    c.Plan = AltPlan.Managed;
                    c.Levels = full;
                    c.Why = "dxt3/dxt2: no TextureFormat member";
                    return c;

                case AltCodec.Bc4:
                case AltCodec.Bc5:
                    break;      // native if the port exposes one, managed otherwise
            }

            string native = NativeName(h.Codec);
            if (nativeUsable && native != null && levels == full)
            {
                c.Plan = AltPlan.Native;
                c.Format = native;
                c.Levels = levels;
                c.Why = h.Dx10 ? "dx10 mislabelled, native upload" : "native upload";
                return c;
            }

            c.Plan = AltPlan.Managed;
            c.Levels = full;
            c.Why = levels < full ? "partial chain, cpu chain" : "managed rgba32";
            return c;
        }

        /// <summary>
        /// True when this header could plausibly end on the native path, i.e. the engine's own format
        /// would be used instead of a CPU decode. Used to skip the read-back probe for files that are
        /// going to be skipped or decoded regardless (BC7, DXT3, one-level sources).
        /// </summary>
        internal static bool NativeCandidate(in AltHeader h)
        {
            if (!h.Valid) return false;
            int full = MipChainLength(h.Width, h.Height);
            int levels = Math.Max(1, h.MipCount);
            if (levels > full) levels = full;
            if (levels != full) return false;      // compressed levels cannot be generated

            switch (h.Codec)
            {
                case AltCodec.Bc1: return h.Dx10;  // legacy DXT1 is already right
                case AltCodec.Bc3: return h.Dx10;  // legacy DXT5 is already right
                case AltCodec.Bc4:
                case AltCodec.Bc5: return true;    // native only if the port exposes a member
                default: return false;
            }
        }

        /// <summary>
        /// True when any BC1 block uses the three-colour + punch-through mode (c0 &lt;= c1). Such a
        /// file keeps 1-bit alpha in the CPU decode but must not go up as DXT1: the port uploads DXT1
        /// as VkFormat.Bc1RGBUNormBlock, where alpha is always 1.
        /// </summary>
        internal static bool Bc1PunchThrough(byte[] data, int offset, int width, int height, int levels)
        {
            if (data == null || offset < 0) return false;
            long need = ChainBytes(AltCodec.Bc1, width, height, levels);
            if (need <= 0 || data.Length - (long)offset < need) return false;

            int at = offset;
            for (int level = 0; level < Math.Max(1, levels); level++)
            {
                int w = Math.Max(1, width >> level);
                int h = Math.Max(1, height >> level);
                int blocks = ((w + 3) / 4) * ((h + 3) / 4);
                for (int i = 0; i < blocks; i++)
                {
                    int c0 = data[at] | (data[at + 1] << 8);
                    int c1 = data[at + 2] | (data[at + 3] << 8);
                    if (c0 <= c1) return true;
                    at += 8;
                }
                if (w == 1 && h == 1) break;
            }
            return false;
        }

        // ------------------------------------------------------------------ decode

        /// <summary>Decode a whole chain; null when the codec cannot be decoded here or data is short.</summary>
        internal static byte[][] DecodeChain(AltCodec codec, byte[] data, int offset,
                                             int width, int height, int levels)
        {
            if (data == null || BlockBytes(codec) == 0) return null;
            levels = Math.Max(1, levels);
            long need = ChainBytes(codec, width, height, levels);
            if (need <= 0 || offset < 0 || data.Length - (long)offset < need) return null;

            var result = new byte[levels][];
            int at = offset;
            for (int i = 0; i < levels; i++)
            {
                int w = Math.Max(1, width >> i);
                int h = Math.Max(1, height >> i);
                byte[] level = Decode(codec, data, at, w, h);
                if (level == null) return null;
                result[i] = level;
                at += MipBytes(codec, w, h);
                if (w == 1 && h == 1 && i + 1 < levels)
                {
                    for (int j = i + 1; j < levels; j++) result[j] = result[i];
                    break;
                }
            }
            return result;
        }

        /// <summary>
        /// One level to RGBA32, row 0 = top of the image (the order the blocks are stored in).
        ///
        /// Channel expansion follows the port's own GPU convention, which ci/dds_to_png.py documents:
        /// a BC4 mask is sampled and read back as (R,0,0,255) and a BC5 normal as (R,G,0,255). Writing
        /// the single BC4 channel into R - rather than into all three - keeps a texture decoded here
        /// identical to the same file uploaded natively by a future port that grows a BC4 member.
        /// </summary>
        internal static byte[] Decode(AltCodec codec, byte[] data, int offset, int width, int height)
        {
            int block = BlockBytes(codec);
            if (data == null || block == 0 || width <= 0 || height <= 0) return null;
            int need = MipBytes(codec, width, height);
            if (offset < 0 || data.Length - offset < need) return null;

            var dst = new byte[width * height * 4];
            int bw = (width + 3) / 4;
            int bh = (height + 3) / 4;
            var alpha = new byte[16];

            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    int at = offset + (by * bw + bx) * block;
                    switch (codec)
                    {
                        case AltCodec.Bc1:
                            ColourBlock(data, at, dst, width, height, bx * 4, by * 4, false, true);
                            break;
                        case AltCodec.Bc2:
                            FourBitAlpha(data, at, alpha);
                            ColourBlock(data, at + 8, dst, width, height, bx * 4, by * 4, true, false);
                            PutChannel(dst, width, height, bx * 4, by * 4, alpha, 3);
                            break;
                        case AltCodec.Bc3:
                            RampAlpha(data, at, alpha);
                            ColourBlock(data, at + 8, dst, width, height, bx * 4, by * 4, true, false);
                            PutChannel(dst, width, height, bx * 4, by * 4, alpha, 3);
                            break;
                        case AltCodec.Bc4:
                            // (R,0,0,255): the port's own sampling of a Bc4UNormBlock.
                            RampAlpha(data, at, alpha);
                            PutChannel(dst, width, height, bx * 4, by * 4, alpha, 0);
                            PutConstant(dst, width, height, bx * 4, by * 4, 1, 0);
                            PutConstant(dst, width, height, bx * 4, by * 4, 2, 0);
                            PutConstant(dst, width, height, bx * 4, by * 4, 3, 255);
                            break;
                        case AltCodec.Bc5:
                            RampAlpha(data, at, alpha);
                            PutChannel(dst, width, height, bx * 4, by * 4, alpha, 0);
                            RampAlpha(data, at + 8, alpha);
                            PutChannel(dst, width, height, bx * 4, by * 4, alpha, 1);
                            PutConstant(dst, width, height, bx * 4, by * 4, 2, 0);
                            PutConstant(dst, width, height, bx * 4, by * 4, 3, 255);
                            break;
                        default:
                            return null;
                    }
                }
            }
            return dst;
        }

        private static void Rgb565(ushort c, out byte r, out byte g, out byte b)
        {
            int r5 = (c >> 11) & 0x1F;
            int g6 = (c >> 5) & 0x3F;
            int b5 = c & 0x1F;
            r = (byte)((r5 * 255 + 15) / 31);
            g = (byte)((g6 * 255 + 31) / 63);
            b = (byte)((b5 * 255 + 15) / 31);
        }

        /// <summary>
        /// One DXT1-style colour block. DXT3/DXT5 always use the four-colour mode (their alpha lives in
        /// a separate block), so the caller passes fourColourOnly: true.
        /// </summary>
        private static void ColourBlock(byte[] src, int off, byte[] dst, int width, int height,
                                        int originX, int originY, bool fourColourOnly, bool punchThrough)
        {
            ushort c0 = (ushort)(src[off] | (src[off + 1] << 8));
            ushort c1 = (ushort)(src[off + 2] | (src[off + 3] << 8));
            uint bits = (uint)(src[off + 4] | (src[off + 5] << 8) | (src[off + 6] << 16) | (src[off + 7] << 24));

            byte r0, g0, b0, r1, g1, b1;
            Rgb565(c0, out r0, out g0, out b0);
            Rgb565(c1, out r1, out g1, out b1);

            var cr = new byte[4]; var cg = new byte[4]; var cb = new byte[4]; var ca = new byte[4];
            cr[0] = r0; cg[0] = g0; cb[0] = b0; ca[0] = 255;
            cr[1] = r1; cg[1] = g1; cb[1] = b1; ca[1] = 255;

            if (fourColourOnly || c0 > c1)
            {
                cr[2] = (byte)((2 * r0 + r1) / 3); cg[2] = (byte)((2 * g0 + g1) / 3); cb[2] = (byte)((2 * b0 + b1) / 3); ca[2] = 255;
                cr[3] = (byte)((r0 + 2 * r1) / 3); cg[3] = (byte)((g0 + 2 * g1) / 3); cb[3] = (byte)((b0 + 2 * b1) / 3); ca[3] = 255;
            }
            else
            {
                cr[2] = (byte)((r0 + r1) / 2); cg[2] = (byte)((g0 + g1) / 2); cb[2] = (byte)((b0 + b1) / 2); ca[2] = 255;
                cr[3] = 0; cg[3] = 0; cb[3] = 0; ca[3] = 0;
            }

            for (int y = 0; y < 4; y++)
            {
                int py = originY + y;
                if (py >= height) break;
                for (int x = 0; x < 4; x++)
                {
                    int px = originX + x;
                    if (px >= width) break;
                    int idx = (int)((bits >> (2 * (y * 4 + x))) & 3);
                    int d = (py * width + px) * 4;
                    dst[d] = cr[idx];
                    dst[d + 1] = cg[idx];
                    dst[d + 2] = cb[idx];
                    dst[d + 3] = punchThrough ? ca[idx] : (byte)255;
                }
            }
        }

        /// <summary>
        /// The 8-byte block BC4 and the alpha half of BC5/DXT5 share: two endpoints plus sixteen 3-bit
        /// indices. a0 &gt; a1 selects the seven-step ramp, otherwise the five-step ramp plus 0 and 255.
        /// </summary>
        private static void RampAlpha(byte[] src, int off, byte[] values)
        {
            int a0 = src[off];
            int a1 = src[off + 1];
            var table = new byte[8];
            table[0] = (byte)a0;
            table[1] = (byte)a1;
            if (a0 > a1)
            {
                for (int i = 1; i <= 6; i++) table[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
            }
            else
            {
                for (int i = 1; i <= 4; i++) table[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
                table[6] = 0;
                table[7] = 255;
            }

            ulong bits = 0;
            for (int i = 0; i < 6; i++) bits |= (ulong)src[off + 2 + i] << (8 * i);
            for (int i = 0; i < 16; i++) values[i] = table[(int)((bits >> (3 * i)) & 7)];
        }

        /// <summary>DXT3/DXT2 alpha: sixteen raw 4-bit values, expanded by 17 so 0xF reaches 255.</summary>
        private static void FourBitAlpha(byte[] src, int off, byte[] values)
        {
            for (int i = 0; i < 16; i++)
            {
                int nibble = (src[off + (i / 2)] >> ((i % 2) * 4)) & 0xF;
                values[i] = (byte)(nibble * 17);
            }
        }

        private static void PutChannel(byte[] dst, int width, int height, int originX, int originY, byte[] values, int channel)
        {
            for (int y = 0; y < 4; y++)
            {
                int py = originY + y;
                if (py >= height) break;
                for (int x = 0; x < 4; x++)
                {
                    int px = originX + x;
                    if (px >= width) break;
                    dst[(py * width + px) * 4 + channel] = values[y * 4 + x];
                }
            }
        }

        private static void PutConstant(byte[] dst, int width, int height, int originX, int originY, int channel, byte value)
        {
            for (int y = 0; y < 4; y++)
            {
                int py = originY + y;
                if (py >= height) break;
                for (int x = 0; x < 4; x++)
                {
                    int px = originX + x;
                    if (px >= width) break;
                    dst[(py * width + px) * 4 + channel] = value;
                }
            }
        }

        // ------------------------------------------------------------------ chain

        /// <summary>
        /// Box-filter one level down (2x2 average, edges clamped) on RGBA32 in file row order.
        /// </summary>
        internal static byte[] Downsample(byte[] rgba, int width, int height, out int outWidth, out int outHeight)
        {
            outWidth = Math.Max(1, width / 2);
            outHeight = Math.Max(1, height / 2);
            if (rgba == null || width <= 0 || height <= 0) return null;
            if (rgba.Length < width * height * 4) return null;
            if (outWidth == width && outHeight == height) return null;   // 1x1: nothing left to halve

            var dst = new byte[outWidth * outHeight * 4];
            for (int y = 0; y < outHeight; y++)
            {
                int y0 = Math.Min(height - 1, y * 2);
                int y1 = Math.Min(height - 1, y * 2 + 1);
                for (int x = 0; x < outWidth; x++)
                {
                    int x0 = Math.Min(width - 1, x * 2);
                    int x1 = Math.Min(width - 1, x * 2 + 1);
                    int a = (y0 * width + x0) * 4;
                    int b = (y0 * width + x1) * 4;
                    int c = (y1 * width + x0) * 4;
                    int d = (y1 * width + x1) * 4;
                    int o = (y * outWidth + x) * 4;
                    for (int ch = 0; ch < 4; ch++)
                        dst[o + ch] = (byte)((rgba[a + ch] + rgba[b + ch] + rgba[c + ch] + rgba[d + ch] + 2) / 4);
                }
            }
            return dst;
        }

        /// <summary>
        /// Cut or extend a decoded chain to exactly <paramref name="want"/> levels, generating the
        /// missing tail with Downsample. The result's length is the number the texture is created
        /// with, so the payload can never leave empty levels behind.
        /// </summary>
        internal static byte[][] FitChain(byte[][] levels, int width, int height, int want)
        {
            if (levels == null || levels.Length == 0 || want <= 0) return null;
            if (levels.Length >= want)
            {
                var cut = new byte[want][];
                Array.Copy(levels, cut, want);
                return cut;
            }

            var list = new List<byte[]>(want);
            for (int i = 0; i < levels.Length; i++) list.Add(levels[i]);

            int lw = Math.Max(1, width >> (levels.Length - 1));
            int lh = Math.Max(1, height >> (levels.Length - 1));
            byte[] previous = levels[levels.Length - 1];

            while (list.Count < want)
            {
                int nw, nh;
                byte[] next = Downsample(previous, lw, lh, out nw, out nh);
                if (next == null) break;
                list.Add(next);
                previous = next;
                lw = nw;
                lh = nh;
            }
            return list.ToArray();
        }

        /// <summary>Concatenate levels in file order - the layout LoadRawTextureData expects.</summary>
        internal static byte[] Join(byte[][] levels)
        {
            if (levels == null) return null;
            long total = 0;
            for (int i = 0; i < levels.Length; i++)
            {
                if (levels[i] == null) return null;
                total += levels[i].Length;
            }
            if (total > int.MaxValue) return null;

            var all = new byte[(int)total];
            int at = 0;
            for (int i = 0; i < levels.Length; i++)
            {
                Array.Copy(levels[i], 0, all, at, levels[i].Length);
                at += levels[i].Length;
            }
            return all;
        }

        /// <summary>
        /// The invariant: a payload must carry exactly as many bytes as the texture's levels account
        /// for. Checked before every upload so a mismatch is a logged refusal, never a silent texture
        /// with empty levels.
        /// </summary>
        internal static bool PayloadFits(AltPlan plan, AltCodec codec, int width, int height,
                                         int levels, long payloadBytes)
        {
            if (levels <= 0 || payloadBytes <= 0) return false;
            long expected = plan == AltPlan.Native
                ? ChainBytes(codec, width, height, levels)
                : RgbaChainBytes(width, height, levels);
            return expected == payloadBytes;
        }

        /// <summary>One line for the log: what the header said and what the loader would have used.</summary>
        internal static string Describe(in AltHeader h)
        {
            string four = h.Dx10 ? ("fourcc=DX10 dxgi=" + h.Dxgi) : ("fourcc=" + (h.FourCc ?? ""));
            return four + " " + h.Width + "x" + h.Height + " mips=" + h.MipCount
                   + " loader=" + LoaderFormatName(h);
        }

        /// <summary>The TextureFormat name the shipped loader would pick for this header.</summary>
        internal static string LoaderFormatName(in AltHeader h)
        {
            if (!h.Valid) return "?";
            if (h.Dx10) return "BC7";                       // ToTextureFormat: any DX10 becomes BC7
            if (!h.Compressed) return "uncompressed";
            switch ((h.FourCc ?? "").TrimEnd('\0', ' '))
            {
                case "DXT1": return "DXT1";
                case "DXT5": return "DXT5";
                default: return "throw";                    // DXT2/3/4, ATI1/2, BC4U/5U
            }
        }

        internal static string CodecName(AltCodec codec)
        {
            switch (codec)
            {
                case AltCodec.Bc1: return "BC1";
                case AltCodec.Bc2: return "BC2/DXT3";
                case AltCodec.Bc3: return "BC3/DXT5";
                case AltCodec.Bc4: return "BC4";
                case AltCodec.Bc5: return "BC5";
                case AltCodec.Bc6H: return "BC6H";
                case AltCodec.Bc7: return "BC7";
                case AltCodec.Rgba8: return "RGBA8";
                default: return "unknown";
            }
        }
    }
}
