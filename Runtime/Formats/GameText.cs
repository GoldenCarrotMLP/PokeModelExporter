using System;
using System.Collections.Generic;
using System.Text;

namespace PokeModelExporter.Formats
{
    // Game Freak's message files (Gen 6 onward, Scarlet/Violet's message/dat/<Language>/{common,script}/<name>.dat) and their
    // label tables (<name>.tbl, "AHTB"). Our own reader, from the public format descriptions (Project Pokemon's text
    // format notes, PokeDocs):
    //
    //   .dat  u16 sections (1), u16 lines, u32 section size, u32 initial key (0), u32 section offset (0x10)
    //         section: u32 size, then per line { u32 offset (from the section start), u16 length in UTF-16 units,
    //         u16 attribute }, then the lines' UTF-16LE text. Each line is scrambled with a 16-bit key: the first unit
    //         with 0x7C89 + line * 0x2983, the key rotated left by 3 bits after every unit.
    //   .tbl  "AHTB", u32 count, then per label { u64 FNV-1a 64 of the name, u16 length incl. the NUL, the name, NUL },
    //         in line order.
    //
    // Inside a line, 0x0010 opens a control code: u16 count, u16 code, then count - 1 argument units. Turned into text
    // tokens (Docs/reference/game-sources.md in PokeSim, "Message text"):
    //   [VAR XXXX(a,b)]   a variable (a name, number, ...) with its code and arguments in hex; the first argument is the
    //                     buffer index the game fills. Known codes are named: [VAR PKNICK(0000)] (see Names).
    //   [WAIT n]          a pause (BE02), [SCROLL] / [CLEAR] (BE00 / BE01: the text box scrolls or clears)
    //   [COLOR n]         text colour (FF00)
    //   [GENDER(buf) m|f] / [NUMBER(buf) one|many]: codes 1100 / 1101, the branches the game picks by the buffer in
    //                     their first argument; the second holds the two texts' lengths (low / high byte) and the texts
    //                     follow the code ("word[NUMBER(0000) |s]"). A literal | or ] inside a branch is \| / \].
    // Private-use glyphs: E07F narrow no-break space, E08D ellipsis, E08E / E08F male / female sign.
    public static class GameText
    {
        // Variable codes named only where Scarlet/Violet's own lines show what they hold (the rest stay in hex): TRNAME a
        // player's or trainer's name, PKNAME a species, PKNICK a Pokemon's nickname, TYPE, LOCATION, ABILITY, MOVE, STAT,
        // NUMn a number of up to n digits (second argument: the digit grouping). Seen in hex in the battle text: 01D1 the
        // player's own Pokemon's nickname (send-out lines), 010C a Pokemon's name with its side ("The opposing ..."),
        // 1300 / 1301 English articles before 019E (a trainer's class and name), 1302 before an item, 1000 / 1001 / 1003
        // quantity articles ("a" / "an" / "some"), BD06 at the start of a line whose breaks the game reflows (the words
        // after the first line break each end with [SCROLL] + newline: break points, not boxes), 0114 at the end of
        // script lines (a speaker cue, its argument varies).
        public static readonly Dictionary<int, string> Names = new Dictionary<int, string>
        {
            [0x0100] = "TRNAME", [0x0101] = "PKNAME", [0x0102] = "PKNICK", [0x0103] = "TYPE", [0x0105] = "LOCATION",
            [0x0106] = "ABILITY", [0x0107] = "MOVE", [0x010D] = "STAT",
            [0x0200] = "NUM1", [0x0201] = "NUM2", [0x0202] = "NUM3", [0x0203] = "NUM4", [0x0204] = "NUM5",
            [0x0205] = "NUM6", [0x0206] = "NUM7", [0x0207] = "NUM8", [0x0208] = "NUM9",
        };

        // Written into the output's index.json so a reader knows what the brackets mean.
        public const string TokenFormat = "[VAR NAME(args)] or [VAR XXXX(args)]: a variable, args in hex, the first the buffer the game fills; "
            + "[GENDER(buf) male|female], [NUMBER(buf) one|many]: branches; [WAIT n], [SCROLL], [CLEAR], [COLOR n]: text box controls; "
            + "\\[ and \\\\: a literal bracket or backslash";

        public sealed class Line
        {
            public string Label;
            public string Text;
            public int Attribute;
        }

        public static List<Line> Read(byte[] dat, byte[] tbl)
        {
            var labels = tbl != null ? ReadLabels(tbl) : new List<string>();
            int lineCount = U16(dat, 2);
            int section = (int)U32(dat, 12);
            var lines = new List<Line>(lineCount);
            for (int i = 0; i < lineCount; i++)
            {
                int entry = section + 4 + i * 8;
                int offset = section + (int)U32(dat, entry);
                int length = U16(dat, entry + 4);
                int attribute = U16(dat, entry + 6);
                var units = new ushort[length];
                ushort key = (ushort)(0x7C89 + i * 0x2983);
                for (int c = 0; c < length && offset + c * 2 + 1 < dat.Length; c++)
                {
                    units[c] = (ushort)(U16(dat, offset + c * 2) ^ key);
                    key = (ushort)((key << 3) | (key >> 13));
                }
                lines.Add(new Line { Label = i < labels.Count ? labels[i] : null, Text = Decode(units), Attribute = attribute });
            }
            return lines;
        }

        public static List<string> ReadLabels(byte[] tbl)
        {
            var names = new List<string>();
            if (tbl.Length < 8 || tbl[0] != 'A' || tbl[1] != 'H' || tbl[2] != 'T' || tbl[3] != 'B') return names;
            int count = (int)U32(tbl, 4), p = 8;
            for (int i = 0; i < count && p + 10 <= tbl.Length; i++)
            {
                int len = U16(tbl, p + 8);
                p += 10;
                int n = len > 0 ? len - 1 : 0;
                names.Add(Encoding.UTF8.GetString(tbl, p, Math.Min(n, tbl.Length - p)));
                p += len;
            }
            return names;
        }

        static string Decode(ushort[] u)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < u.Length; i++)
            {
                int c = u[i];
                if (c == 0) break;
                if (c == 0x10 && i + 2 < u.Length)
                {
                    int count = u[i + 1], code = u[i + 2];
                    int argStart = i + 3, argCount = Math.Max(0, Math.Min(count - 1, u.Length - argStart));
                    i = argStart + argCount - 1;
                    // Gender / number branches: args (buffer, first length | second length << 8), the two texts after.
                    if ((code == 0x1100 || code == 0x1101) && argCount == 2)
                    {
                        int a = u[argStart + 1] & 0xFF, b = u[argStart + 1] >> 8;
                        if (i + 1 + a + b <= u.Length)
                        {
                            string first = Plain(u, i + 1, a), second = Plain(u, i + 1 + a, b);
                            sb.Append($"[{(code == 0x1100 ? "GENDER" : "NUMBER")}({u[argStart]:X4}) {first}|{second}]");
                            i += a + b;
                            continue;
                        }
                    }
                    sb.Append(Control(code, u, argStart, argCount));
                    continue;
                }
                sb.Append(Glyph(c));
            }
            return sb.ToString();
        }

        static string Control(int code, ushort[] u, int start, int count)
        {
            switch (code)
            {
                case 0xBE00: return "[SCROLL]";
                case 0xBE01: return "[CLEAR]";
                case 0xBE02: return count > 0 ? $"[WAIT {u[start]}]" : "[WAIT]";
                case 0xBDFF: return "";
                case 0xFF00: return count > 0 ? $"[COLOR {u[start]}]" : "[COLOR]";
            }
            var sb = new StringBuilder("[VAR ");
            sb.Append(Names.TryGetValue(code, out var name) ? name : code.ToString("X4"));
            if (count > 0)
            {
                sb.Append('(');
                for (int k = 0; k < count; k++) { if (k > 0) sb.Append(','); sb.Append(u[start + k].ToString("X4")); }
                sb.Append(')');
            }
            return sb.Append(']').ToString();
        }

        static string Plain(ushort[] u, int start, int n)
        {
            var sb = new StringBuilder();
            for (int k = start; k < start + n && k < u.Length; k++) sb.Append(Glyph(u[k]));
            return sb.ToString().Replace("|", "\\|").Replace("]", "\\]");
        }

        static string Glyph(int c)
        {
            switch (c)
            {
                case 0xE07F: return "\u202F";
                case 0xE08D: return "\u2026";
                case 0xE08E: return "\u2642";
                case 0xE08F: return "\u2640";
                case '[': return "\\["; // (a literal bracket can't be taken for a token)
                case '\\': return "\\\\";
                default: return ((char)c).ToString();
            }
        }

        static int U16(byte[] b, int o) => o + 1 < b.Length ? b[o] | (b[o + 1] << 8) : 0;
        static uint U32(byte[] b, int o) => o + 3 < b.Length ? (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)) : 0;
    }
}
