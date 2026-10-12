using System;

namespace PokeModelExporter.Formats
{
    // Decodes GPU block-compressed textures (BC1-BC5, BC7) to RGBA8, from the public format
    // specifications (Microsoft "Texture Block Compression in Direct3D 11", Khronos Data Format
    // Specification). BC7 partition and anchor tables are the spec's, as published in Rich
    // Geldreich's bc7decomp (MIT / public domain).
    public static class BcDecoder
    {
        public enum Format { BC1, BC2, BC3, BC4, BC5, BC7 }

        public static int BlockBytes(Format f) => f == Format.BC1 || f == Format.BC4 ? 8 : 16;

        // blocks: linear 4x4 blocks, row-major. Returns width*height*4 bytes (RGBA).
        // normalMap: for BC5, rebuild Z into blue so the result is a usable RGB normal map.
        public static byte[] Decode(byte[] blocks, int width, int height, Format format, bool normalMap = false)
        {
            int bw = (width + 3) / 4, bh = (height + 3) / 4, size = BlockBytes(format);
            var rgba = new byte[width * height * 4];
            var block = new byte[64];
            for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int src = (by * bw + bx) * size;
                if (src + size > blocks.Length) continue;
                switch (format)
                {
                    case Format.BC1: DecodeBc1(blocks, src, block, true); break;
                    case Format.BC2: DecodeBc2(blocks, src, block); break;
                    case Format.BC3: DecodeBc3(blocks, src, block); break;
                    case Format.BC4: DecodeBc4Gray(blocks, src, block); break;
                    case Format.BC5: DecodeBc5(blocks, src, block, normalMap); break;
                    case Format.BC7: DecodeBc7(blocks, src, block); break;
                }
                for (int py = 0; py < 4; py++)
                for (int px = 0; px < 4; px++)
                {
                    int x = bx * 4 + px, y = by * 4 + py;
                    if (x >= width || y >= height) continue;
                    Buffer.BlockCopy(block, (py * 4 + px) * 4, rgba, (y * width + x) * 4, 4);
                }
            }
            return rgba;
        }

        // ---- BC1-BC5 ----

        static void DecodeBc1(byte[] b, int o, byte[] dst, bool allowAlpha)
        {
            int c0 = BitConverter.ToUInt16(b, o), c1 = BitConverter.ToUInt16(b, o + 2);
            var pal = new byte[16];
            Rgb565(c0, pal, 0);
            Rgb565(c1, pal, 4);
            pal[3] = pal[7] = 255;
            if (c0 > c1 || !allowAlpha)
            {
                for (int k = 0; k < 3; k++)
                {
                    pal[8 + k] = (byte)((2 * pal[k] + pal[4 + k] + 1) / 3);
                    pal[12 + k] = (byte)((pal[k] + 2 * pal[4 + k] + 1) / 3);
                }
                pal[11] = pal[15] = 255;
            }
            else
            {
                for (int k = 0; k < 3; k++) pal[8 + k] = (byte)((pal[k] + pal[4 + k]) / 2);
                pal[11] = 255;
                pal[12] = pal[13] = pal[14] = pal[15] = 0;
            }
            uint idx = BitConverter.ToUInt32(b, o + 4);
            for (int i = 0; i < 16; i++)
                Buffer.BlockCopy(pal, (int)((idx >> (2 * i)) & 3) * 4, dst, i * 4, 4);
        }

        static void Rgb565(int c, byte[] d, int at)
        {
            int r = (c >> 11) & 31, g = (c >> 5) & 63, bl = c & 31;
            d[at] = (byte)((r << 3) | (r >> 2));
            d[at + 1] = (byte)((g << 2) | (g >> 4));
            d[at + 2] = (byte)((bl << 3) | (bl >> 2));
        }

        static void DecodeBc2(byte[] b, int o, byte[] dst)
        {
            DecodeBc1(b, o + 8, dst, false);
            ulong a = BitConverter.ToUInt64(b, o);
            for (int i = 0; i < 16; i++) dst[i * 4 + 3] = (byte)(((a >> (4 * i)) & 15) * 17);
        }

        static void DecodeBc3(byte[] b, int o, byte[] dst)
        {
            DecodeBc1(b, o + 8, dst, false);
            var a = new byte[16];
            DecodeBc4(b, o, a);
            for (int i = 0; i < 16; i++) dst[i * 4 + 3] = a[i];
        }

        // One BC4 channel into 16 values.
        static void DecodeBc4(byte[] b, int o, byte[] dst16)
        {
            int r0 = b[o], r1 = b[o + 1];
            var v = new int[8];
            v[0] = r0; v[1] = r1;
            if (r0 > r1) for (int k = 1; k <= 6; k++) v[1 + k] = ((7 - k) * r0 + k * r1) / 7;
            else
            {
                for (int k = 1; k <= 4; k++) v[1 + k] = ((5 - k) * r0 + k * r1) / 5;
                v[6] = 0; v[7] = 255;
            }
            ulong bits = 0;
            for (int i = 0; i < 6; i++) bits |= (ulong)b[o + 2 + i] << (8 * i);
            for (int i = 0; i < 16; i++) dst16[i] = (byte)v[(int)((bits >> (3 * i)) & 7)];
        }

        static void DecodeBc4Gray(byte[] b, int o, byte[] dst)
        {
            var r = new byte[16];
            DecodeBc4(b, o, r);
            for (int i = 0; i < 16; i++) { dst[i * 4] = dst[i * 4 + 1] = dst[i * 4 + 2] = r[i]; dst[i * 4 + 3] = 255; }
        }

        static void DecodeBc5(byte[] b, int o, byte[] dst, bool normalMap)
        {
            var r = new byte[16];
            var g = new byte[16];
            DecodeBc4(b, o, r);
            DecodeBc4(b, o + 8, g);
            for (int i = 0; i < 16; i++)
            {
                dst[i * 4] = r[i];
                dst[i * 4 + 1] = g[i];
                byte z = 0;
                if (normalMap)
                {
                    double x = r[i] / 127.5 - 1, y = g[i] / 127.5 - 1;
                    z = (byte)Math.Round((Math.Sqrt(Math.Max(0, 1 - x * x - y * y)) + 1) * 127.5);
                }
                dst[i * 4 + 2] = z;
                dst[i * 4 + 3] = 255;
            }
        }

        // ---- BC7 ----

        // Per mode: subsets, partition bits, rotation bits, index-selection bits, color bits,
        // alpha bits, per-endpoint p-bits, shared p-bits, index bits, secondary index bits.
        static readonly int[,] Modes =
        {
            { 3, 4, 0, 0, 4, 0, 1, 0, 3, 0 },
            { 2, 6, 0, 0, 6, 0, 0, 1, 3, 0 },
            { 3, 6, 0, 0, 5, 0, 0, 0, 2, 0 },
            { 2, 6, 0, 0, 7, 0, 1, 0, 2, 0 },
            { 1, 0, 2, 1, 5, 6, 0, 0, 2, 3 },
            { 1, 0, 2, 0, 7, 8, 0, 0, 2, 2 },
            { 1, 0, 0, 0, 7, 7, 1, 0, 4, 0 },
            { 2, 6, 0, 0, 5, 5, 1, 0, 2, 0 },
        };

        static readonly int[] Weights2 = { 0, 21, 43, 64 };
        static readonly int[] Weights3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
        static readonly int[] Weights4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

        static int[] WeightsFor(int bits) => bits == 2 ? Weights2 : bits == 3 ? Weights3 : Weights4;

        sealed class Bits
        {
            readonly ulong _lo, _hi;
            int _pos;
            public Bits(byte[] b, int o) { _lo = BitConverter.ToUInt64(b, o); _hi = BitConverter.ToUInt64(b, o + 8); }
            public int Read(int count)
            {
                int v = 0;
                for (int i = 0; i < count; i++, _pos++)
                {
                    ulong word = _pos < 64 ? _lo : _hi;
                    v |= (int)((word >> (_pos & 63)) & 1) << i;
                }
                return v;
            }
        }

        static void DecodeBc7(byte[] b, int o, byte[] dst)
        {
            int mode = 0;
            while (mode < 8 && ((b[o] >> mode) & 1) == 0) mode++;
            if (mode == 8) { Array.Clear(dst, 0, 64); return; }

            var bits = new Bits(b, o);
            bits.Read(mode + 1);
            int ns = Modes[mode, 0], pb = Modes[mode, 1], rb = Modes[mode, 2], isb = Modes[mode, 3];
            int cb = Modes[mode, 4], ab = Modes[mode, 5], epb = Modes[mode, 6], spb = Modes[mode, 7];
            int ib = Modes[mode, 8], ib2 = Modes[mode, 9];

            int partition = bits.Read(pb), rotation = bits.Read(rb), indexSel = bits.Read(isb);
            int endpoints = ns * 2;
            var ep = new int[endpoints, 4];
            for (int c = 0; c < 3; c++)
                for (int e = 0; e < endpoints; e++) ep[e, c] = bits.Read(cb);
            for (int e = 0; e < endpoints; e++) ep[e, 3] = ab > 0 ? bits.Read(ab) : 255;

            var pbits = new int[endpoints];
            if (epb > 0) for (int e = 0; e < endpoints; e++) pbits[e] = bits.Read(1);
            if (spb > 0) for (int s = 0; s < ns; s++) { int p = bits.Read(1); pbits[s * 2] = pbits[s * 2 + 1] = p; }

            for (int e = 0; e < endpoints; e++)
            {
                for (int c = 0; c < 3; c++) ep[e, c] = Expand(ep[e, c], cb, epb + spb > 0 ? pbits[e] : -1);
                if (ab > 0) ep[e, 3] = Expand(ep[e, 3], ab, epb > 0 ? pbits[e] : -1);
            }

            // Indices; anchor pixels store one bit less.
            int a1 = ns == 2 ? Anchor2[partition] : ns == 3 ? Anchor3a[partition] : -1;
            int a2 = ns == 3 ? Anchor3b[partition] : -1;
            var idx = new int[16];
            var idx2 = new int[16];
            for (int i = 0; i < 16; i++)
                idx[i] = bits.Read(i == 0 || i == a1 || i == a2 ? ib - 1 : ib);
            if (ib2 > 0)
                for (int i = 0; i < 16; i++) idx2[i] = bits.Read(i == 0 ? ib2 - 1 : ib2);

            for (int i = 0; i < 16; i++)
            {
                int subset = ns == 1 ? 0 : ns == 2 ? Partition2[partition * 16 + i] : Partition3[partition * 16 + i];
                int e0 = subset * 2, e1 = e0 + 1;
                int ci = idx[i], cbits = ib, ai = idx[i], abits = ib;
                if (ib2 > 0)
                {
                    if (indexSel == 0) { ai = idx2[i]; abits = ib2; }
                    else { ci = idx2[i]; cbits = ib2; }
                }
                int cw = WeightsFor(cbits)[ci], aw = WeightsFor(abits)[ai];
                int r = Interp(ep[e0, 0], ep[e1, 0], cw), g = Interp(ep[e0, 1], ep[e1, 1], cw), bl = Interp(ep[e0, 2], ep[e1, 2], cw);
                int a = ab > 0 ? Interp(ep[e0, 3], ep[e1, 3], aw) : 255;
                switch (rotation)
                {
                    case 1: { int t = a; a = r; r = t; break; }
                    case 2: { int t = a; a = g; g = t; break; }
                    case 3: { int t = a; a = bl; bl = t; break; }
                }
                dst[i * 4] = (byte)r; dst[i * 4 + 1] = (byte)g; dst[i * 4 + 2] = (byte)bl; dst[i * 4 + 3] = (byte)a;
            }
        }

        static int Interp(int e0, int e1, int w) => ((64 - w) * e0 + w * e1 + 32) >> 6;

        // Unquantize an endpoint to 8 bits (with its p-bit appended when there is one).
        static int Expand(int v, int bits, int pbit)
        {
            if (pbit >= 0) { v = (v << 1) | pbit; bits++; }
            v <<= 8 - bits;
            return v | (v >> bits);
        }

        static readonly byte[] Partition2 = // 1024 values
        {
            0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1,
            0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 1,
            0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1,
            0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 1,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1,
            0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1,
            0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1,
            0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0,
            0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 0, 1,
            0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0,
            0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 0, 1, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 0, 0,
            0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0,
            0, 1, 1, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0, 0,
            0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1,
            0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 0, 0,
            0, 0, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 1, 0, 1, 0, 1, 0, 1, 1, 0, 1, 0, 1, 0, 1, 0,
            0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 0, 1, 1, 0, 1, 0, 1, 0, 1, 0, 0, 1, 0, 1,
            0, 1, 1, 1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 1, 0, 0, 0, 0, 1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 0, 0, 0,
            0, 0, 1, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 1, 0, 0, 0, 0, 1, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 1, 0, 0,
            0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 0, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 0, 0, 0, 0, 1, 1,
            0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 0, 0, 0,
            0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0,
            0, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 1,
            0, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 1, 0, 0, 0, 1, 1, 0,
            0, 1, 1, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 1, 0, 1, 1, 0, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0, 0, 1,
            0, 1, 1, 1, 1, 1, 1, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 1,
            0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0,
            0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 1, 1, 1
        };

        static readonly byte[] Partition3 = // 1024 values
        {
            0, 0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 1, 2, 2, 2, 2, 0, 0, 0, 1, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 2, 1,
            0, 0, 0, 0, 2, 0, 0, 1, 2, 2, 1, 1, 2, 2, 1, 1, 0, 2, 2, 2, 0, 0, 2, 2, 0, 0, 1, 1, 0, 1, 1, 1,
            0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 2, 2, 0, 0, 2, 2,
            0, 0, 2, 2, 0, 0, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1,
            0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2,
            0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2,
            0, 1, 1, 2, 0, 1, 1, 2, 0, 1, 1, 2, 0, 1, 1, 2, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 2,
            0, 0, 1, 1, 0, 1, 1, 2, 1, 1, 2, 2, 1, 2, 2, 2, 0, 0, 1, 1, 2, 0, 0, 1, 2, 2, 0, 0, 2, 2, 2, 0,
            0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 2, 1, 1, 2, 2, 0, 1, 1, 1, 0, 0, 1, 1, 2, 0, 0, 1, 2, 2, 0, 0,
            0, 0, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 1, 1, 1, 1,
            0, 1, 1, 1, 0, 1, 1, 1, 0, 2, 2, 2, 0, 2, 2, 2, 0, 0, 0, 1, 0, 0, 0, 1, 2, 2, 2, 1, 2, 2, 2, 1,
            0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 2, 2, 0, 1, 2, 2, 0, 0, 0, 0, 1, 1, 0, 0, 2, 2, 1, 0, 2, 2, 1, 0,
            0, 1, 2, 2, 0, 1, 2, 2, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 1, 2, 0, 0, 1, 2, 1, 1, 2, 2, 2, 2, 2, 2,
            0, 1, 1, 0, 1, 2, 2, 1, 1, 2, 2, 1, 0, 1, 1, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 2, 2, 1, 1, 2, 2, 1,
            0, 0, 2, 2, 1, 1, 0, 2, 1, 1, 0, 2, 0, 0, 2, 2, 0, 1, 1, 0, 0, 1, 1, 0, 2, 0, 0, 2, 2, 2, 2, 2,
            0, 0, 1, 1, 0, 1, 2, 2, 0, 1, 2, 2, 0, 0, 1, 1, 0, 0, 0, 0, 2, 0, 0, 0, 2, 2, 1, 1, 2, 2, 2, 1,
            0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 2, 2, 2, 0, 2, 2, 2, 0, 0, 2, 2, 0, 0, 1, 2, 0, 0, 1, 1,
            0, 0, 1, 1, 0, 0, 1, 2, 0, 0, 2, 2, 0, 2, 2, 2, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0,
            0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0,
            0, 1, 2, 0, 2, 0, 1, 2, 1, 2, 0, 1, 0, 1, 2, 0, 0, 0, 1, 1, 2, 2, 0, 0, 1, 1, 2, 2, 0, 0, 1, 1,
            0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0, 1, 1, 0, 1, 0, 1, 0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2,
            0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 2, 1, 2, 1, 2, 1, 0, 0, 2, 2, 1, 1, 2, 2, 0, 0, 2, 2, 1, 1, 2, 2,
            0, 0, 2, 2, 0, 0, 1, 1, 0, 0, 2, 2, 0, 0, 1, 1, 0, 2, 2, 0, 1, 2, 2, 1, 0, 2, 2, 0, 1, 2, 2, 1,
            0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 0, 1, 0, 1, 0, 0, 0, 0, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1,
            0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 2, 2, 2, 2, 0, 2, 2, 2, 0, 1, 1, 1, 0, 2, 2, 2, 0, 1, 1, 1,
            0, 0, 0, 2, 1, 1, 1, 2, 0, 0, 0, 2, 1, 1, 1, 2, 0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 2,
            0, 2, 2, 2, 0, 1, 1, 1, 0, 1, 1, 1, 0, 2, 2, 2, 0, 0, 0, 2, 1, 1, 1, 2, 1, 1, 1, 2, 0, 0, 0, 2,
            0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 1, 2,
            0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 2, 2, 2, 2, 2, 2, 0, 0, 2, 2, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 2, 2,
            0, 0, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2, 0, 0, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2,
            0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 1, 0, 2, 2, 2, 1, 2, 2, 2, 0, 2, 2, 2, 1, 2, 2, 2,
            0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 0, 1, 1, 1, 2, 0, 1, 1, 2, 2, 0, 1, 2, 2, 2, 0
        };

        static readonly byte[] Anchor2 = // 64 values
        {
            15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
            15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6, 6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15
        };

        static readonly byte[] Anchor3a = // 64 values
        {
            3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3, 3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
            8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15, 3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3
        };

        static readonly byte[] Anchor3b = // 64 values
        {
            15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8, 15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
            15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8, 15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8
        };
    }
}
