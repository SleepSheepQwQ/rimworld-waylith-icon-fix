using System;
using Xunit;

namespace AgentTouchFix.Tests
{
    /// <summary>
    /// The DDS side of the fix, asserted against blocks whose decoded values are computed by hand.
    ///
    /// Three things are being pinned down:
    ///   1. the header decision - a DX10 header must be read for its DXGI word instead of being
    ///      labelled BC7 (the shipped loader's IsBc7 compares the FourCC against 'DX10', so a BC4 or
    ///      BC5 file is created as BC7 and only fails later inside GetPixels32);
    ///   2. the block decoders - BC4/BC5 (one and two channel), DXT3 (4-bit alpha + DXT1 colour);
    ///   3. the repair predicate - only the formats the loader throws on or mis-files are touched.
    /// </summary>
    public class DdsFormatTests
    {
        // ------------------------------------------------------------------ header building

        private static byte[] Header(string fourCc, int width, int height, int mips,
                                     uint dxgi = 0, uint pfFlags = 0x4, uint rgbBits = 0)
        {
            var b = new byte[DdsFormat.Dx10HeaderBytes + 64];
            void U32(int at, uint v) => BitConverter.GetBytes(v).CopyTo(b, at);

            U32(0, DdsFormat.Magic);
            U32(4, 124);
            U32(8, 0x21007u | (mips > 1 ? 0x20000u : 0u));      // caps|height|width|pixelformat|mipcount
            U32(12, (uint)height);
            U32(16, (uint)width);
            U32(76, 32);                                        // DDS_PIXELFORMAT.dwSize
            U32(80, pfFlags);
            if ((pfFlags & 0x4) != 0)
                for (int i = 0; i < 4; i++) b[84 + i] = (byte)fourCc[i];
            U32(88, rgbBits);
            U32(108, 0x1000);
            if (mips > 1) U32(28, (uint)mips);
            if (fourCc == "DX10")
            {
                U32(128, dxgi);
                U32(132, 3);        // DDS_DIMENSION_TEXTURE2D
                U32(136, 1);        // array size
            }
            return b;
        }

        private static DdsInfo Parse(byte[] buf)
        {
            DdsInfo info;
            Assert.True(DdsFormat.TryParse(buf, buf.Length, out info), "header should parse");
            return info;
        }

        // ------------------------------------------------------------------ the decision table

        [Theory]
        // BC1
        [InlineData(70u, (int)DdsCodec.Bc1)]
        [InlineData(71u, (int)DdsCodec.Bc1)]
        [InlineData(72u, (int)DdsCodec.Bc1)]
        // BC2 (DXT3)
        [InlineData(73u, (int)DdsCodec.Bc2)]
        [InlineData(74u, (int)DdsCodec.Bc2)]
        [InlineData(75u, (int)DdsCodec.Bc2)]
        // BC3 (DXT5)
        [InlineData(76u, (int)DdsCodec.Bc3)]
        [InlineData(77u, (int)DdsCodec.Bc3)]
        [InlineData(78u, (int)DdsCodec.Bc3)]
        // BC4 - 80 is BC4_UNORM, the value the bc5.dds fixture carries
        [InlineData(79u, (int)DdsCodec.Bc4)]
        [InlineData(80u, (int)DdsCodec.Bc4)]
        [InlineData(81u, (int)DdsCodec.Bc4)]
        // BC5 - 83 is BC5_UNORM
        [InlineData(82u, (int)DdsCodec.Bc5)]
        [InlineData(83u, (int)DdsCodec.Bc5)]
        [InlineData(84u, (int)DdsCodec.Bc5)]
        // BC6H / BC7
        [InlineData(94u, (int)DdsCodec.Bc6H)]
        [InlineData(95u, (int)DdsCodec.Bc6H)]
        [InlineData(96u, (int)DdsCodec.Bc6H)]
        [InlineData(97u, (int)DdsCodec.Bc7)]
        [InlineData(98u, (int)DdsCodec.Bc7)]
        [InlineData(99u, (int)DdsCodec.Bc7)]
        // uncompressed DX10 payloads
        [InlineData(28u, (int)DdsCodec.Uncompressed)]
        [InlineData(29u, (int)DdsCodec.Uncompressed)]
        [InlineData(87u, (int)DdsCodec.Uncompressed)]
        [InlineData(91u, (int)DdsCodec.Uncompressed)]
        // not a block format we know
        [InlineData(0u, (int)DdsCodec.Unknown)]
        [InlineData(10u, (int)DdsCodec.Unknown)]
        [InlineData(12u, (int)DdsCodec.Unknown)]
        [InlineData(999u, (int)DdsCodec.Unknown)]
        public void DxgiWordDecidesTheCodec(uint dxgi, int expected)
        {
            Assert.Equal((DdsCodec)expected, DdsFormat.MapDxgi(dxgi));
            Assert.Equal((DdsCodec)expected, Parse(Header("DX10", 8, 8, 1, dxgi)).Codec);
        }

        [Theory]
        [InlineData("DXT1", (int)DdsCodec.Bc1)]
        [InlineData("DXT2", (int)DdsCodec.Bc2)]
        [InlineData("DXT3", (int)DdsCodec.Bc2)]
        [InlineData("DXT4", (int)DdsCodec.Bc3)]
        [InlineData("DXT5", (int)DdsCodec.Bc3)]
        [InlineData("ATI1", (int)DdsCodec.Bc4)]
        [InlineData("BC4U", (int)DdsCodec.Bc4)]
        [InlineData("ATI2", (int)DdsCodec.Bc5)]
        [InlineData("BC5U", (int)DdsCodec.Bc5)]
        [InlineData("ABCD", (int)DdsCodec.Unknown)]
        public void LegacyFourCcsMapToTheirBlockFormat(string fourCc, int expected)
        {
            Assert.Equal((DdsCodec)expected, DdsFormat.MapFourCc(fourCc));
            DdsInfo info = Parse(Header(fourCc, 8, 8, 1));
            Assert.Equal((DdsCodec)expected, info.Codec);
            Assert.False(info.Dx10);
            Assert.Equal(DdsFormat.BaseHeaderBytes, info.DataOffset);
        }

        [Fact]
        public void Dx10HeaderShiftsThePayloadAndIsNeverCalledBc7Blindly()
        {
            DdsInfo bc5 = Parse(Header("DX10", 128, 128, 8, 83));
            Assert.True(bc5.Dx10);
            Assert.Equal(83u, bc5.Dxgi);
            Assert.Equal(DdsCodec.Bc5, bc5.Codec);
            Assert.Equal(DdsFormat.Dx10HeaderBytes, bc5.DataOffset);
            Assert.True(DdsFormat.NeedsRepair(bc5));
            // the loader would have picked BC7 for this file - that is the bug being repaired
            Assert.Equal("BC7", DdsFormat.LoaderFormatName(bc5));

            DdsInfo bc4 = Parse(Header("DX10", 128, 128, 8, 80));
            Assert.Equal(DdsCodec.Bc4, bc4.Codec);
            Assert.True(DdsFormat.NeedsRepair(bc4));

            DdsInfo bc7 = Parse(Header("DX10", 128, 128, 8, 98));
            Assert.Equal(DdsCodec.Bc7, bc7.Codec);
            Assert.False(DdsFormat.NeedsRepair(bc7));      // the loader is right, leave it alone
        }

        [Fact]
        public void OnlyTheFormatsTheLoaderGetsWrongAreRepaired()
        {
            // throws in ToTextureFormat -> BadTex today
            Assert.True(DdsFormat.NeedsRepair(Parse(Header("DXT3", 8, 8, 1))));
            Assert.True(DdsFormat.NeedsRepair(Parse(Header("DXT2", 8, 8, 1))));
            Assert.True(DdsFormat.NeedsRepair(Parse(Header("DXT4", 8, 8, 1))));
            Assert.True(DdsFormat.NeedsRepair(Parse(Header("ATI1", 8, 8, 1))));
            Assert.True(DdsFormat.NeedsRepair(Parse(Header("ATI2", 8, 8, 1))));
            Assert.True(DdsFormat.NeedsRepair(Parse(Header("DX10", 8, 8, 1, 95))));   // BC6H

            // handled natively: mapping untouched
            Assert.False(DdsFormat.NeedsRepair(Parse(Header("DXT1", 8, 8, 1))));
            Assert.False(DdsFormat.NeedsRepair(Parse(Header("DXT5", 8, 8, 1))));
            Assert.False(DdsFormat.NeedsRepair(Parse(Header("DX10", 8, 8, 1, 98))));  // BC7
            Assert.False(DdsFormat.NeedsRepair(Parse(Header("", 8, 8, 1, 0, 0x41, 32))));

            // ... but the same formats behind a DX10 header are mis-filed as BC7 and must be repaired
            Assert.True(DdsFormat.NeedsRepair(Parse(Header("DX10", 8, 8, 1, 71))));  // BC1
            Assert.True(DdsFormat.NeedsRepair(Parse(Header("DX10", 8, 8, 1, 77))));  // BC3
            Assert.True(DdsFormat.NeedsRepair(Parse(Header("DX10", 8, 8, 1, 28))));  // RGBA8
        }

        [Fact]
        public void ParseRejectsWhatItCannotHandle()
        {
            DdsInfo info;
            Assert.False(DdsFormat.TryParse(null, 0, out info));

            byte[] badMagic = Header("DXT3", 8, 8, 1);
            badMagic[0] = (byte)'X';
            Assert.False(DdsFormat.TryParse(badMagic, badMagic.Length, out info));

            // truncated: shorter than the 128-byte fixed header
            byte[] shortBuf = Header("DXT3", 8, 8, 1);
            Assert.False(DdsFormat.TryParse(shortBuf, 100, out info));

            // zero width
            byte[] zero = Header("DXT3", 8, 8, 1);
            BitConverter.GetBytes(0).CopyTo(zero, 16);
            Assert.False(DdsFormat.TryParse(zero, zero.Length, out info));

            // a texture array is out of scope
            byte[] array = Header("DX10", 8, 8, 1, 98);
            BitConverter.GetBytes(4u).CopyTo(array, 136);
            Assert.False(DdsFormat.TryParse(array, array.Length, out info));
        }

        [Fact]
        public void MipChainSizesFollowTheBlockGrid()
        {
            // 8x8 BC1 = 4 blocks of 8 bytes; 128x128 BC7 = 16 bytes per block
            Assert.Equal(32, DdsFormat.MipBytes(DdsCodec.Bc1, 8, 8));
            Assert.Equal(16384, DdsFormat.MipBytes(DdsCodec.Bc7, 128, 128));
            Assert.Equal(32, DdsFormat.MipBytes(DdsCodec.Bc4, 8, 8));
            Assert.Equal(64, DdsFormat.MipBytes(DdsCodec.Bc2, 8, 8));

            // 2x2 is one block, not zero
            Assert.Equal(8, DdsFormat.MipBytes(DdsCodec.Bc1, 2, 2));

            // 8x8 with 4 levels: 32 + 8 + 8 + 8 (2x2, 1x1 stay one block each)
            Assert.Equal(56, DdsFormat.ChainBytes(DdsCodec.Bc1, 8, 8, 4));

            // A partial block still occupies a whole block: 2x2 and 1x1 both take 8 bytes here, which
            // is why these two totals are the exact payload sizes of the CI fixtures (ddsfix/bc5.dds is
            // 10936 bytes after its 148-byte header, ddsfix/bc7.dds 21872).
            Assert.Equal(10936, DdsFormat.ChainBytes(DdsCodec.Bc4, 128, 128, 8));
            Assert.Equal(21872, DdsFormat.ChainBytes(DdsCodec.Bc7, 128, 128, 8));
        }

        [Fact]
        public void ATruncatedChainIsRefusedInsteadOfDecodedPastTheEnd()
        {
            byte[] six = new byte[6];                     // one 8x8 BC4 block needs 8 bytes
            Assert.Null(DdsFormat.Decode(DdsCodec.Bc4, six, 0, 8, 8));
            Assert.Null(DdsFormat.DecodeMipChain(DdsCodec.Bc4, six, 0, 8, 8, 1));
        }

        // ------------------------------------------------------------------ BC4

        /// <summary>Builds one BC4/BC5/alpha block: two endpoints and 16 three-bit indices.</summary>
        private static byte[] AlphaBlock(byte a0, byte a1, params byte[] perTexel)
        {
            Assert.Equal(16, perTexel.Length);
            var b = new byte[8];
            b[0] = a0;
            b[1] = a1;
            ulong bits = 0;
            for (int i = 0; i < 16; i++) bits |= (ulong)perTexel[i] << (3 * i);
            for (int i = 0; i < 6; i++) b[2 + i] = (byte)(bits >> (8 * i));
            return b;
        }

        private static readonly byte[] Zero16 = new byte[16];

        [Fact]
        public void Bc4EndpointsBecomeGreyPixels()
        {
            // a0 > a1 selects the eight-value ramp; every index 0 -> a0
            byte[] rgba = DdsFormat.Decode(DdsCodec.Bc4, AlphaBlock(200, 100, Zero16), 0, 4, 4);
            Assert.NotNull(rgba);
            for (int i = 0; i < 16; i++)
            {
                Assert.Equal(200, rgba[i * 4 + 0]);
                Assert.Equal(200, rgba[i * 4 + 1]);
                Assert.Equal(200, rgba[i * 4 + 2]);
                Assert.Equal(255, rgba[i * 4 + 3]);       // single channel art is opaque
            }
        }

        [Fact]
        public void Bc4UsesTheEightValueRampWhenTheEndpointsDescend()
        {
            // a0=200, a1=100: (7-i)*200 + i*100, integer division by 7
            byte[] ramp = { 200, 100, 185, 171, 157, 142, 128, 114 };
            for (byte idx = 0; idx < 8; idx++)
            {
                var per = new byte[16];
                for (int i = 0; i < 16; i++) per[i] = idx;
                byte[] rgba = DdsFormat.Decode(DdsCodec.Bc4, AlphaBlock(200, 100, per), 0, 4, 4);
                Assert.Equal(ramp[idx], rgba[0]);
                Assert.Equal(ramp[idx], rgba[1 * 4 + 1]);
                Assert.Equal(ramp[idx], rgba[15 * 4 + 2]);
            }
        }

        [Fact]
        public void Bc4UsesTheSixValueRampPlusBlackAndWhiteWhenTheEndpointsAscend()
        {
            // a0=10, a1=20 (a0 <= a1): 10, 20, (5-i)*10+i*20)/5 -> 12,14,16,18, then 0 and 255
            byte[] ramp = { 10, 20, 12, 14, 16, 18, 0, 255 };
            for (byte idx = 0; idx < 8; idx++)
            {
                var per = new byte[16];
                for (int i = 0; i < 16; i++) per[i] = idx;
                byte[] rgba = DdsFormat.Decode(DdsCodec.Bc4, AlphaBlock(10, 20, per), 0, 4, 4);
                Assert.Equal(ramp[idx], rgba[0]);
            }
        }

        [Fact]
        public void Bc4ClipsBlocksAtNonMultipleOfFourSizes()
        {
            // 5x3 image: 2 blocks across, 1 down; the second block's right column is out of bounds
            byte[] data = new byte[16];
            AlphaBlock(77, 77, Zero16).CopyTo(data, 0);
            AlphaBlock(77, 77, Zero16).CopyTo(data, 8);
            byte[] rgba = DdsFormat.Decode(DdsCodec.Bc4, data, 0, 5, 3);
            Assert.NotNull(rgba);
            Assert.Equal(5 * 3 * 4, rgba.Length);
            for (int i = 0; i < 5 * 3; i++) Assert.Equal(77, rgba[i * 4]);
        }

        // ------------------------------------------------------------------ BC5

        [Fact]
        public void Bc5KeepsBothChannelsAndPutsThemInRedAndGreen()
        {
            var data = new byte[16];
            AlphaBlock(10, 10, Zero16).CopyTo(data, 0);       // R = 10
            AlphaBlock(250, 250, Zero16).CopyTo(data, 8);     // G = 250

            byte[] rgba = DdsFormat.Decode(DdsCodec.Bc5, data, 0, 4, 4);
            Assert.NotNull(rgba);
            for (int i = 0; i < 16; i++)
            {
                Assert.Equal(10, rgba[i * 4 + 0]);
                Assert.Equal(250, rgba[i * 4 + 1]);
                Assert.Equal(0, rgba[i * 4 + 2]);
                Assert.Equal(255, rgba[i * 4 + 3]);
            }
        }

        [Fact]
        public void Bc5FeedsTheSecondBlockIntoGreenNotTheFirst()
        {
            var data = new byte[16];
            AlphaBlock(1, 1, Zero16).CopyTo(data, 0);
            AlphaBlock(2, 2, Zero16).CopyTo(data, 8);
            byte[] rgba = DdsFormat.Decode(DdsCodec.Bc5, data, 0, 4, 4);
            Assert.Equal(1, rgba[0]);
            Assert.Equal(2, rgba[1]);
        }

        // ------------------------------------------------------------------ DXT3

        /// <summary>One DXT1 colour block: two RGB565 endpoints and 16 two-bit indices.</summary>
        private static byte[] ColourBlock(ushort c0, ushort c1, byte index)
        {
            uint bits = 0;
            for (int i = 0; i < 16; i++) bits |= (uint)(index & 3) << (2 * i);
            return ColourBlock(c0, c1, bits);
        }

        private static byte[] ColourBlock(ushort c0, ushort c1, uint bits)
        {
            var b = new byte[8];
            BitConverter.GetBytes(c0).CopyTo(b, 0);
            BitConverter.GetBytes(c1).CopyTo(b, 2);
            BitConverter.GetBytes(bits).CopyTo(b, 4);
            return b;
        }

        [Fact]
        public void Dxt3CombinesTheFourBitAlphaBlockWithTheColourBlock()
        {
            var data = new byte[16];
            // alpha nibbles: texel 0 (low nibble of byte 0) = F, texel 1 = 8, texel 2 = F, texel 3 = 0
            data[0] = 0x8F;
            data[1] = 0x0F;      // texel 2 = F, texel 3 = 0
            for (int i = 2; i < 8; i++) data[i] = 0xFF;
            // colour: pure red then pure blue, four-colour mode (c0 > c1), index 0 = red
            ColourBlock(0xF800, 0x001F, 0).CopyTo(data, 8);

            byte[] rgba = DdsFormat.Decode(DdsCodec.Bc2, data, 0, 4, 4);
            Assert.NotNull(rgba);
            // texel 0: alpha 0xF -> 255, colour red
            Assert.Equal(255, rgba[0]);
            Assert.Equal(0, rgba[1]);
            Assert.Equal(0, rgba[2]);
            Assert.Equal(255, rgba[3]);
            // texel 1: alpha 0x8 -> 136
            Assert.Equal(136, rgba[4 + 3]);
            // texel 2: alpha 0xF
            Assert.Equal(255, rgba[8 + 3]);
            // texel 3: alpha 0x0
            Assert.Equal(0, rgba[12 + 3]);
        }

        [Fact]
        public void Dxt3AlphaNibblesExpandToFullRange()
        {
            Assert.Equal(0, DecodeDxt3Alpha(0x0));
            Assert.Equal(17, DecodeDxt3Alpha(0x1));
            Assert.Equal(136, DecodeDxt3Alpha(0x8));
            Assert.Equal(238, DecodeDxt3Alpha(0xE));
            Assert.Equal(255, DecodeDxt3Alpha(0xF));
        }

        private static byte DecodeDxt3Alpha(byte nibble)
        {
            var data = new byte[16];
            data[0] = nibble;                            // texel 0 in the low nibble
            ColourBlock(0xFFFF, 0x0000, 0).CopyTo(data, 8);
            byte[] rgba = DdsFormat.Decode(DdsCodec.Bc2, data, 0, 4, 4);
            return rgba[3];
        }

        [Fact]
        public void Dxt3KeepsFourColourModeEvenWhenTheEndpointsAscend()
        {
            // DXT1 would switch to three-colour mode here and make index 3 transparent black; DXT3
            // carries its alpha separately, so the colour block stays four-colour.
            var data = new byte[16];
            for (int i = 0; i < 8; i++) data[i] = 0xFF;          // alpha = 255 everywhere
            ColourBlock(0x001F, 0xF800, 3).CopyTo(data, 8);      // c0 < c1, index 3
            byte[] rgba = DdsFormat.Decode(DdsCodec.Bc2, data, 0, 4, 4);

            // four-colour index 3 = (c0 + 2*c1)/3 -> mostly red
            Assert.Equal(170, rgba[0]);
            Assert.Equal(0, rgba[1]);
            Assert.Equal(85, rgba[2]);
            Assert.Equal(255, rgba[3]);
        }

        [Fact]
        public void Dxt3EveryTexelIndexPicksItsOwnAlphaNibble()
        {
            var data = new byte[16];
            for (int i = 0; i < 8; i++) data[i] = 0x10;           // nibbles: 0 then 0x1 per byte
            ColourBlock(0xF800, 0x001F, 0).CopyTo(data, 8);
            byte[] rgba = DdsFormat.Decode(DdsCodec.Bc2, data, 0, 4, 4);
            for (int i = 0; i < 16; i++)
                Assert.Equal(i % 2 == 0 ? 0 : 17, rgba[i * 4 + 3]);
        }

        // ------------------------------------------------------------------ DXT1 1-bit alpha

        [Fact]
        public void Bc1PunchThroughBecomesTransparentBlack()
        {
            // c0 = 0x001F (blue), c1 = 0xFFFF (white); c0 <= c1 selects the three-colour mode in which
            // the fourth index is transparent black. Indices 0xC0C0C0C0 put that index on the last
            // texel of every row: 4 holes in a 4x4 block, 16 in an 8x8 (4 blocks) fixture.
            byte[] block = ColourBlock(0x001F, 0xFFFF, 0xC0C0C0C0u);
            byte[] rgba = DdsFormat.Decode(DdsCodec.Bc1, block, 0, 4, 4);
            Assert.NotNull(rgba);

            for (int y = 0; y < 4; y++)
            {
                int hole = (y * 4 + 3) * 4;
                Assert.Equal(0, rgba[hole + 0]);
                Assert.Equal(0, rgba[hole + 1]);
                Assert.Equal(0, rgba[hole + 2]);
                Assert.Equal(0, rgba[hole + 3]);

                int solid = (y * 4) * 4;
                Assert.Equal(255, rgba[solid + 3]);      // index 0 -> c0, opaque blue
                Assert.Equal(0, rgba[solid + 0]);
                Assert.Equal(255, rgba[solid + 2]);
            }
        }

        [Fact]
        public void Bc1PunchThroughIsDetectedSoTheFileIsNotUploadedAsDxt1()
        {
            byte[] punching = ColourBlock(0x001F, 0xFFFF, 0xC0C0C0C0u);
            Assert.True(DdsFormat.Bc1HasPunchThrough(punching, 0, 4, 4, 1));

            byte[] opaque = ColourBlock(0xF800, 0x001F, 0);
            Assert.False(DdsFormat.Bc1HasPunchThrough(opaque, 0, 4, 4, 1));

            // the transparent block can sit in a later mip: 4x4 then 2x2
            var chain = new byte[16];
            opaque.CopyTo(chain, 0);
            punching.CopyTo(chain, 8);
            Assert.True(DdsFormat.Bc1HasPunchThrough(chain, 0, 4, 4, 2));

            // a short buffer is answered with false, never read past the end
            Assert.False(DdsFormat.Bc1HasPunchThrough(new byte[4], 0, 4, 4, 1));
        }

        // ------------------------------------------------------------------ DXT5 (BC3)

        [Fact]
        public void Dxt5AlphaInterpolatesAndSetsTransparency()
        {
            var data = new byte[16];
            AlphaBlock(255, 0, Zero16).CopyTo(data, 0);            // a0 > a1 -> 0..255 ramp, index 0 = 255
            ColourBlock(0xF800, 0x001F, 0).CopyTo(data, 8);
            byte[] rgba = DdsFormat.Decode(DdsCodec.Bc3, data, 0, 4, 4);
            Assert.Equal(255, rgba[3]);
            Assert.Equal(255, rgba[0]);

            AlphaBlock(0, 255, Zero16).CopyTo(data, 0);            // a0 <= a1 -> six-value ramp, index 0 = 0
            rgba = DdsFormat.Decode(DdsCodec.Bc3, data, 0, 4, 4);
            Assert.Equal(0, rgba[3]);
        }

        // ------------------------------------------------------------------ whole-file walk

        [Fact]
        public void DecodeMipChainWalksEveryLevelInOrder()
        {
            // 8x8 BC4 with two levels: level 0 = 4 blocks of 200, level 1 (4x4) = 1 block of 50
            var data = new byte[8 * 5];
            for (int i = 0; i < 4; i++) AlphaBlock(200, 200, Zero16).CopyTo(data, i * 8);
            AlphaBlock(50, 50, Zero16).CopyTo(data, 32);

            byte[][] mips = DdsFormat.DecodeMipChain(DdsCodec.Bc4, data, 0, 8, 8, 2);
            Assert.NotNull(mips);
            Assert.Equal(2, mips.Length);
            Assert.Equal(8 * 8 * 4, mips[0].Length);
            Assert.Equal(4 * 4 * 4, mips[1].Length);
            Assert.Equal(200, mips[0][0]);
            Assert.Equal(50, mips[1][0]);
        }
    }
}
