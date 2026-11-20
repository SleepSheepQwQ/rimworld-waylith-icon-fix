using System;
using System.Text;

namespace AgentTouchFix
{
    /// <summary>One DDS header, reduced to what the shadowing decision needs to know.</summary>
    internal struct ShadowDdsHead
    {
        internal bool Valid;
        internal bool Dx10;
        internal bool Compressed;
        internal int Width;
        internal int Height;
        internal int MipCount;
        internal int DataOffset;
        internal uint Dxgi;
        internal uint PfFlags;
        internal uint RgbBits;
        internal uint RMask, GMask, BMask, AMask;
        internal string FourCc;
        /// <summary>The loader (or the DX10 repair module) can turn this file into a texture.</summary>
        internal bool Decodable;
    }

    /// <summary>
    /// Decides whether a loose .dds file is worth losing a same-named .png to, from its header alone.
    ///
    /// This is deliberately NARROWER than Verse.DdsPixelFormat.ToTextureFormat's own table. The
    /// question here is not "can anything in this mod decode it" but "would the shipped loader accept
    /// it": a DXT3 file is rejected by the loader (NotSupportedException -> BaseContent.BadTex), and
    /// the fact that a later repair module could decode it does not make the .dds a safe replacement
    /// for a .png that is known to work. So the shadow guard accepts only what the stock loader maps
    /// without help, plus every DX10 header - those carry their format in the DXGI word, whose handling
    /// is another module's job (see DdsFormatFix), and refusing them here would break BC7/BC5 art that
    /// works today.
    ///
    /// The rules mirror ModDdsLoader.TryLoadDds: magic 0x20534444, dwSize 124, DDS_PIXELFORMAT.dwSize
    /// 32, then either a legacy FourCC of DXT1/DXT5, a DX10 header, or an uncompressed layout
    /// ToTextureFormat can name (RGB/BGR 888, RGB 565, RGBA 4444 with alpha, and the luminance/alpha
    /// branch it maps to Alpha8). Everything else - DXT3, DXT2, DXT4, ATI1, ATI2, BC4U, BC5U, unknown
    /// masks, a bad magic, a short header - is "cannot be decoded" and must not shadow anything.
    ///
    /// No UnityEngine type appears here: tests/TexSt compiles this exact file and asserts it against
    /// headers built by hand.
    /// </summary>
    internal static class ShadowDds
    {
        internal const uint Magic = 0x20534444;   // "DDS "
        internal const int BaseHeaderBytes = 128;
        internal const int Dx10HeaderBytes = 148;

        private const int OffFlags = 8, OffHeight = 12, OffWidth = 16, OffMipCount = 28;
        private const int OffPfSize = 76, OffPfFlags = 80, OffFourCc = 84, OffRgbBits = 88;
        private const int OffRMask = 92, OffGMask = 96, OffBMask = 100, OffAMask = 104, OffDxgi = 128;

        private const uint PfAlphaPixels = 0x1, PfAlpha = 0x2, PfFourCc = 0x4, PfRgb = 0x40;
        private const uint PfLuminance = 0x20000, FlagsMipCount = 0x20000;

        /// <summary>
        /// Read a header out of <paramref name="head"/>, of which <paramref name="length"/> bytes are
        /// real. Returns false when the buffer is too short to be a DDS header at all; a header that
        /// parses but names an unsupported format comes back Valid with Decodable == false.
        /// </summary>
        internal static bool TryRead(byte[] head, int length, out ShadowDdsHead h)
        {
            h = default(ShadowDdsHead);
            if (head == null || length < BaseHeaderBytes) return false;
            if (BitConverter.ToUInt32(head, 0) != Magic) return false;
            if (BitConverter.ToUInt32(head, 4) != 124) return false;              // dwSize
            if (BitConverter.ToUInt32(head, OffPfSize) != 32) return false;       // DDS_PIXELFORMAT.dwSize

            int height = BitConverter.ToInt32(head, OffHeight);
            int width = BitConverter.ToInt32(head, OffWidth);
            if (width <= 0 || height <= 0 || width > 32768 || height > 32768) return false;

            uint flags = BitConverter.ToUInt32(head, OffFlags);
            int mips = (flags & FlagsMipCount) != 0 ? (int)BitConverter.ToUInt32(head, OffMipCount) : 1;
            if (mips <= 0) mips = 1;
            if (mips > 32) return false;

            uint pfFlags = BitConverter.ToUInt32(head, OffPfFlags);
            bool compressed = (pfFlags & PfFourCc) != 0;
            string fourCc = compressed ? Encoding.ASCII.GetString(head, OffFourCc, 4).TrimEnd('\0', ' ') : "";
            bool dx10 = compressed && string.Equals(fourCc, "DX10", StringComparison.Ordinal);
            if (dx10 && length < Dx10HeaderBytes) return false;                   // the DXGI word is not all there

            h.Valid = true;
            h.Dx10 = dx10;
            h.Compressed = compressed;
            h.Width = width;
            h.Height = height;
            h.MipCount = mips;
            h.DataOffset = dx10 ? Dx10HeaderBytes : BaseHeaderBytes;
            h.FourCc = fourCc;
            h.PfFlags = pfFlags;
            h.RgbBits = BitConverter.ToUInt32(head, OffRgbBits);
            h.RMask = BitConverter.ToUInt32(head, OffRMask);
            h.GMask = BitConverter.ToUInt32(head, OffGMask);
            h.BMask = BitConverter.ToUInt32(head, OffBMask);
            h.AMask = BitConverter.ToUInt32(head, OffAMask);
            h.Dxgi = dx10 ? BitConverter.ToUInt32(head, OffDxgi) : 0u;
            h.Decodable = Decide(h);
            return true;
        }

        /// <summary>Header-only answer: true when the stock loader can build a texture from this file.</summary>
        internal static bool Decodable(byte[] head, int length)
        {
            ShadowDdsHead h;
            return TryRead(head, length, out h) && h.Decodable;
        }

        private static bool Decide(in ShadowDdsHead h)
        {
            // Any DX10 header: its real format is the DXGI word, and DdsFormatFix maps it. Judging it
            // here would either duplicate that table or reject art that loads correctly today.
            if (h.Dx10) return true;

            if (!h.Compressed)
            {
                if ((h.PfFlags & PfRgb) != 0) return RgbFamily(h);
                // ToTextureFormat maps Luminance/Alpha-only layouts to Alpha8 through the same
                // LoadRawTextureData path, so those decode as well.
                if ((h.PfFlags & (PfLuminance | PfAlpha)) != 0) return true;
                return false;
            }

            // Exactly the two legacy FourCCs the loader knows. DXT4 shares the DXT5 blocks but is
            // rejected by the name check (ToTextureFormat throws), so it must not shadow a png.
            if (string.Equals(h.FourCc, "DXT1", StringComparison.Ordinal)) return true;
            if (string.Equals(h.FourCc, "DXT5", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>The uncompressed layouts DdsPixelFormat.ToTextureFormat can name (and nothing else).</summary>
        private static bool RgbFamily(in ShadowDdsHead h)
        {
            bool alphaPixels = (h.PfFlags & PfAlphaPixels) != 0;

            // IsRgb888 / IsBgr888: 24- or 32-bit, alpha flag only picks RGB24 vs RGBA32.
            if (h.RMask == 0x00FF0000u && h.GMask == 0x0000FF00u && h.BMask == 0x000000FFu) return true;
            if (h.RMask == 0x000000FFu && h.GMask == 0x0000FF00u && h.BMask == 0x00FF0000u) return true;

            // IsRgb565
            if (h.RMask == 0xF800u && h.GMask == 0x07E0u && h.BMask == 0x001Fu) return true;

            // IsRgba4444 - the loader throws for this mask set without the alpha flag.
            if (h.RMask == 0x0F00u && h.GMask == 0x00F0u && h.BMask == 0x000Fu &&
                h.AMask == 0xF000u && alphaPixels) return true;

            return false;
        }

        /// <summary>
        /// Bytes per 4x4 block for the legacy FourCCs, 0 when the file is not one of them.
        /// </summary>
        internal static int BlockBytes(string fourCc)
        {
            if (string.Equals(fourCc, "DXT1", StringComparison.Ordinal)) return 8;
            if (string.Equals(fourCc, "DXT5", StringComparison.Ordinal)) return 16;
            return 0;
        }

        /// <summary>
        /// True when the file is at least as long as the mip chain its own header describes.
        ///
        /// A header can be perfect and the payload still cut short (an interrupted copy, a "texture
        /// pack" that only shipped mip 0). The loader uploads whatever span it is handed, so such a
        /// file throws or produces garbage - it cannot be called decodable. Only the legacy block
        /// formats are checked: for DX10 the block size belongs to the DXGI table the repair module
        /// owns, and for uncompressed data the byte count depends on masks, so neither is second-guessed
        /// here.
        /// </summary>
        internal static bool PayloadComplete(in ShadowDdsHead h, long fileLength)
        {
            if (!h.Valid) return false;
            if (h.Dx10 || !h.Compressed) return true;

            int block = BlockBytes(h.FourCc);
            if (block == 0) return false;

            long need = 0;
            int w = h.Width, hh = h.Height;
            for (int level = 0; level < h.MipCount; level++)
            {
                need += (long)(((w + 3) / 4) * ((hh + 3) / 4)) * block;
                if (w == 1 && hh == 1) break;
                w = Math.Max(1, w >> 1);
                hh = Math.Max(1, hh >> 1);
            }
            return fileLength - h.DataOffset >= need;
        }

        /// <summary>Short label for the log: "dxt1", "dx10/dxgi=83", "rgba8888", "unreadable".</summary>
        internal static string Describe(in ShadowDdsHead h)
        {
            if (!h.Valid) return "unreadable header";
            if (h.Dx10) return "dx10/dxgi=" + h.Dxgi;
            if (!h.Compressed) return "uncompressed " + h.RgbBits + "bpp";
            return h.FourCc.Length == 0 ? "fourcc?" : h.FourCc.ToLowerInvariant();
        }
    }
}
