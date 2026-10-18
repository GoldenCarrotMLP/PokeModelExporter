using System;
using System.Collections.Generic;
using System.Text;

namespace PokeModelExporter.Ctr
{
    // An Ultra Sun/Moon battle sequence (SESD, GARC a/0/8/8: moves, Z-Moves, other battle scenes). Header 'SESD', u32
    // version, u32, u32 length in frames; records from 0x10: u32 start frame, u32 end frame, u16 slot (version >= 1),
    // u16 variant (>= 2: which camera / trainer / side branch the record belongs to, 0 = always), u16 flag (>= 3),
    // u16 command, then the command's arguments; a start of 0xFFFFFFFF ends the list. The argument sizes are the game's
    // own table (Battle.cro, u32[195]); the layouts are the reads each command's handler makes (Battle.cro's handler
    // table): i int, f float, b bool (u32), v three floats, sN N bytes of text, ? an unread word. Every one of the 1237
    // files parses exactly with them. What the commands do: Docs/usum-research.md.
    public sealed class Sesd
    {
        public sealed class Record
        {
            public int Start, End, Slot, Variant, Flag, Command;
            public object[] Args; // int, float, bool, (float x, float y, float z) as float[3], string

            public int Int(int i) => Args[i] is int v ? v : Args[i] is bool b ? (b ? 1 : 0) : (int)(float)Args[i];
            public float Float(int i) => Args[i] is float v ? v : Args[i] is int n ? n : 0;
            public bool Bool(int i) => Args[i] is bool v ? v : Args[i] is int n && n != 0;
            public float[] Vec(int i) => Args[i] as float[] ?? new float[3];
            public string Text(int i) => Args[i] as string ?? "";

            public override string ToString()
            {
                var sb = new StringBuilder($"{Start,4}-{End,-4} a{Slot,-3} b{Variant,-3} c{Flag} cmd {Command,3}:");
                foreach (var a in Args)
                    sb.Append(' ').Append(a switch
                    {
                        float[] v => $"({v[0]:0.###},{v[1]:0.###},{v[2]:0.###})",
                        float f => f.ToString("0.###") + "f",
                        bool b => b ? "T" : "F",
                        string s => "\"" + s + "\"",
                        _ => a.ToString(),
                    });
                return sb.ToString();
            }
        }

        public int Version, Length;
        public readonly List<Record> Records = new List<Record>();

        public static bool IsSesd(byte[] d) => d != null && d.Length >= 16 && d[0] == 'S' && d[1] == 'E' && d[2] == 'S' && d[3] == 'D';

        public static Sesd Read(byte[] d)
        {
            if (!IsSesd(d)) throw new System.IO.InvalidDataException("not a SESD file");
            var s = new Sesd { Version = BitConverter.ToInt32(d, 4), Length = BitConverter.ToInt32(d, 12) };
            int p = 0x10;
            while (p + 4 <= d.Length && BitConverter.ToUInt32(d, p) != 0xFFFFFFFF)
            {
                var r = new Record { Start = BitConverter.ToInt32(d, p), End = BitConverter.ToInt32(d, p + 4) };
                p += 8;
                r.Slot = BitConverter.ToUInt16(d, p); p += 2;
                if (s.Version >= 2) { r.Variant = BitConverter.ToUInt16(d, p); p += 2; }
                if (s.Version >= 3) { r.Flag = BitConverter.ToUInt16(d, p); p += 2; }
                r.Command = BitConverter.ToUInt16(d, p); p += 2;
                if (r.Command >= Sizes.Length) throw new System.IO.InvalidDataException($"command {r.Command} at {p - 2:x}");
                int end = p + Sizes[r.Command];
                r.Args = Args(d, p, end, Layouts[r.Command]);
                p = end;
                s.Records.Add(r);
            }
            return s;
        }

        static object[] Args(byte[] d, int p, int end, string layout)
        {
            var o = new List<object>();
            for (int k = 0; k < layout.Length && p < end; k++)
            {
                char t = layout[k];
                if (t == 's')
                {
                    int n = 0;
                    while (k + 1 < layout.Length && char.IsDigit(layout[k + 1])) n = n * 10 + (layout[++k] - '0');
                    int z = Array.IndexOf(d, (byte)0, p, n); if (z < 0) z = p + n;
                    o.Add(Encoding.ASCII.GetString(d, p, z - p)); p += n;
                }
                else if (t == 'v') { o.Add(new[] { BitConverter.ToSingle(d, p), BitConverter.ToSingle(d, p + 4), BitConverter.ToSingle(d, p + 8) }); p += 12; }
                else if (t == 'f') { o.Add(BitConverter.ToSingle(d, p)); p += 4; }
                else if (t == 'b') { o.Add(BitConverter.ToUInt32(d, p) != 0); p += 4; }
                else { o.Add(BitConverter.ToInt32(d, p)); p += 4; }
            }
            return o.ToArray();
        }

        static readonly int[] Sizes =
        {
            0, 28, 44, 4, 0, 20, 24, 16, 32, 8, 8, 4, 4, 8, 12, 16, 28, 8, 8, 8,
            4, 8, 36, 8, 76, 4, 28, 4, 12, 4, 20, 12, 0, 36, 60, 44, 4, 16, 16, 8,
            16, 44, 24, 72, 52, 4, 4, 12, 4, 0, 20, 0, 0, 16, 44, 24, 36, 16, 16, 16,
            16, 72, 32, 76, 4, 4, 0, 20, 0, 16, 8, 0, 16, 44, 24, 16, 16, 16, 16, 4,
            72, 32, 84, 44, 4, 4, 52, 20, 12, 4, 8, 40, 44, 0, 20, 0, 28, 8, 8, 8,
            8, 4, 8, 24, 24, 4, 16, 24, 24, 20, 20, 16, 16, 4, 8, 0, 12, 4, 4, 4,
            8, 0, 20, 16, 8, 4, 16, 12, 20, 12, 12, 20, 0, 80, 72, 16, 44, 24, 16, 16,
            16, 24, 24, 24, 28, 24, 80, 80, 20, 4, 12, 0, 20, 8, 20, 8, 4, 4, 8, 12,
            12, 12, 8, 8, 8, 0, 8, 8, 0, 4, 0, 4, 8, 8, 8, 8, 48, 4, 8, 4,
            4, 4, 4, 4, 4, 0, 4, 8, 0, 0, 20, 0, 0, 0, 0,
        };

        static readonly string[] Layouts =
        {
            "", "ivbbb", "iiiviiiib", "i", "", "ivb", "ivbb", "iiif", "iivbbf", "ii",
            "ib", "b", "b", "ib", "iii", "iiii", "iiiiiii", "ii", "i?", "if",
            "i", "ib", "ivfvf", "ii", "iis32bbbvv", "i", "iffffib", "i", "ibb", "i",
            "iiv", "iib", "", "vvfii", "iivvfiiiibi", "ivvfbbi", "i", "iiii", "iiii", "ii",
            "fffi", "iiiiviiib", "iivb", "iiis32vbbbb", "iiiiiiiviib", "b", "f", "v", "f", "",
            "iiiii", "", "", "vb", "iiviiiiib", "ivbb", "is32", "vb", "vb", "iifb",
            "vb", "is32bbbvv", "iiivbb", "iis32bbbvv", "b", "f", "", "ffffi", "", "iiii",
            "ii", "", "vb", "iiviiiiib", "ivbb", "vb", "vb", "iifb", "vb", "b",
            "is32bbbvv", "iiivbb", "iis32bbbvvbb", "iis32b", "b", "f", "s32ivf", "vbi", "ibi", "i",
            "fi", "is32i", "iis32i", "", "ffffi", "", "iiiiiii", "ii", "ii", "ii",
            "ii", "i", "ii", "ifffff", "iiiiii", "i", "vb", "iivi", "ivbb", "iiv",
            "iiv", "iiib", "iiii", "i", "ii", "", "ibb", "b", "i", "i",
            "ii", "", "bvf", "vf", "bb", "i", "iv", "v", "ibv", "iib",
            "iib", "ibv", "", "iibiiiiiiiiiiviiii", "iiiiiiiiiiiviiii", "vb", "iiviiiiib", "ivbb", "vb", "vb",
            "iifb", "ivff", "viff", "ivbf", "vvb", "vv", "iviifbfbiifvvb", "ivvviii???????", "fbiif", "i",
            "iib", "", "ivb", "ib", "ivb", "ib", "b", "b", "ib", "iib",
            "iib", "iib", "if", "if", "ib", "", "ii", "ii", "", "i",
            "", "i", "ib", "ib", "ib", "ib", "iiiiiviiii", "i", "ii", "i",
            "i", "i", "i", "b", "i", "", "i", "ii", "", "",
            "iiv", "", "", "", "",
        };
    }
}
