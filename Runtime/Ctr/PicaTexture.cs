using System;

namespace PokeModelExporter.Ctr
{
    // PICA200 (3DS GPU) textures -> RGBA8, rows from the top. The GPU stores them in 8x8 tiles, tiles in rows, pixels
    // inside a tile in Morton (Z) order, with the image's first row at the bottom. Formats by GameFreak's numbering.
    public static class PicaTexture
    {
        public const int RGB565 = 0x02, RGB8 = 0x03, RGBA8 = 0x04, RGBA4 = 0x16, RGBA5551 = 0x17, LA8 = 0x23, HiLo8 = 0x24,
            L8 = 0x25, A8 = 0x26, LA4 = 0x27, L4 = 0x28, A4 = 0x29, ETC1 = 0x2A, ETC1A4 = 0x2B;

        public static int BitsPerPixel(int format) => format switch
        {
            RGBA8 => 32, RGB8 => 24, RGB565 or RGBA4 or RGBA5551 or LA8 or HiLo8 => 16,
            L8 or A8 or LA4 or ETC1A4 => 8, L4 or A4 or ETC1 => 4, _ => 0,
        };

        public static string Name(int format) => format switch
        {
            RGB565 => "RGB565", RGB8 => "RGB8", RGBA8 => "RGBA8", RGBA4 => "RGBA4", RGBA5551 => "RGBA5551", LA8 => "LA8", HiLo8 => "HiLo8",
            L8 => "L8", A8 => "A8", LA4 => "LA4", L4 => "L4", A4 => "A4", ETC1 => "ETC1", ETC1A4 => "ETC1A4", _ => $"0x{format:X2}",
        };

        static int MortonX(int i) => (i & 1) | (i >> 1 & 2) | (i >> 2 & 4);
        static int MortonY(int i) => (i >> 1 & 1) | (i >> 2 & 2) | (i >> 3 & 4);

        public static byte[] Decode(byte[] data, int offset, int width, int height, int format)
        {
            var o = new byte[width * height * 4];
            if (format == ETC1 || format == ETC1A4) { DecodeEtc(data, offset, width, height, format == ETC1A4, o); return o; }
            int bpp = BitsPerPixel(format);
            if (bpp == 0) throw new NotSupportedException("PICA texture format " + Name(format));
            int p = offset * 8; // bit position
            for (int ty = 0; ty < height; ty += 8)
                for (int tx = 0; tx < width; tx += 8)
                    for (int i = 0; i < 64; i++, p += bpp)
                    {
                        int x = tx + MortonX(i), y = ty + MortonY(i);
                        if (x >= width || y >= height) continue;
                        int q = ((height - 1 - y) * width + x) * 4;
                        Pixel(data, p, format, o, q);
                    }
            return o;
        }

        static void Pixel(byte[] d, int bit, int f, byte[] o, int q)
        {
            int b = bit >> 3;
            byte r, g, bl, a = 255;
            switch (f)
            {
                case RGBA8: a = d[b]; bl = d[b + 1]; g = d[b + 2]; r = d[b + 3]; break;
                case RGB8: bl = d[b]; g = d[b + 1]; r = d[b + 2]; break;
                case RGB565: { int v = d[b] | d[b + 1] << 8; r = X(v >> 11, 5); g = X(v >> 5 & 63, 6); bl = X(v & 31, 5); break; }
                case RGBA5551: { int v = d[b] | d[b + 1] << 8; r = X(v >> 11, 5); g = X(v >> 6 & 31, 5); bl = X(v >> 1 & 31, 5); a = (byte)((v & 1) * 255); break; }
                case RGBA4: { int v = d[b] | d[b + 1] << 8; r = X(v >> 12, 4); g = X(v >> 8 & 15, 4); bl = X(v >> 4 & 15, 4); a = X(v & 15, 4); break; }
                case LA8: a = d[b]; r = g = bl = d[b + 1]; break;
                case HiLo8: g = d[b]; r = d[b + 1]; bl = 0; break;
                case L8: r = g = bl = d[b]; break;
                case A8: r = g = bl = 255; a = d[b]; break;
                case LA4: r = g = bl = X(d[b] >> 4, 4); a = X(d[b] & 15, 4); break;
                case L4: r = g = bl = X((bit & 4) != 0 ? d[b] >> 4 : d[b] & 15, 4); break;
                case A4: r = g = bl = 255; a = X((bit & 4) != 0 ? d[b] >> 4 : d[b] & 15, 4); break;
                default: r = g = bl = 255; break;
            }
            o[q] = r; o[q + 1] = g; o[q + 2] = bl; o[q + 3] = a;
        }

        static byte X(int v, int bits) => (byte)(v * 255 / ((1 << bits) - 1));

        // ETC1 / ETC1A4: 4x4 blocks, four per 8x8 tile (top-left, top-right, bottom-left, bottom-right); each block is
        // a little-endian 64-bit ETC1 word, preceded in ETC1A4 by 64 bits of 4-bit alpha (column by column).
        static readonly int[,] Modifiers = { { 2, 8 }, { 5, 17 }, { 9, 29 }, { 13, 42 }, { 18, 60 }, { 24, 80 }, { 33, 106 }, { 47, 183 } };

        static void DecodeEtc(byte[] d, int p, int width, int height, bool alpha, byte[] o)
        {
            for (int ty = 0; ty < height; ty += 8)
                for (int tx = 0; tx < width; tx += 8)
                    for (int blk = 0; blk < 4; blk++)
                    {
                        ulong alphaBits = ulong.MaxValue;
                        if (alpha) { alphaBits = BitConverter.ToUInt64(d, p); p += 8; }
                        ulong block = BitConverter.ToUInt64(d, p); p += 8;
                        int bx = tx + (blk & 1) * 4, by = ty + (blk >> 1) * 4;
                        Block(block, alphaBits, alpha, bx, by, width, height, o);
                    }
        }

        static void Block(ulong v, ulong alphaBits, bool hasAlpha, int bx, int by, int width, int height, byte[] o)
        {
            bool flip = (v >> 32 & 1) != 0, diff = (v >> 33 & 1) != 0;
            int t1 = (int)(v >> 37 & 7), t2 = (int)(v >> 34 & 7);
            int r1, g1, b1, r2, g2, b2;
            if (diff)
            {
                int r = (int)(v >> 59 & 31), g = (int)(v >> 51 & 31), b = (int)(v >> 43 & 31);
                int dr = Sign3((int)(v >> 56 & 7)), dg = Sign3((int)(v >> 48 & 7)), db = Sign3((int)(v >> 40 & 7));
                r1 = E5(r); g1 = E5(g); b1 = E5(b); r2 = E5(r + dr); g2 = E5(g + dg); b2 = E5(b + db);
            }
            else
            {
                r1 = E4((int)(v >> 60 & 15)); g1 = E4((int)(v >> 52 & 15)); b1 = E4((int)(v >> 44 & 15));
                r2 = E4((int)(v >> 56 & 15)); g2 = E4((int)(v >> 48 & 15)); b2 = E4((int)(v >> 40 & 15));
            }
            for (int x = 0; x < 4; x++)
                for (int y = 0; y < 4; y++)
                {
                    int i = x * 4 + y; // pixel index: column-major
                    bool second = flip ? y >= 2 : x >= 2;
                    int msb = (int)(v >> (16 + i) & 1), lsb = (int)(v >> i & 1);
                    int table = second ? t2 : t1, mod = Modifiers[table, lsb];
                    if (msb != 0) mod = -mod;
                    int cr = (second ? r2 : r1) + mod, cg = (second ? g2 : g1) + mod, cb = (second ? b2 : b1) + mod;
                    int px = bx + x, py = by + y;
                    if (px >= width || py >= height) continue;
                    int q = ((height - 1 - py) * width + px) * 4;
                    o[q] = Clamp(cr); o[q + 1] = Clamp(cg); o[q + 2] = Clamp(cb);
                    o[q + 3] = hasAlpha ? (byte)((int)(alphaBits >> (i * 4) & 15) * 17) : (byte)255;
                }
        }

        static int Sign3(int v) => v >= 4 ? v - 8 : v;
        static int E5(int v) { v &= 31; return v << 3 | v >> 2; }
        static int E4(int v) => v << 4 | v;
        static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }
}
