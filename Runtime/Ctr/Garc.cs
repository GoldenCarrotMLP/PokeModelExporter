using System;
using System.Collections.Generic;
using System.IO;

namespace PokeModelExporter.Ctr
{
    // Ultra Sun/Moon (3DS) archives: GARC ("CRAG"), the numbered files under RomFS/a/x/y/z. Each file can hold up to
    // 32 sub-files (variants); most hold one. Contents are often LZ11-compressed (first byte 0x11).
    public static class Garc
    {
        public static List<List<byte[]>> Read(string path) => Read(File.ReadAllBytes(path));

        public static List<List<byte[]>> Read(byte[] b)
        {
            if (b.Length < 0x1C || b[0] != 'C' || b[1] != 'R' || b[2] != 'A' || b[3] != 'G') throw new InvalidDataException("not a GARC");
            int headerSize = BitConverter.ToInt32(b, 4);
            int dataOffset = BitConverter.ToInt32(b, 0x10);
            int p = headerSize;
            if (b[p] != 'O' || b[p + 1] != 'T' || b[p + 2] != 'A' || b[p + 3] != 'F') throw new InvalidDataException("GARC: no FATO");
            int fatoSize = BitConverter.ToInt32(b, p + 4);
            int count = BitConverter.ToUInt16(b, p + 8);
            var offsets = new int[count];
            for (int i = 0; i < count; i++) offsets[i] = BitConverter.ToInt32(b, p + 12 + 4 * i);
            p += fatoSize;
            if (b[p] != 'B' || b[p + 1] != 'T' || b[p + 2] != 'A' || b[p + 3] != 'F') throw new InvalidDataException("GARC: no FATB");
            var files = new List<List<byte[]>>(count);
            foreach (int o in offsets)
            {
                int q = p + 12 + o;
                uint bits = BitConverter.ToUInt32(b, q); q += 4;
                var subs = new List<byte[]>();
                for (int k = 0; k < 32; k++)
                {
                    if ((bits & (1u << k)) == 0) continue;
                    int start = BitConverter.ToInt32(b, q), end = BitConverter.ToInt32(b, q + 4); q += 12;
                    var d = new byte[end - start];
                    Array.Copy(b, dataOffset + start, d, 0, d.Length);
                    subs.Add(d);
                }
                files.Add(subs);
            }
            return files;
        }

        // The first sub-file of entry i, decompressed when it is LZ11.
        public static byte[] File0(List<List<byte[]>> garc, int i)
        {
            if (i < 0 || i >= garc.Count || garc[i].Count == 0) return null;
            return Lz11.Maybe(garc[i][0]);
        }
    }

    public static class Lz11
    {
        // Decompresses when the data looks like LZ11 (0x11 + a size that fits); otherwise returns it as is.
        public static byte[] Maybe(byte[] d)
        {
            if (d == null || d.Length < 4 || d[0] != 0x11) return d;
            try { return Decompress(d); } catch { return d; }
        }

        public static byte[] Decompress(byte[] d)
        {
            int size = d[1] | d[2] << 8 | d[3] << 16, p = 4;
            if (size == 0) { size = BitConverter.ToInt32(d, 4); p = 8; }
            if (size <= 0 || size > 64 << 20) throw new InvalidDataException("LZ11 size");
            var o = new byte[size];
            int n = 0;
            while (n < size)
            {
                int flags = d[p++];
                for (int i = 0; i < 8 && n < size; i++)
                {
                    if ((flags & (0x80 >> i)) == 0) { o[n++] = d[p++]; continue; }
                    int b0 = d[p], ind = b0 >> 4, len, disp;
                    if (ind == 0) { len = ((b0 & 0xF) << 4 | d[p + 1] >> 4) + 0x11; disp = ((d[p + 1] & 0xF) << 8 | d[p + 2]) + 1; p += 3; }
                    else if (ind == 1) { len = ((b0 & 0xF) << 12 | d[p + 1] << 4 | d[p + 2] >> 4) + 0x111; disp = ((d[p + 2] & 0xF) << 8 | d[p + 3]) + 1; p += 4; }
                    else { len = ind + 1; disp = ((b0 & 0xF) << 8 | d[p + 1]) + 1; p += 2; }
                    for (int k = 0; k < len && n < size; k++, n++) o[n] = o[n - disp];
                }
            }
            return o;
        }
    }
}
