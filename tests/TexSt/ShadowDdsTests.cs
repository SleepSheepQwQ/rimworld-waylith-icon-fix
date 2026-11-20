using System;
using Xunit;

namespace AgentTouchFix.Tests
{
    /// <summary>
    /// The shadowing decision, asserted from headers built by hand.
    ///
    /// This is the rule that decides whether a loose .png loses to a same-named .dds, so it is
    /// deliberately conservative: only the formats Verse.ModDdsLoader actually maps may shadow. The
    /// cases below are the ones that cost real art in the shipped game - DXT3 (throws in
    /// ToTextureFormat, so the png behind it is gone and the square is blank), DXT4/ATI2/BC4U (same
    /// throw), a truncated download, and a header that is not a DDS at all - plus the formats that must
    /// keep working: DXT1, DXT5, every DX10 header (the DXGI word is another module's job), and the
    /// uncompressed RGB/RGBA layouts.
    /// </summary>
    public class ShadowDdsTests
    {
        // ------------------------------------------------------------------ header building

        private const uint PfAlphaPixels = 0x1;
        private const uint PfFourCc = 0x4;
        private const uint PfRgb = 0x40;

        /// <summary>
        /// A DDS header as a writer would emit it. The buffer is 148 bytes (the DX10 length) plus
        /// <paramref name="payload"/> bytes, so a caller can cut it short or give it real blocks.
        /// </summary>
        private static byte[] Header(string fourCc, int width = 8, int height = 8, int mips = 1,
                                     uint pfFlags = PfFourCc, uint pfSize = 32, uint headerSize = 124,
                                     uint magic = 0x20534444u, uint rgbBits = 0,
                                     uint rMask = 0, uint gMask = 0, uint bMask = 0, uint aMask = 0,
                                     uint dxgi = 0, int payload = 64)
        {
            var b = new byte[148 + payload];
            void U32(int at, uint v) => BitConverter.GetBytes(v).CopyTo(b, at);

            U32(0, magic);
            U32(4, headerSize);
            U32(8, 0x1007u | 0x80000u | (mips > 1 ? 0x20000u : 0u));   // caps|height|width|pf|linearsize|mipcount
            U32(12, (uint)height);
            U32(16, (uint)width);
            U32(20, (uint)(Math.Max(1, width) * Math.Max(1, height) / 4));
            U32(28, (uint)Math.Max(1, mips));
            U32(76, pfSize);
            U32(80, pfFlags);
            if ((pfFlags & PfFourCc) != 0)
                for (int i = 0; i < 4 && i < fourCc.Length; i++) b[84 + i] = (byte)fourCc[i];
            U32(88, rgbBits);
            U32(92, rMask);
            U32(96, gMask);
            U32(100, bMask);
            U32(104, aMask);
            U32(108, 0x1000);                                          // DDSCAPS_TEXTURE
            if (fourCc == "DX10")
            {
                U32(128, dxgi);
                U32(132, 3);                                           // DDS_DIMENSION_TEXTURE2D
                U32(136, 1);                                           // array size
            }
            return b;
        }

        private static ShadowDdsHead Parse(byte[] buf, int length = -1)
        {
            ShadowDdsHead h;
            Assert.True(ShadowDds.TryRead(buf, length < 0 ? buf.Length : length, out h),
                        "header should parse");
            return h;
        }

        private static void AssertDecodable(bool expected, byte[] buf, int length = -1)
        {
            int n = length < 0 ? buf.Length : length;
            ShadowDdsHead h;
            // A header that does not parse at all (bad magic, short buffer, wrong dwSize) is "cannot be
            // decoded" by definition; only a parsed header carries the flag under test.
            if (ShadowDds.TryRead(buf, n, out h)) Assert.Equal(expected, h.Decodable);
            else Assert.False(expected, "the header does not parse, so it cannot be decodable");
            Assert.Equal(expected, ShadowDds.Decodable(buf, n));
        }

        // ------------------------------------------------------------------ the whitelist

        [Fact]
        public void Dxt1IsDecodable()
        {
            AssertDecodable(true, Header("DXT1"));
        }

        [Fact]
        public void Dxt5IsDecodable()
        {
            AssertDecodable(true, Header("DXT5"));
        }

        [Theory]
        [InlineData(70u)]    // BC1
        [InlineData(77u)]    // BC3
        [InlineData(80u)]    // BC4
        [InlineData(83u)]    // BC5 - the mask format the shipped packs use
        [InlineData(98u)]    // BC7
        [InlineData(10u)]    // an unknown/unmapped DXGI word
        public void AnyDx10HeaderIsDecodable(uint dxgi)
        {
            // The DXGI word is not read here on purpose: DdsFormatFix maps it, and this guard must not
            // second-guess a format another module already repairs.
            AssertDecodable(true, Header("DX10", dxgi: dxgi));
        }

        [Fact]
        public void Dx10IsJudgedByItsOwnHeaderLength()
        {
            byte[] full = Header("DX10", dxgi: 83);
            AssertDecodable(true, full, 148);
            Assert.False(ShadowDds.Decodable(full, 140), "a DX10 header without its DXGI word is truncated");
        }

        // ------------------------------------------------------------------ the rejects

        [Fact]
        public void Dxt3IsNotDecodable()
        {
            // ToTextureFormat knows DXT1, DXT5 and DX10 only; DXT3 throws NotSupportedException and
            // LoadItem turns that into BadTex. This is the "Shadow.dds" case.
            AssertDecodable(false, Header("DXT3", payload: 64));
        }

        [Theory]
        [InlineData("DXT2")]
        [InlineData("DXT4")]
        [InlineData("ATI1")]
        [InlineData("ATI2")]
        [InlineData("BC4U")]
        [InlineData("BC5U")]
        public void OtherLegacyFourCcAreNotDecodable(string fourCc)
        {
            AssertDecodable(false, Header(fourCc));
        }

        [Fact]
        public void BadMagicIsNotDecodable()
        {
            AssertDecodable(false, Header("DXT1", magic: 0x2053444Fu));
        }

        [Fact]
        public void TruncatedHeaderIsNotDecodable()
        {
            byte[] dxt1 = Header("DXT1");
            Assert.False(ShadowDds.TryRead(dxt1, 100, out _), "100 bytes cannot hold a DDS header");
            Assert.False(ShadowDds.Decodable(dxt1, 100));

            byte[] dxt5 = Header("DXT5");
            Assert.False(ShadowDds.Decodable(dxt5, 0));
            Assert.False(ShadowDds.Decodable(null, 0));
        }

        [Fact]
        public void WrongHeaderOrPixelFormatSizeIsNotDecodable()
        {
            AssertDecodable(false, Header("DXT1", headerSize: 123));
            AssertDecodable(false, Header("DXT1", pfSize: 31));
        }

        [Fact]
        public void ZeroDimensionsAreNotDecodable()
        {
            AssertDecodable(false, Header("DXT1", width: 0, height: 8));
        }

        // ------------------------------------------------------------------ uncompressed

        [Fact]
        public void UncompressedRgba8888IsDecodable()
        {
            AssertDecodable(true, Header("", pfFlags: PfRgb | PfAlphaPixels, rgbBits: 32,
                                         rMask: 0x000000FFu, gMask: 0x0000FF00u,
                                         bMask: 0x00FF0000u, aMask: 0xFF000000u));
        }

        [Fact]
        public void UncompressedRgb888IsDecodable()
        {
            AssertDecodable(true, Header("", pfFlags: PfRgb, rgbBits: 24,
                                         rMask: 0x00FF0000u, gMask: 0x0000FF00u, bMask: 0x000000FFu));
        }

        [Fact]
        public void UncompressedRgb565IsDecodable()
        {
            AssertDecodable(true, Header("", pfFlags: PfRgb, rgbBits: 16,
                                         rMask: 0xF800u, gMask: 0x07E0u, bMask: 0x001Fu));
        }

        [Fact]
        public void UncompressedRgba4444NeedsItsAlphaFlag()
        {
            AssertDecodable(true, Header("", pfFlags: PfRgb | PfAlphaPixels, rgbBits: 16,
                                         rMask: 0x0F00u, gMask: 0x00F0u, bMask: 0x000Fu, aMask: 0xF000u));
            // ToTextureFormat throws for this mask set without DDPF_ALPHAPIXELS.
            AssertDecodable(false, Header("", pfFlags: PfRgb, rgbBits: 16,
                                          rMask: 0x0F00u, gMask: 0x00F0u, bMask: 0x000Fu, aMask: 0xF000u));
        }

        [Fact]
        public void UnmappedRgbMasksAreNotDecodable()
        {
            // R10G10B10A2: RGB flag set, but no IsRgb888/565/4444 branch matches, so the loader throws.
            AssertDecodable(false, Header("", pfFlags: PfRgb | PfAlphaPixels, rgbBits: 32,
                                          rMask: 0x3FFu, gMask: 0xFFC00u, bMask: 0x3FF00000u, aMask: 0xC0000000u));
        }

        // ------------------------------------------------------------------ payload length

        [Fact]
        public void PayloadCompleteAcceptsAFullChain()
        {
            // 8x8 DXT1, one level: 4 blocks * 8 bytes = 32, after a 128-byte header.
            ShadowDdsHead h = Parse(Header("DXT1"));
            Assert.Equal(128, h.DataOffset);
            Assert.True(ShadowDds.PayloadComplete(h, 128 + 32));
            Assert.True(ShadowDds.PayloadComplete(h, 128 + 4096), "trailing data is not a problem");
            Assert.False(ShadowDds.PayloadComplete(h, 128 + 16), "half a mip level is not a texture");
        }

        [Fact]
        public void PayloadCompleteWalksTheWholeChain()
        {
            // 8x8 DXT5 with three levels: 4*16 + 1*16 + 1*16 = 96 bytes.
            ShadowDdsHead h = Parse(Header("DXT5", mips: 3));
            Assert.Equal(3, h.MipCount);
            Assert.True(ShadowDds.PayloadComplete(h, 128 + 96));
            Assert.False(ShadowDds.PayloadComplete(h, 128 + 95));
        }

        [Fact]
        public void PayloadOfAnUnreadableHeaderIsNeverComplete()
        {
            ShadowDdsHead h;
            Assert.False(ShadowDds.TryRead(new byte[10], 10, out h), "10 bytes are not a header");
            Assert.False(ShadowDds.PayloadComplete(h, 1024));
        }

        // ------------------------------------------------------------------ labels

        [Fact]
        public void DescribeNamesTheFormatForTheLog()
        {
            Assert.Equal("dxt3", ShadowDds.Describe(Parse(Header("DXT3"))));
            Assert.Equal("dx10/dxgi=83", ShadowDds.Describe(Parse(Header("DX10", dxgi: 83))));
            Assert.Contains("uncompressed", ShadowDds.Describe(Parse(Header("", pfFlags: PfRgb, rgbBits: 32,
                                                                   rMask: 0x00FF0000u, gMask: 0x0000FF00u,
                                                                   bMask: 0x000000FFu))));
            Assert.Equal("unreadable header", ShadowDds.Describe(default(ShadowDdsHead)));
        }
    }
}
