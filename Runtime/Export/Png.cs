using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PokeModelExporter.Export
{
    // Minimal PNG encoder (8-bit RGBA, no filtering) using .NET's built-in deflate.
    public static class Png
    {
        public static byte[] Encode(byte[] rgba, int width, int height)
        {
            using var o = new MemoryStream();
            o.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

            var ihdr = new byte[13];
            WriteBE(ihdr, 0, (uint)width);
            WriteBE(ihdr, 4, (uint)height);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 6;  // RGBA
            Chunk(o, "IHDR", ihdr);

            // zlib stream: header, raw deflate of filter-byte-prefixed rows, Adler-32.
            using var z = new MemoryStream();
            z.WriteByte(0x78); z.WriteByte(0x9C);
            uint a = 1, b = 0;
            using (var deflate = new DeflateStream(z, CompressionLevel.Optimal, leaveOpen: true))
            {
                var row = new byte[width * 4 + 1];
                for (int y = 0; y < height; y++)
                {
                    row[0] = 0;
                    Buffer.BlockCopy(rgba, y * width * 4, row, 1, width * 4);
                    deflate.Write(row, 0, row.Length);
                    foreach (byte v in row) { a = (a + v) % 65521; b = (b + a) % 65521; }
                }
            }
            var adler = new byte[4];
            WriteBE(adler, 0, (b << 16) | a);
            z.Write(adler, 0, 4);
            Chunk(o, "IDAT", z.ToArray());
            Chunk(o, "IEND", new byte[0]);
            return o.ToArray();
        }

        static void Chunk(Stream o, string type, byte[] data)
        {
            var len = new byte[4];
            WriteBE(len, 0, (uint)data.Length);
            o.Write(len, 0, 4);
            var t = Encoding.ASCII.GetBytes(type);
            o.Write(t, 0, 4);
            o.Write(data, 0, data.Length);
            uint crc = Crc(Crc(0xFFFFFFFF, t), data) ^ 0xFFFFFFFF;
            var c = new byte[4];
            WriteBE(c, 0, crc);
            o.Write(c, 0, 4);
        }

        static readonly uint[] CrcTable = BuildCrcTable();

        static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        static uint Crc(uint crc, byte[] data)
        {
            foreach (byte v in data) crc = CrcTable[(crc ^ v) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        static void WriteBE(byte[] d, int at, uint v)
        {
            d[at] = (byte)(v >> 24); d[at + 1] = (byte)(v >> 16); d[at + 2] = (byte)(v >> 8); d[at + 3] = (byte)v;
        }
    }
}
