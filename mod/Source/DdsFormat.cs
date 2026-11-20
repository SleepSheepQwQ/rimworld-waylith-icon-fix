using System;
using System.Text;

namespace AgentTouchFix
{
    /// <summary>
    /// The block codec a DDS file actually carries, decided from the header alone.
    ///
    /// This exists because Verse.DdsPixelFormat.ToTextureFormat decides by FourCC and treats every
    /// FourCC "DX10" file as BC7 without ever reading the DXGI format word that follows it
    /// (DdsPixelFormat.IsBc7 compares FourCC against 808540228, i.e. the ASCII 'DX10', not 'BC7').
    /// A BC5 mask shipped with a DX10 header therefore becomes a BC7 texture fed 8-byte blocks: the
    /// texture object comes back non-null and only blows up later, inside GetPixels32
    /// (IndexOutOfRangeException), while DXT3/DXT2/DXT4/BC4/BC5-with-a-legacy-FourCC throw
    /// NotSupportedException in the loader and end up as BaseContent.BadTex.
    /// </summary>
    internal enum DdsCodec
    {
        Unknown = 0,
        Bc1,            // DXT1 / DXGI 70-72
        Bc2,            // DXT3 (DXT2 premultiplied) / DXGI 73-75
        Bc3,            // DXT5 (DXT4 premultiplied) / DXGI 76-78
        Bc4,            // ATI1 / BC4U / DXGI 79-81
        Bc5,            // ATI2 / BC5U / DXGI 82-84
        Bc6H,           // DXGI 94-96
        Bc7,            // DXGI 97-99
        Uncompressed,   // RGB/BGR 888, 565, ARGB/RGBA 4444, DXGI 28/29/87/91
    }

    /// <summary>What one DDS header says. Plain fields only, so the tests can build one by hand.</summary>
    internal struct DdsInfo
    {
        internal bool Valid;
        internal int Width;
        internal int Height;
        internal int MipCount;
        internal int DataOffset;        // 128, or 148 when a DX10 header is present
        internal bool Compressed;       // the pixel format is FourCC based
        internal string FourCc;         // four ASCII chars, "" when the format is uncompressed
        internal bool Dx10;
        internal uint Dxgi;
        internal bool Bgra;             // DXGI 87/91: uncompressed, blue first
        internal DdsCodec Codec;
    }

    /// <summary>
    /// Header parsing, the format decision table and the block decoders - no UnityEngine type appears
    /// here, so tests/TexSt compiles this exact file and asserts it against hand-computed blocks.
    /// </summary>
    internal static class DdsFormat
    {
        internal const uint Magic = 0x20534444;    // "DDS "
        internal const int BaseHeaderBytes = 128;
        internal const int Dx10HeaderBytes = 148;

        private const uint PfAlphaPixels = 0x1;
        private const uint PfFourCc = 0x4;
        private const uint PfRgb = 0x40;
        private const uint PfLuminance = 0x20000;
        private const uint HeaderFlagsMipCount = 0x20000;

        // ------------------------------------------------------------------ header

        /// <summary>
        /// Parse the fixed part of a DDS header (plus the DX10 extension when the FourCC says so).
        /// Returns false for anything that is not a plain 2D DDS this fix can reason about - a wrong
        /// magic, a truncated buffer, nonsense dimensions, a texture array or a 3D volume.
        /// </summary>
        internal static bool TryParse(byte[] buf, int length, out DdsInfo info)
        {
            info = default(DdsInfo);
            if (buf == null || length < BaseHeaderBytes) return false;
            if (BitConverter.ToUInt32(buf, 0) != Magic) return false;
            if (BitConverter.ToUInt32(buf, 4) != 124) return false;

            uint flags = BitConverter.ToUInt32(buf, 8);
            int height = BitConverter.ToInt32(buf, 12);
            int width = BitConverter.ToInt32(buf, 16);
            if (width <= 0 || height <= 0 || width > 32768 || height > 32768) return false;

            int mips = (flags & HeaderFlagsMipCount) != 0 ? (int)BitConverter.ToUInt32(buf, 28) : 1;
            if (mips <= 0) mips = 1;
            if (mips > 32) return false;

            if (BitConverter.ToUInt32(buf, 76) != 32) return false;   // DDS_PIXELFORMAT.dwSize
            uint pfFlags = BitConverter.ToUInt32(buf, 80);
            string fourCc = Encoding.ASCII.GetString(buf, 84, 4).TrimEnd('\0', ' ');

            bool dx10 = (pfFlags & PfFourCc) != 0 && fourCc == "DX10";
            if (dx10 && length < Dx10HeaderBytes) return false;

            uint dxgi = 0;
            bool bgra = false;
            DdsCodec codec;
            if (dx10)
            {
                dxgi = BitConverter.ToUInt32(buf, 128);
                uint resourceDimension = BitConverter.ToUInt32(buf, 132);
                uint arraySize = BitConverter.ToUInt32(buf, 136);
                if (arraySize > 1) return false;                       // texture arrays are out of scope
                if (resourceDimension == 4) return false;               // 3D volume
                codec = MapDxgi(dxgi);
                bgra = dxgi == 87 || dxgi == 91;                        // B8G8R8A8_UNORM(_SRGB)
            }
            else if ((pfFlags & PfFourCc) != 0)
            {
                codec = MapFourCc(fourCc);
            }
            else if ((pfFlags & (PfRgb | PfLuminance)) != 0)
            {
                codec = DdsCodec.Uncompressed;
            }
            else
            {
                codec = DdsCodec.Unknown;
            }

            info.Valid = true;
            info.Width = width;
            info.Height = height;
            info.MipCount = mips;
            info.DataOffset = dx10 ? Dx10HeaderBytes : BaseHeaderBytes;
            info.Compressed = (pfFlags & PfFourCc) != 0;
            info.FourCc = (pfFlags & PfFourCc) != 0 ? fourCc : "";
            info.Dx10 = dx10;
            info.Dxgi = dxgi;
            info.Bgra = bgra;
            info.Codec = codec;
            return true;
        }

        /// <summary>
        /// The DXGI format word, which the shipped loader never reads. Values follow dxgiformat.h
        /// exactly - the previous session's fixture called DXGI 80 "BC5", but 80 is BC4_UNORM (one
        /// byte per texel: the fixture is 10936 bytes for a 128x128 8-mip chain, i.e. single channel);
        /// BC5_UNORM is 83. Both map correctly below, so a real BC5 file lands on BC5.
        /// </summary>
        internal static DdsCodec MapDxgi(uint dxgi)
        {
            switch (dxgi)
            {
                case 70: case 71: case 72: return DdsCodec.Bc1;     // BC1 typeless/unorm/unorm_srgb
                case 73: case 74: case 75: return DdsCodec.Bc2;
                case 76: case 77: case 78: return DdsCodec.Bc3;
                case 79: case 80: case 81: return DdsCodec.Bc4;
                case 82: case 83: case 84: return DdsCodec.Bc5;
                case 94: case 95: case 96: return DdsCodec.Bc6H;
                case 97: case 98: case 99: return DdsCodec.Bc7;
                case 28: case 29:                                  // R8G8B8A8_UNORM(_SRGB)
                case 87: case 91: return DdsCodec.Uncompressed;     // B8G8R8A8_UNORM(_SRGB)
                default: return DdsCodec.Unknown;
            }
        }

        /// <summary>Legacy FourCCs. "DXT1"/"DXT5" are the two the loader already handles.</summary>
        internal static DdsCodec MapFourCc(string fourCc)
        {
            switch ((fourCc ?? "").TrimEnd('\0', ' '))
            {
                case "DXT1": return DdsCodec.Bc1;
                case "DXT2": return DdsCodec.Bc2;      // premultiplied DXT3: same blocks
                case "DXT3": return DdsCodec.Bc2;
                case "DXT4": return DdsCodec.Bc3;      // premultiplied DXT5: same blocks
                case "DXT5": return DdsCodec.Bc3;
                case "ATI1": case "BC4U": case "BC4S": return DdsCodec.Bc4;
                case "ATI2": case "BC5U": case "BC5S": return DdsCodec.Bc5;
                default: return DdsCodec.Unknown;
            }
        }

        /// <summary>
        /// True when the shipped loader either throws on this file (DXT3/DXT2/DXT4/BC4/BC5 behind a
        /// legacy FourCC) or silently files it under the wrong format (any DX10 header that is not
        /// BC7, and DX10 uncompressed data, which is then read as if it were BC7 blocks).
        /// </summary>
        internal static bool NeedsRepair(in DdsInfo info)
        {
            if (!info.Valid) return false;
            switch (info.Codec)
            {
                case DdsCodec.Bc7: return false;                       // exactly what the loader assumes
                case DdsCodec.Uncompressed: return info.Dx10;           // plain RGBA/BGR files are fine
                case DdsCodec.Bc1: return info.Dx10 || !IsFourCc(info, "DXT1");
                case DdsCodec.Bc3: return info.Dx10 || !IsFourCc(info, "DXT5");   // DXT4 throws
                case DdsCodec.Unknown: return false;                    // nothing we could do anyway
                default: return true;                                   // Bc2, Bc4, Bc5, Bc6H
            }
        }

        private static bool IsFourCc(in DdsInfo info, string expected)
        {
            return string.Equals((info.FourCc ?? "").TrimEnd('\0', ' '), expected, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when any BC1 block in the file uses the three-colour + punch-through mode (c0 &lt;= c1).
        ///
        /// Such a block encodes 1-bit transparency, and the port uploads DXT1 as
        /// VkFormat.Bc1RGBUNormBlock (TexturePixelData.cs:694-695), which Vulkan defines as "alpha is
        /// always 1" - the CPU decoder (BcBlockDecoder.cs:625) honours the transparent index, so
        /// GetPixels32 shows holes that the screen does not: they are drawn as opaque white/black.
        /// The only way to keep them is to stop uploading the file as DXT1, i.e. decode it to RGBA32.
        /// </summary>
        internal static bool Bc1HasPunchThrough(byte[] data, int offset, int width, int height, int mipCount)
        {
            if (data == null || offset < 0 || (long)data.Length - offset < ChainBytes(DdsCodec.Bc1, width, height, mipCount))
                return false;

            int at = offset;
            for (int level = 0; level < Math.Max(1, mipCount); level++)
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

        /// <summary>
        /// The TextureFormat name the shipped loader would pick for this header - printed in the log so
        /// "dxgi=80 loader=BC7" shows the bug being repaired rather than asserting it.
        /// </summary>
        internal static string LoaderFormatName(in DdsInfo info)
        {
            if (!info.Valid) return "?";
            if (info.Dx10) return "BC7";                 // ToTextureFormat: any DX10 -> BC7
            if (!info.Compressed) return "uncompressed";  // RGB/BGR 888, 565 and 4444 are handled
            switch ((info.FourCc ?? "").TrimEnd('\0', ' '))
            {
                case "DXT1": return "DXT1";
                case "DXT5": return "DXT5";
                default: return "throw";                 // ToTextureFormat throws NotSupportedException (DXT2/3/4, ATI1/2, BC4U/5U)
            }
        }

        // ------------------------------------------------------------------ sizes

        /// <summary>Bytes per 4x4 block, or 0 when the codec is not block compressed.</summary>
        internal static int BlockBytes(DdsCodec codec)
        {
            switch (codec)
            {
                case DdsCodec.Bc1:
                case DdsCodec.Bc4: return 8;
                case DdsCodec.Bc2:
                case DdsCodec.Bc3:
                case DdsCodec.Bc5:
                case DdsCodec.Bc6H:
                case DdsCodec.Bc7: return 16;
                default: return 0;
            }
        }

        /// <summary>Bytes one mip level occupies in the file.</summary>
        internal static int MipBytes(DdsCodec codec, int width, int height)
        {
            if (codec == DdsCodec.Uncompressed) return width * height * 4;
            int block = BlockBytes(codec);
            if (block == 0) return 0;
            int bw = (width + 3) / 4;
            int bh = (height + 3) / 4;
            return bw * bh * block;
        }

        /// <summary>Bytes the whole mip chain occupies - the truncation check before decoding.</summary>
        internal static long ChainBytes(DdsCodec codec, int width, int height, int mipCount)
        {
            if (codec != DdsCodec.Uncompressed && BlockBytes(codec) == 0) return 0;
            long total = 0;
            for (int i = 0; i < Math.Max(1, mipCount); i++)
            {
                int w = Math.Max(1, width >> i);
                int h = Math.Max(1, height >> i);
                total += MipBytes(codec, w, h);
                if (w == 1 && h == 1) break;
            }
            return total;
        }

        /// <summary>
        /// How many levels a full chain has for this size: 1 + floor(log2(max(w, h))). A 128x128 art
        /// asset gets 8, a 256x256 gets 9 - the same count CalculateMipCount produces.
        /// </summary>
        internal static int MipChainLength(int width, int height)
        {
            int size = Math.Max(width, height);
            int levels = 1;
            while (size > 1) { size >>= 1; levels++; }
            return levels;
        }

        /// <summary>
        /// Box-filter one level down (2x2 average, edges clamped). Pure byte math on RGBA32 in file
        /// row order, so the tests can assert a hand-computed average.
        ///
        /// This exists because the engine builds `max(MipCount, uploads.Length)` levels and only writes
        /// the uploaded ones, while GenerateMipmaps refuses block-compressed formats
        /// (Texture2D.NewFileTexture promotes a one-level file to CalculateMipCount, UploadLevels leaves
        /// the rest empty). A mod that ships a single-mip DDS therefore samples fully transparent levels
        /// as soon as it is drawn smaller than 1:1 - the icon-size case. Whenever the source has one
        /// level we generate the chain here instead of letting the engine leave holes.
        /// </summary>
        internal static byte[] Downsample(byte[] rgba, int width, int height, out int outWidth, out int outHeight)
        {
            outWidth = Math.Max(1, width / 2);
            outHeight = Math.Max(1, height / 2);
            if (rgba == null || rgba.Length < width * height * 4) return null;

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

        // ------------------------------------------------------------------ decode

        /// <summary>
        /// Decode a whole mip chain into RGBA32 byte arrays, one per level, in file row order (row 0 is
        /// the top of the image, exactly as the block order in the file defines it). Returns null when
        /// the codec cannot be decoded here or the buffer is too short - the caller then leaves the
        /// loader's own result alone.
        /// </summary>
        internal static byte[][] DecodeMipChain(DdsCodec codec, byte[] data, int offset,
                                                int width, int height, int mipCount)
        {
            if (data == null || BlockBytes(codec) == 0) return null;
            int levels = Math.Max(1, mipCount);
            long need = ChainBytes(codec, width, height, levels);
            if (offset < 0 || need <= 0 || (long)data.Length - offset < need) return null;

            var result = new byte[levels][];
            int at = offset;
            for (int i = 0; i < levels; i++)
            {
                int w = Math.Max(1, width >> i);
                int h = Math.Max(1, height >> i);
                int bytes = MipBytes(codec, w, h);
                byte[] level = Decode(codec, data, at, w, h);
                if (level == null) return null;
                result[i] = level;
                at += bytes;
                if (w == 1 && h == 1 && i + 1 < levels)
                {
                    for (int j = i + 1; j < levels; j++) result[j] = result[i];
                    break;
                }
            }
            return result;
        }

        /// <summary>Decode one mip level to RGBA32 (width * height * 4 bytes).</summary>
        internal static byte[] Decode(DdsCodec codec, byte[] data, int offset, int width, int height)
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
                    int off = offset + (by * bw + bx) * block;
                    switch (codec)
                    {
                        case DdsCodec.Bc1:
                            DecodeColourBlock(data, off, dst, width, height, bx * 4, by * 4, false, true);
                            break;
                        case DdsCodec.Bc4:
                            DecodeAlphaBlock(data, off, alpha);
                            WriteSingleChannel(dst, width, height, bx * 4, by * 4, alpha);
                            break;
                        case DdsCodec.Bc5:
                            DecodeAlphaBlock(data, off, alpha);
                            WriteChannel(dst, width, height, bx * 4, by * 4, alpha, 0);
                            DecodeAlphaBlock(data, off + 8, alpha);
                            WriteChannel(dst, width, height, bx * 4, by * 4, alpha, 1);
                            WriteChannel(dst, width, height, bx * 4, by * 4, Opaque, 3);
                            break;
                        case DdsCodec.Bc2:
                            DecodeFourBitAlpha(data, off, alpha);
                            DecodeColourBlock(data, off + 8, dst, width, height, bx * 4, by * 4, true, false);
                            WriteAlpha(dst, width, height, bx * 4, by * 4, alpha);
                            break;
                        case DdsCodec.Bc3:
                            DecodeAlphaBlock(data, off, alpha);
                            DecodeColourBlock(data, off + 8, dst, width, height, bx * 4, by * 4, true, false);
                            WriteAlpha(dst, width, height, bx * 4, by * 4, alpha);
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
        /// One DXT1-style colour block. DXT3/DXT5 always use the four colour mode (their alpha lives in
        /// a separate block), so the caller passes fourColourOnly: true; plain BC1 keeps the
        /// c0 &lt;= c1 three-colour mode with its transparent index.
        /// </summary>
        private static void DecodeColourBlock(byte[] src, int off, byte[] dst, int width, int height,
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
        /// indices. a0 &gt; a1 selects the eight-value ramp, otherwise the six-value ramp plus 0 and 255.
        /// </summary>
        private static void DecodeAlphaBlock(byte[] src, int off, byte[] values)
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

        /// <summary>DXT3 alpha: sixteen raw 4-bit values, expanded by 17 so 0xF reaches 255.</summary>
        private static void DecodeFourBitAlpha(byte[] src, int off, byte[] values)
        {
            for (int i = 0; i < 16; i++)
            {
                int nibble = (src[off + (i / 2)] >> ((i % 2) * 4)) & 0xF;
                values[i] = (byte)(nibble * 17);
            }
        }

        private static void WriteSingleChannel(byte[] dst, int width, int height, int originX, int originY, byte[] values)
        {
            for (int y = 0; y < 4; y++)
            {
                int py = originY + y;
                if (py >= height) break;
                for (int x = 0; x < 4; x++)
                {
                    int px = originX + x;
                    if (px >= width) break;
                    byte v = values[y * 4 + x];
                    int d = (py * width + px) * 4;
                    dst[d] = v; dst[d + 1] = v; dst[d + 2] = v; dst[d + 3] = 255;
                }
            }
        }

        private static void WriteChannel(byte[] dst, int width, int height, int originX, int originY, byte[] values, int channel)
        {
            for (int y = 0; y < 4; y++)
            {
                int py = originY + y;
                if (py >= height) break;
                for (int x = 0; x < 4; x++)
                {
                    int px = originX + x;
                    if (px >= width) break;
                    int d = (py * width + px) * 4 + channel;
                    dst[d] = values[y * 4 + x];
                }
            }
        }

        private static readonly byte[] Opaque = Filled(255);

        private static byte[] Filled(byte v)
        {
            var a = new byte[16];
            for (int i = 0; i < a.Length; i++) a[i] = v;
            return a;
        }

        private static void WriteAlpha(byte[] dst, int width, int height, int originX, int originY, byte[] values)
        {
            WriteChannel(dst, width, height, originX, originY, values, 3);
        }
    }
}
