using System;
using System.Collections.Generic;

namespace PokeModelExporter.Ctr
{
    // PICA200 command streams: pairs of (parameter, header) words; the header holds the register id (16 bits), a byte
    // mask (4 bits), how many more parameters follow (11 bits) and whether they go to consecutive registers (bit 31).
    // Streams are 8-byte aligned (a padding word after an odd count).
    public static class Pica
    {
        public static List<(int Reg, uint Value)> Writes(uint[] cmds) => Writes(cmds, 0, cmds.Length, out _);

        public static List<(int Reg, uint Value)> Writes(uint[] w, int start, int end, out bool valid)
        {
            var o = new List<(int, uint)>();
            valid = true;
            int i = start;
            while (i + 1 < end)
            {
                uint param = w[i], hdr = w[i + 1];
                int id = (int)(hdr & 0xFFFF), extra = (int)(hdr >> 20 & 0x7FF);
                bool consecutive = (hdr >> 31) != 0;
                if (id >= 0x300 || i + 2 + extra > end) { valid = false; break; }
                o.Add((id, param));
                for (int k = 0; k < extra; k++) o.Add((consecutive ? id + 1 + k : id, w[i + 2 + k]));
                i += 2 + extra;
                if (((i - start) & 1) != 0) i++;
            }
            return o;
        }

        // The command stream in d[start..end) whose parse ends at end with every header valid and which writes the
        // given register (GF stores render state at the end of a section, after variable-length fields).
        public static uint[] Find(byte[] d, int start, int end, int mustWrite)
        {
            end &= ~3;
            for (int p = (start + 3) & ~3; p + 8 <= end; p += 4)
            {
                int n = (end - p) / 4;
                var w = new uint[n];
                for (int k = 0; k < n; k++) w[k] = BitConverter.ToUInt32(d, p + 4 * k);
                var writes = Writes(w, 0, n, out bool valid);
                if (!valid) continue;
                foreach (var (reg, _) in writes)
                    if (reg == mustWrite) return w;
            }
            return Array.Empty<uint>();
        }

        // The last value written to each register.
        public static Dictionary<int, uint> Last(uint[] cmds)
        {
            var o = new Dictionary<int, uint>();
            foreach (var (r, v) in Writes(cmds)) o[r] = v;
            return o;
        }
    }
}
