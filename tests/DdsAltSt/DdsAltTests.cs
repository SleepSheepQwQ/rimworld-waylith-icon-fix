using System;
using AgentTouchFix;
using Xunit;

namespace DdsAltSt
{
    /// <summary>
    /// The pure half of DdsFormatFixAlt: the DXGI/FourCC tables, the header parser, the block
    /// decoders, the decision table and the mip-chain invariant. Everything is asserted against
    /// hand-computed blocks or hand-built headers - no fixture, no game assembly.
    /// </summary>
    public class DdsAltFormatTests
    {
        // ------------------------------------------------------------------ fixtures (headers)

        private static void U32(byte[] b, int at, uint v)
        {
            b[at] = (byte)(v & 0xFF);
            b[at + 1] = (byte)((v >> 8) & 0xFF);
            b[at + 2] = (byte)((v >> 16) & 0xFF);
            b[at + 3] = (byte)((v >> 24) & 0xFF);
        }

        /// <summary>A legacy (FourCC) header with room for the data that follows.</summary>
        private static byte[] LegacyHeader(string fourCc, int w, int h, int mips)
        {
            var b = new byte[128 + 256];
            U32(b, 0, DdsAltFormat.Magic);
            U32(b, 4, 124);
            U32(b, 8, mips > 1 ? 0xA1007u : 0x81007u);
            U32(b, 12, (uint)h);
            U32(b, 16, (uint)w);
            U32(b, 28, (uint)mips);
            U32(b, 76, 32);
            U32(b, 80, 0x4);                          // DDPF_FOURCC
            for (int i = 0; i < 4; i++) b[84 + i] = (byte)fourCc[i];
            return b;
        }

        /// <summary>A DX10 header (magic + 124 bytes + the 20-byte extension).</summary>
        private static byte[] Dx10Header(uint dxgi, int w, int h, int mips,
                                         uint arraySize = 1, uint dimension = 3)
        {
            var b = new byte[148 + 256];
            U32(b, 0, DdsAltFormat.Magic);
            U32(b, 4, 124);
            U32(b, 8, mips > 1 ? 0xA1007u : 0x81007u);
            U32(b, 12, (uint)h);
            U32(b, 16, (uint)w);
            U32(b, 28, (uint)mips);
            U32(b, 76, 32);
            U32(b, 80, 0x5);                          // DDPF_FOURCC | DDPF_ALPHAPIXELS
            b[84] = (byte)'D'; b[85] = (byte)'X'; b[86] = (byte)'1'; b[87] = (byte)'0';
            U32(b, 128, dxgi);
            U32(b, 132, dimension);                   // 2 = 1D, 3 = 2D, 4 = 3D
            U32(b, 136, arraySize);
            U32(b, 140, 0);
            return b;
        }

        /// <summary>An uncompressed RGB(A) header - the shape the loader already handles.</summary>
        private static byte[] RgbHeader(int w, int h)
        {
            var b = new byte[128 + 256];
            U32(b, 0, DdsAltFormat.Magic);
            U32(b, 4, 124);
            U32(b, 8, 0x81007u);
            U32(b, 12, (uint)h);
            U32(b, 16, (uint)w);
            U32(b, 76, 32);
            U32(b, 80, 0x41);                         // RGB | AlphaPixels
            U32(b, 88, 32);                           // RGBBitCount
            U32(b, 92, 0xFF);
            U32(b, 96, 0xFF00);
            U32(b, 100, 0xFF0000);
            U32(b, 104, 0xFF000000);
            return b;
        }

        private static byte[] Bc1Block(ushort c0, ushort c1, uint indices)
        {
            var b = new byte[8];
            b[0] = (byte)(c0 & 0xFF); b[1] = (byte)(c0 >> 8);
            b[2] = (byte)(c1 & 0xFF); b[3] = (byte)(c1 >> 8);
            b[4] = (byte)(indices & 0xFF);
            b[5] = (byte)((indices >> 8) & 0xFF);
            b[6] = (byte)((indices >> 16) & 0xFF);
            b[7] = (byte)((indices >> 24) & 0xFF);
            return b;
        }

        /// <summary>Pack sixteen 3-bit indices, LSB first, as a BC4/BC5/DXT5 alpha block.</summary>
        private static byte[] RampBlock(byte a0, byte a1, int[] indices)
        {
            var b = new byte[8];
            b[0] = a0;
            b[1] = a1;
            ulong bits = 0;
            for (int i = 0; i < 16; i++) bits |= (ulong)(indices[i] & 7) << (3 * i);
            for (int i = 0; i < 6; i++) b[2 + i] = (byte)((bits >> (8 * i)) & 0xFF);
            return b;
        }

        private static int[] Ramp(int start)
        {
            var v = new int[16];
            for (int i = 0; i < 16; i++) v[i] = (start + i) & 7;
            return v;
        }

        // ------------------------------------------------------------------ DXGI table

        [Theory]
        [InlineData(70u, (int)AltCodec.Bc1)]
        [InlineData(71u, (int)AltCodec.Bc1)]
        [InlineData(72u, (int)AltCodec.Bc1)]
        [InlineData(73u, (int)AltCodec.Bc2)]
        [InlineData(74u, (int)AltCodec.Bc2)]
        [InlineData(75u, (int)AltCodec.Bc2)]
        [InlineData(76u, (int)AltCodec.Bc3)]
        [InlineData(77u, (int)AltCodec.Bc3)]
        [InlineData(78u, (int)AltCodec.Bc3)]
        [InlineData(79u, (int)AltCodec.Bc4)]
        [InlineData(80u, (int)AltCodec.Bc4)]      // the CI fixture called bc5.dds is dxgi 80 = BC4_UNORM
        [InlineData(81u, (int)AltCodec.Bc4)]
        [InlineData(82u, (int)AltCodec.Bc5)]
        [InlineData(83u, (int)AltCodec.Bc5)]      // real BC5_UNORM
        [InlineData(84u, (int)AltCodec.Bc5)]
        [InlineData(94u, (int)AltCodec.Bc6H)]
        [InlineData(95u, (int)AltCodec.Bc6H)]
        [InlineData(96u, (int)AltCodec.Bc6H)]
        [InlineData(97u, (int)AltCodec.Bc7)]
        [InlineData(98u, (int)AltCodec.Bc7)]
        [InlineData(99u, (int)AltCodec.Bc7)]
        [InlineData(28u, (int)AltCodec.Rgba8)]
        [InlineData(29u, (int)AltCodec.Rgba8)]
        [InlineData(87u, (int)AltCodec.Rgba8)]
        [InlineData(91u, (int)AltCodec.Rgba8)]
        [InlineData(0u, (int)AltCodec.Unknown)]
        [InlineData(1u, (int)AltCodec.Unknown)]
        [InlineData(100u, (int)AltCodec.Unknown)]
        public void DxgiTable(uint dxgi, int codec)
        {
            // int in the signature because AltCodec is internal to the mod assembly (CS0051).
            Assert.Equal((AltCodec)codec, DdsAltFormat.MapDxgi(dxgi));
        }

        [Theory]
        [InlineData("DXT1", (int)AltCodec.Bc1)]
        [InlineData("DXT2", (int)AltCodec.Bc2)]   // premultiplied DXT3: same blocks
        [InlineData("DXT3", (int)AltCodec.Bc2)]
        [InlineData("DXT4", (int)AltCodec.Bc3)]   // premultiplied DXT5: same blocks
        [InlineData("DXT5", (int)AltCodec.Bc3)]
        [InlineData("ATI1", (int)AltCodec.Bc4)]
        [InlineData("BC4U", (int)AltCodec.Bc4)]
        [InlineData("ATI2", (int)AltCodec.Bc5)]
        [InlineData("BC5U", (int)AltCodec.Bc5)]
        [InlineData("DX10", (int)AltCodec.Unknown)]   // the extension decides, not the FourCC
        [InlineData("", (int)AltCodec.Unknown)]
        public void FourCcTable(string fourCc, int codec)
        {
            Assert.Equal((AltCodec)codec, DdsAltFormat.MapFourCc(fourCc));
        }

        [Fact]
        public void NativeNamesCoverTheTable()
        {
            Assert.Equal("DXT1", DdsAltFormat.NativeName(AltCodec.Bc1));
            Assert.Equal("DXT5", DdsAltFormat.NativeName(AltCodec.Bc3));
            Assert.Equal("BC4", DdsAltFormat.NativeName(AltCodec.Bc4));
            Assert.Equal("BC5", DdsAltFormat.NativeName(AltCodec.Bc5));
            Assert.Equal("BC7", DdsAltFormat.NativeName(AltCodec.Bc7));
            Assert.Null(DdsAltFormat.NativeName(AltCodec.Bc2));    // no DXT3 member exists in this port
            Assert.Null(DdsAltFormat.NativeName(AltCodec.Bc6H));
        }

        [Fact]
        public void BlockSizes()
        {
            Assert.Equal(8, DdsAltFormat.BlockBytes(AltCodec.Bc1));
            Assert.Equal(8, DdsAltFormat.BlockBytes(AltCodec.Bc4));
            Assert.Equal(16, DdsAltFormat.BlockBytes(AltCodec.Bc2));
            Assert.Equal(16, DdsAltFormat.BlockBytes(AltCodec.Bc3));
            Assert.Equal(16, DdsAltFormat.BlockBytes(AltCodec.Bc5));
            Assert.Equal(16, DdsAltFormat.BlockBytes(AltCodec.Bc7));
            Assert.Equal(0, DdsAltFormat.BlockBytes(AltCodec.Rgba8));
        }

        [Fact]
        public void ChainSizesMatchTheRealFixtures()
        {
            // bc5.dds: dxgi 80 (BC4), 128x128, 8 levels -> 10936 bytes of 8-byte blocks.
            Assert.Equal(10936L, DdsAltFormat.ChainBytes(AltCodec.Bc4, 128, 128, 8));
            // bc7.dds: dxgi 98, 128x128, 8 levels -> 21872 bytes of 16-byte blocks.
            Assert.Equal(21872L, DdsAltFormat.ChainBytes(AltCodec.Bc7, 128, 128, 8));
            // dxt3.dds: 8x8, one level -> four 16-byte blocks.
            Assert.Equal(64L, DdsAltFormat.ChainBytes(AltCodec.Bc2, 8, 8, 1));
            // RGBA32 chain of an 8x8 source: 256 + 64 + 16 + 4.
            Assert.Equal(340L, DdsAltFormat.RgbaChainBytes(8, 8, 4));
        }

        [Theory]
        [InlineData(1, 1, 1)]
        [InlineData(1, 8, 4)]
        [InlineData(8, 8, 4)]
        [InlineData(64, 64, 7)]
        [InlineData(128, 128, 8)]
        [InlineData(128, 64, 8)]
        [InlineData(256, 256, 9)]
        public void ChainLength(int w, int h, int expected)
        {
            Assert.Equal(expected, DdsAltFormat.MipChainLength(w, h));
        }

        // ------------------------------------------------------------------ header parsing

        [Fact]
        public void ParsesADx10Bc4Header()
        {
            byte[] buf = Dx10Header(80, 128, 128, 8);
            AltHeader h;
            Assert.True(DdsAltFormat.TryParse(buf, buf.Length, out h));
            Assert.True(h.Valid);
            Assert.True(h.Dx10);
            Assert.Equal(80u, h.Dxgi);
            Assert.Equal(AltCodec.Bc4, h.Codec);
            Assert.Equal(128, h.Width);
            Assert.Equal(128, h.Height);
            Assert.Equal(8, h.MipCount);
            Assert.True(h.Compressed);
            Assert.Equal(148, h.DataOffset);
            Assert.False(h.Bgra);
            Assert.Equal("BC7", DdsAltFormat.LoaderFormatName(h));   // the port calls every DX10 file BC7
        }

        [Fact]
        public void ParsesALegacyDxt3Header()
        {
            byte[] buf = LegacyHeader("DXT3", 8, 8, 1);
            AltHeader h;
            Assert.True(DdsAltFormat.TryParse(buf, buf.Length, out h));
            Assert.False(h.Dx10);
            Assert.Equal(AltCodec.Bc2, h.Codec);
            Assert.Equal(1, h.MipCount);
            Assert.Equal(128, h.DataOffset);
            Assert.Equal("throw", DdsAltFormat.LoaderFormatName(h));   // ToTextureFormat throws on DXT3
        }

        [Fact]
        public void ParsesAnUncompressedHeader()
        {
            byte[] buf = RgbHeader(64, 32);
            AltHeader h;
            Assert.True(DdsAltFormat.TryParse(buf, buf.Length, out h));
            Assert.Equal(AltCodec.Rgba8, h.Codec);
            Assert.False(h.Compressed);
            Assert.Equal("uncompressed", DdsAltFormat.LoaderFormatName(h));
        }

        [Fact]
        public void RejectsWhatItCannotReasonAbout()
        {
            AltHeader h;
            Assert.False(DdsAltFormat.TryParse(null, 0, out h));

            byte[] bad = Dx10Header(98, 64, 64, 8);
            bad[0] = (byte)'X';
            Assert.False(DdsAltFormat.TryParse(bad, bad.Length, out h));

            byte[] shortBuf = LegacyHeader("DXT1", 8, 8, 1);
            Assert.False(DdsAltFormat.TryParse(shortBuf, 64, out h));       // truncated

            byte[] array = Dx10Header(98, 64, 64, 8, arraySize: 4);
            Assert.False(DdsAltFormat.TryParse(array, array.Length, out h));

            byte[] volume = Dx10Header(98, 64, 64, 8, dimension: 4);
            Assert.False(DdsAltFormat.TryParse(volume, volume.Length, out h));

            byte[] zero = Dx10Header(98, 0, 64, 8);
            Assert.False(DdsAltFormat.TryParse(zero, zero.Length, out h));

            AltHeader untouched;
            Assert.False(DdsAltFormat.TryParse(LegacyHeader("DXT1", 8, 8, 1), 100, out untouched));
            Assert.False(untouched.Valid);
        }

        // ------------------------------------------------------------------ decision table

        private static AltHeader Header(byte[] buf)
        {
            AltHeader h;
            Assert.True(DdsAltFormat.TryParse(buf, buf.Length, out h));
            return h;
        }

        [Fact]
        public void Bc7IsLeftToTheLoader()
        {
            AltChoice c = DdsAltFormat.Decide(Header(Dx10Header(98, 128, 128, 8)), false, true);
            Assert.Equal(AltPlan.Skip, c.Plan);
        }

        [Fact]
        public void Bc4BehindDx10IsDecodedWhenThereIsNoNativeMember()
        {
            AltChoice c = DdsAltFormat.Decide(Header(Dx10Header(80, 128, 128, 8)), false, false);
            Assert.Equal(AltPlan.Managed, c.Plan);
            Assert.Equal(AltCodec.Bc4, c.Codec);
            Assert.Equal(8, c.Levels);
        }

        [Fact]
        public void Bc4GoesNativeWhenThePortExposesTheFormat()
        {
            AltChoice c = DdsAltFormat.Decide(Header(Dx10Header(80, 128, 128, 8)), false, true);
            Assert.Equal(AltPlan.Native, c.Plan);
            Assert.Equal("BC4", c.Format);
            Assert.Equal(8, c.Levels);
        }

        [Fact]
        public void Bc5BehindDx10IsRepaired()
        {
            AltChoice c = DdsAltFormat.Decide(Header(Dx10Header(83, 128, 128, 8)), false, false);
            Assert.Equal(AltPlan.Managed, c.Plan);
            Assert.Equal(AltCodec.Bc5, c.Codec);
        }

        [Fact]
        public void Dxt3AndDxt4AreDecoded()
        {
            Assert.Equal(AltPlan.Managed,
                DdsAltFormat.Decide(Header(LegacyHeader("DXT3", 128, 128, 1)), false, false).Plan);
            Assert.Equal(AltPlan.Managed,
                DdsAltFormat.Decide(Header(LegacyHeader("DXT2", 8, 8, 1)), false, false).Plan);
            Assert.Equal(AltPlan.Managed,
                DdsAltFormat.Decide(Header(LegacyHeader("DXT4", 8, 8, 1)), false, false).Plan);
            Assert.Equal(AltPlan.Managed,
                DdsAltFormat.Decide(Header(LegacyHeader("ATI1", 8, 8, 1)), false, false).Plan);
            Assert.Equal(AltPlan.Managed,
                DdsAltFormat.Decide(Header(LegacyHeader("ATI2", 8, 8, 1)), false, false).Plan);
        }

        [Fact]
        public void LegacyDxt1AndDxt5AreLeftAloneWhenTheChainIsComplete()
        {
            Assert.Equal(AltPlan.Skip,
                DdsAltFormat.Decide(Header(LegacyHeader("DXT1", 64, 64, 7)), false, true).Plan);
            Assert.Equal(AltPlan.Skip,
                DdsAltFormat.Decide(Header(LegacyHeader("DXT5", 64, 64, 7)), false, true).Plan);
        }

        [Fact]
        public void LegacyDxt1ShortChainIsCompleted()
        {
            // The loader creates as many levels as the header claims and uploads exactly that many;
            // a one-level 64x64 file therefore has to go through the CPU chain like any other.
            AltChoice c = DdsAltFormat.Decide(Header(LegacyHeader("DXT1", 64, 64, 1)), false, true);
            Assert.Equal(AltPlan.Managed, c.Plan);
            Assert.Equal(7, c.Levels);

            AltChoice d = DdsAltFormat.Decide(Header(LegacyHeader("DXT5", 64, 64, 1)), false, true);
            Assert.Equal(AltPlan.Managed, d.Plan);
            Assert.Equal(7, d.Levels);
        }

        [Fact]
        public void PunchThroughBc1IsDecodedEvenWhenTheLoaderHandlesDxt1()
        {
            AltChoice c = DdsAltFormat.Decide(Header(LegacyHeader("DXT1", 8, 8, 1)), true, true);
            Assert.Equal(AltPlan.Managed, c.Plan);
            Assert.Equal(4, c.Levels);      // 8x8 -> a full chain is 4 levels
        }

        [Fact]
        public void Dx10Bc1AndBc3PreferNative()
        {
            AltChoice b1 = DdsAltFormat.Decide(Header(Dx10Header(71, 64, 64, 7)), false, true);
            Assert.Equal(AltPlan.Native, b1.Plan);
            Assert.Equal("DXT1", b1.Format);

            AltChoice b3 = DdsAltFormat.Decide(Header(Dx10Header(77, 64, 64, 7)), false, true);
            Assert.Equal(AltPlan.Native, b3.Plan);
            Assert.Equal("DXT5", b3.Format);

            // Without a usable native format the same file is decoded instead.
            Assert.Equal(AltPlan.Managed,
                DdsAltFormat.Decide(Header(Dx10Header(77, 64, 64, 7)), false, false).Plan);
        }

        [Fact]
        public void Bc6hAndUnknownAreSkipped()
        {
            AltChoice bc6 = DdsAltFormat.Decide(Header(Dx10Header(95, 64, 64, 7)), false, true);
            Assert.Equal(AltPlan.Skip, bc6.Plan);
            Assert.Contains("bc6h", bc6.Why);

            AltChoice unk = DdsAltFormat.Decide(Header(Dx10Header(1234, 64, 64, 7)), false, true);
            Assert.Equal(AltPlan.Skip, unk.Plan);
        }

        [Fact]
        public void OneLevelSourcesGetAFullChain()
        {
            // 128x128 with one level: the engine builds 8 levels, so the payload must carry 8.
            AltChoice c = DdsAltFormat.Decide(Header(LegacyHeader("DXT3", 128, 128, 1)), false, false);
            Assert.Equal(AltPlan.Managed, c.Plan);
            Assert.Equal(8, c.Levels);

            // A partial chain (4 of 8) is completed the same way.
            AltChoice p = DdsAltFormat.Decide(Header(Dx10Header(80, 128, 128, 4)), false, false);
            Assert.Equal(AltPlan.Managed, p.Plan);
            Assert.Equal(8, p.Levels);

            // More levels than the dimensions allow are clamped.
            AltChoice q = DdsAltFormat.Decide(Header(LegacyHeader("DXT3", 8, 8, 8)), false, false);
            Assert.Equal(4, q.Levels);
        }

        [Theory]
        [InlineData(98u, 128, 128, 8, false)]   // BC7: never native (skipped)
        [InlineData(80u, 128, 128, 8, true)]    // BC4: native candidate
        [InlineData(83u, 128, 128, 8, true)]    // BC5: native candidate
        [InlineData(71u, 128, 128, 8, true)]    // DX10 BC1: native candidate
        [InlineData(77u, 128, 128, 8, true)]    // DX10 BC3: native candidate
        [InlineData(80u, 128, 128, 1, false)]   // one level: compressed levels cannot be generated
        public void NativeCandidates(uint dxgi, int w, int h, int mips, bool expected)
        {
            Assert.Equal(expected, DdsAltFormat.NativeCandidate(Header(Dx10Header(dxgi, w, h, mips))));
        }

        // ------------------------------------------------------------------ decoders

        [Fact]
        public void Bc1FourColourBlock()
        {
            // c0 = red, c1 = blue: index 2 is (2*c0 + c1)/3.
            byte[] block = Bc1Block(0xF800, 0x001F, 0x00000000);
            byte[] px = DdsAltFormat.Decode(AltCodec.Bc1, block, 0, 4, 4);
            Assert.NotNull(px);
            Assert.Equal((byte)255, px[0]);
            Assert.Equal((byte)0, px[1]);
            Assert.Equal((byte)0, px[2]);
            Assert.Equal((byte)255, px[3]);

            px = DdsAltFormat.Decode(AltCodec.Bc1, Bc1Block(0xF800, 0x001F, 0x55555555), 0, 4, 4);
            Assert.Equal((byte)0, px[0]);
            Assert.Equal((byte)255, px[2]);

            px = DdsAltFormat.Decode(AltCodec.Bc1, Bc1Block(0xF800, 0x001F, 0xAAAAAAAA), 0, 4, 4);
            Assert.Equal((byte)((2 * 255 + 0) / 3), px[0]);
            Assert.Equal((byte)((2 * 0 + 255) / 3), px[2]);
            Assert.Equal((byte)255, px[3]);
        }

        [Fact]
        public void Bc1PunchThroughKeepsTheHoles()
        {
            // c0 = blue, c1 = red, so c0 <= c1 selects the three-colour mode; index 3 is transparent.
            byte[] block = Bc1Block(0x001F, 0xF800, 0xFFFFFFFF);
            byte[] px = DdsAltFormat.Decode(AltCodec.Bc1, block, 0, 4, 4);
            Assert.Equal((byte)0, px[0]);
            Assert.Equal((byte)0, px[1]);
            Assert.Equal((byte)0, px[2]);
            Assert.Equal((byte)0, px[3]);

            // index 2 is the halfway colour in three-colour mode.
            px = DdsAltFormat.Decode(AltCodec.Bc1, Bc1Block(0x001F, 0xF800, 0xAAAAAAAA), 0, 4, 4);
            Assert.Equal((byte)127, px[0]);
            Assert.Equal((byte)127, px[2]);
            Assert.Equal((byte)255, px[3]);
        }

        [Fact]
        public void PunchThroughScanLooksAtEveryBlock()
        {
            var fourColour = new byte[64];
            for (int i = 0; i < 4; i++)
            {
                byte[] b = Bc1Block(0xF800, 0x001F, 0);
                Array.Copy(b, 0, fourColour, i * 8, 8);
            }
            Assert.False(DdsAltFormat.Bc1PunchThrough(fourColour, 0, 8, 8, 1));

            // The last of the four blocks uses the three-colour mode.
            Array.Copy(Bc1Block(0x001F, 0xF800, 0), 0, fourColour, 24, 8);
            Assert.True(DdsAltFormat.Bc1PunchThrough(fourColour, 0, 8, 8, 1));
        }

        [Fact]
        public void Bc4ExpandsItsSingleChannelIntoRed()
        {
            // a0 = 255, a1 = 0 selects the seven-step ramp: 255, 0, 218, 182, 145, 109, 72, 36.
            // The channel lands in R only - the port samples a Bc4UNormBlock as (R,0,0,1).
            byte[] block = RampBlock(255, 0, Ramp(0));

            byte[] px = DdsAltFormat.Decode(AltCodec.Bc4, block, 0, 4, 4);
            Assert.Equal((byte)255, px[0]);      // index 0
            Assert.Equal((byte)0, px[1]);
            Assert.Equal((byte)0, px[2]);
            Assert.Equal((byte)255, px[3]);
            Assert.Equal((byte)0, px[4]);        // index 1
            Assert.Equal((byte)218, px[8]);      // index 2
            Assert.Equal((byte)182, px[12]);     // index 3
            Assert.Equal((byte)145, px[16]);     // index 4
            Assert.Equal((byte)109, px[20]);     // index 5
            Assert.Equal((byte)72, px[24]);      // index 6
            Assert.Equal((byte)36, px[28]);      // index 7
            Assert.Equal((byte)0, px[9]);        // G stays 0
            Assert.Equal((byte)0, px[10]);       // B stays 0
        }

        [Fact]
        public void Bc4SixStepRampEndsInZeroAndWhite()
        {
            // a0 <= a1 selects the five-step ramp plus 0 and 255.
            byte[] block = RampBlock(10, 200, Ramp(0));
            byte[] px = DdsAltFormat.Decode(AltCodec.Bc4, block, 0, 4, 4);
            Assert.Equal((byte)10, px[0]);
            Assert.Equal((byte)200, px[4]);
            // index 2 = (4*10 + 1*200)/5 = 48
            Assert.Equal((byte)48, px[8]);
            // index 6 = 0, index 7 = 255
            Assert.Equal((byte)0, px[24]);
            Assert.Equal((byte)255, px[28]);
        }

        [Fact]
        public void Bc5TakesTwoChannelsAndLeavesBlueAtZero()
        {
            var block = new byte[16];
            Array.Copy(RampBlock(200, 100, Ramp(0)), 0, block, 0, 8);    // R: 200, 100, 185, ...
            Array.Copy(RampBlock(50, 40, Ramp(0)), 0, block, 8, 8);      // G: 50, 40, ...

            byte[] px = DdsAltFormat.Decode(AltCodec.Bc5, block, 0, 4, 4);
            Assert.Equal((byte)200, px[0]);
            Assert.Equal((byte)50, px[1]);
            Assert.Equal((byte)0, px[2]);
            Assert.Equal((byte)255, px[3]);
            Assert.Equal((byte)185, px[4]);      // (6*200 + 100)/7
            Assert.Equal((byte)48, px[5]);       // (6*50 + 40)/7 = 340/7 = 48
        }

        [Fact]
        public void Bc2CarriesFourBitAlpha()
        {
            var block = new byte[16];
            // alpha nibbles: texel 0 = 0xF, texel 1 = 0x8, texel 2 = 0x0, texel 3 = 0xF, rest 0xF
            block[0] = 0x8F;
            block[1] = 0xF0;
            for (int i = 2; i < 8; i++) block[i] = 0xFF;
            Array.Copy(Bc1Block(0xF800, 0x001F, 0), 0, block, 8, 8);

            byte[] px = DdsAltFormat.Decode(AltCodec.Bc2, block, 0, 4, 4);
            Assert.Equal((byte)255, px[3]);      // 0xF * 17
            Assert.Equal((byte)136, px[7]);      // 0x8 * 17
            Assert.Equal((byte)0, px[11]);       // 0x0
            Assert.Equal((byte)255, px[0]);      // colour from the DXT1 block
        }

        [Fact]
        public void Bc3AlwaysUsesTheFourColourMode()
        {
            var block = new byte[16];
            Array.Copy(RampBlock(255, 0, Ramp(0)), 0, block, 0, 8);
            // c0 (blue) < c1 (red): DXT3/DXT5 must still interpolate all four colours.
            Array.Copy(Bc1Block(0x001F, 0xF800, 0xAAAAAAAA), 0, block, 8, 8);

            byte[] px = DdsAltFormat.Decode(AltCodec.Bc3, block, 0, 4, 4);
            Assert.Equal((byte)85, px[0]);       // (2*0 + 255)/3
            Assert.Equal((byte)170, px[2]);      // (2*255 + 0)/3
            Assert.Equal((byte)255, px[3]);      // alpha from the ramp block, index 0
            Assert.Equal((byte)0, px[7]);        // alpha index 1
        }

        [Fact]
        public void DecodeRefusesShortBuffersAndUnknownCodecs()
        {
            Assert.Null(DdsAltFormat.Decode(AltCodec.Bc1, new byte[7], 0, 4, 4));
            Assert.Null(DdsAltFormat.Decode(AltCodec.Bc6H, new byte[64], 0, 4, 4));
            Assert.Null(DdsAltFormat.Decode(AltCodec.Bc7, new byte[64], 0, 4, 4));
            Assert.Null(DdsAltFormat.Decode(AltCodec.Bc1, new byte[8], 0, 0, 4));
        }

        // ------------------------------------------------------------------ chain

        [Fact]
        public void DownsampleAveragesTwoByTwo()
        {
            var src = new byte[2 * 2 * 4];
            src[0] = 0; src[4] = 100; src[8] = 200; src[12] = 255;
            src[3] = 255; src[7] = 255; src[11] = 255; src[15] = 255;

            int w, h;
            byte[] dst = DdsAltFormat.Downsample(src, 2, 2, out w, out h);
            Assert.NotNull(dst);
            Assert.Equal(1, w);
            Assert.Equal(1, h);
            Assert.Equal((byte)((0 + 100 + 200 + 255 + 2) / 4), dst[0]);
            Assert.Equal((byte)255, dst[3]);
        }

        [Fact]
        public void DownsampleRefusesOneByOne()
        {
            int w, h;
            Assert.Null(DdsAltFormat.Downsample(new byte[4], 1, 1, out w, out h));
        }

        [Fact]
        public void FitChainCompletesAndTrims()
        {
            // One 8x8 level, filled with a known ramp.
            var level0 = new byte[8 * 8 * 4];
            for (int i = 0; i < level0.Length; i += 4)
            {
                level0[i] = (byte)((i / 4) % 256);
                level0[i + 3] = 255;
            }

            byte[][] chain = DdsAltFormat.FitChain(new[] { level0 }, 8, 8, 4);
            Assert.NotNull(chain);
            Assert.Equal(4, chain.Length);
            Assert.Equal(256, chain[0].Length);
            Assert.Equal(64, chain[1].Length);
            Assert.Equal(16, chain[2].Length);
            Assert.Equal(4, chain[3].Length);
            Assert.Equal(340, DdsAltFormat.Join(chain).Length);

            // Level 1 is exactly the box filter of level 0.
            int w, h;
            byte[] expected = DdsAltFormat.Downsample(level0, 8, 8, out w, out h);
            Assert.Equal(expected, chain[1]);

            // Trimming keeps the first N levels.
            Assert.Equal(2, DdsAltFormat.FitChain(chain, 8, 8, 2).Length);
        }

        [Fact]
        public void FitChainStopsAtOneByOne()
        {
            var level0 = new byte[4];
            level0[3] = 255;
            // 1x1 has no lower level; asking for more must terminate with what exists.
            byte[][] chain = DdsAltFormat.FitChain(new[] { level0 }, 1, 1, 8);
            Assert.Single(chain);
        }

        [Fact]
        public void PayloadMustFillEveryLevel()
        {
            // Managed: 4 levels of an 8x8 texture is 340 bytes, and nothing else.
            Assert.True(DdsAltFormat.PayloadFits(AltPlan.Managed, AltCodec.Bc2, 8, 8, 4, 340));
            Assert.False(DdsAltFormat.PayloadFits(AltPlan.Managed, AltCodec.Bc2, 8, 8, 4, 256));
            Assert.False(DdsAltFormat.PayloadFits(AltPlan.Managed, AltCodec.Bc2, 8, 8, 0, 340));

            // Native: one DXT3 level of an 8x8 texture is 64 bytes.
            Assert.True(DdsAltFormat.PayloadFits(AltPlan.Native, AltCodec.Bc2, 8, 8, 1, 64));
            Assert.False(DdsAltFormat.PayloadFits(AltPlan.Native, AltCodec.Bc2, 8, 8, 1, 128));
        }

        /// <summary>
        /// End to end on the real shape of the CI defect: a 128x128 BC4 file with 8 levels (dxgi 80,
        /// 10936 bytes of blocks) must decode, complete to 8 levels and produce an RGBA32 payload of
        /// exactly 87380 bytes - the number of levels the texture is created with.
        /// </summary>
        [Fact]
        public void FullChainForARealBc4Shape()
        {
            byte[] file = Dx10Header(80, 128, 128, 8);
            Array.Resize(ref file, 148 + 10936);
            for (int i = 148; i < file.Length; i++) file[i] = (byte)((i * 7) & 0xFF);

            AltHeader h = Header(file);
            AltChoice c = DdsAltFormat.Decide(h, false, false);
            Assert.Equal(AltPlan.Managed, c.Plan);
            Assert.Equal(8, c.Levels);

            byte[][] decoded = DdsAltFormat.DecodeChain(c.Codec, file, h.DataOffset, h.Width, h.Height, c.Levels);
            Assert.NotNull(decoded);
            Assert.Equal(8, decoded.Length);

            byte[][] chain = DdsAltFormat.FitChain(decoded, h.Width, h.Height, c.Levels);
            byte[] payload = DdsAltFormat.Join(chain);
            Assert.Equal(8, chain.Length);
            Assert.Equal(87380, payload.Length);
            Assert.True(DdsAltFormat.PayloadFits(c.Plan, c.Codec, h.Width, h.Height, chain.Length, payload.Length));
        }

        /// <summary>The one-level case the icon-size defect is about.</summary>
        [Fact]
        public void OneLevelSourceProducesAFilledChain()
        {
            byte[] file = LegacyHeader("DXT3", 128, 128, 1);
            Array.Resize(ref file, 128 + 64);
            for (int i = 128; i < file.Length; i++) file[i] = 0xFF;   // opaque white blocks

            AltHeader h = Header(file);
            AltChoice c = DdsAltFormat.Decide(h, false, false);
            Assert.Equal(AltPlan.Managed, c.Plan);
            Assert.Equal(8, c.Levels);

            byte[][] decoded = DdsAltFormat.DecodeChain(c.Codec, file, h.DataOffset, h.Width, h.Height, 1);
            Assert.NotNull(decoded);
            Assert.Single(decoded);

            byte[][] chain = DdsAltFormat.FitChain(decoded, h.Width, h.Height, c.Levels);
            Assert.Equal(8, chain.Length);
            byte[] payload = DdsAltFormat.Join(chain);
            Assert.Equal(87380, payload.Length);

            // Level 1 must still be opaque white - not the empty level the sampler would read.
            Assert.Equal((byte)255, chain[1][0]);
            Assert.Equal((byte)255, chain[1][3]);
        }
    }
}
