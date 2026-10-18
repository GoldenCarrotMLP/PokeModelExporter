using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PokeModelExporter.Formats
{
    // Translates the NV assembly of a compiled particle shader (NVvp5.0 vertex / NVfp5.0 fragment programs, the text the
    // Switch's shader compiler keeps beside the binary; VfxbFile.VertexProgram / FragmentProgram) into an HLSL function
    // that does the same thing bit for bit: registers are uint4 (the GPU's untyped 32-bit lanes), each instruction reads
    // them as the type its suffix says (.F32 float, .S signed, .U unsigned) and writes the result's bits back.
    //
    // The generated function names its inputs by what the program reads; the caller binds them:
    //   vertex:   void F(uint4 vin[16], uint vid, out float4 opos, out uint4 vout[16])   (vertex.attrib[n], vertex.id)
    //   fragment: float4 F(uint4 fin[16], float4 fpos, float facing)                    (fragment.attrib[n], .position)
    //   uniform block n (program.buffer[n]): float4 _SvBn[] arrays, read by byte offset (LDC).
    //   textures: SvSample<slot>(uv), SvSampleLod<slot>(uv, lod), SvFetch<slot>(int3), SvLod<slot>(uv); the slot is the
    //             uniform block 14 offset / 8 the program loads its bindless handle from.
    //   PARAM c[] = program.local[]: float4 _SvLocal[4].
    public static class NvToHlsl
    {
        public sealed class Result
        {
            public string Code;                          // the function
            public bool Vertex;
            public SortedSet<int> Buffers = new SortedSet<int>();       // uniform blocks read
            public SortedDictionary<int, int> BufferSize = new SortedDictionary<int, int>(); // block -> float4s needed
            public SortedSet<int> Textures = new SortedSet<int>();      // texture slots sampled (any way)
            public SortedSet<int> Inputs = new SortedSet<int>();        // attrib slots read
            public SortedSet<int> Outputs = new SortedSet<int>();       // attrib slots written (vertex)
            public bool UsesVertexId, UsesPosition, UsesFacing, UsesLocal;
        }

        public static Result Translate(string program, string function)
        {
            var t = new Translator(program, function);
            return t.Run();
        }

        sealed class Translator
        {
            readonly string _text, _name;
            readonly Result _r = new Result();
            readonly StringBuilder _b = new StringBuilder();
            readonly Dictionary<string, int> _handles = new Dictionary<string, int>(); // "D0.x" -> texture slot
            int _indent = 1, _loops;
            int _maxR = -1;
            readonly Dictionary<string, int> _arrays = new Dictionary<string, int>();

            public Translator(string text, string name) { _text = text; _name = name; }

            public Result Run()
            {
                var lines = _text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                _r.Vertex = lines[0].StartsWith("!!NVvp");
                var body = new StringBuilder();
                foreach (var raw in lines)
                {
                    string line = raw.TrimEnd(';').Trim();
                    if (line.StartsWith("!!") || line == "END") continue;
                    string word = line.Split(' ')[0];
                    var lm = Regex.Match(line, @"^TEMP (lmem\d+)\[(\d+)\]$"); // a local array (indexed temporaries)
                    if (lm.Success) { _arrays[lm.Groups[1].Value] = int.Parse(lm.Groups[2].Value); continue; }
                    if (word == "OPTION" || word == "CBUFFER" || word == "ATTRIB" || word == "OUTPUT" || word == "TEMP" || word == "LONG"
                        || word == "SHORT" || word == "PARAM") continue;
                    Statement(line);
                }
                var head = new StringBuilder();
                if (_r.Vertex) head.Append($"void {_name}(uint4 vin[16], uint vid, out float4 opos, out uint4 vout[16])\n{{\n");
                else head.Append($"float4 {_name}(uint4 fin[16], float4 fpos, float facing)\n{{\n");
                head.Append("    uint4 T = 0, RC = 0, HC = 0;\n");
                for (int i = 0; i <= _maxR; i++) head.Append($"    uint4 R{i} = 0;\n");
                foreach (var a in _arrays) head.Append($"    uint4 {a.Key}[{a.Value}];\n    [unroll] for (int {a.Key}i = 0; {a.Key}i < {a.Value}; {a.Key}i++) {a.Key}[{a.Key}i] = 0;\n");
                head.Append("    float4 cc = 0;\n");
                head.Append(_r.Vertex ? "    uint4 oposb = 0;\n    [unroll] for (int oi = 0; oi < 16; oi++) vout[oi] = 0;\n" : "    uint4 ocol = 0;\n");
                head.Append(_b);
                head.Append(_r.Vertex ? "    opos = asfloat(oposb);\n}\n" : "    return asfloat(ocol);\n}\n");
                _r.Code = head.ToString();
                return _r;
            }

            void Emit(string s) { _b.Append(' ', 4 * _indent).Append(s).Append('\n'); }

            static readonly Regex CondRe = new Regex(@"^\(?(EQ|NE|LT|GT|LE|GE|TR|FL)(\d)?(?:\.([xyzw]{1,4}))?\)?$");

            // A condition-code test: "NE.x" -> on lane c of the written mask.
            string Test(string cond, int lane)
            {
                var m = CondRe.Match(cond.Trim());
                if (!m.Success) throw new NotSupportedException("condition " + cond);
                string sw = m.Groups[3].Success ? m.Groups[3].Value : "xyzw";
                char c = sw.Length == 1 ? sw[0] : sw[lane];
                string v = "cc." + c;
                switch (m.Groups[1].Value)
                {
                    case "EQ": return $"({v} == 0)";
                    case "NE": return $"({v} != 0)";
                    case "LT": return $"({v} < 0)";
                    case "GT": return $"({v} > 0)";
                    case "LE": return $"({v} <= 0)";
                    case "GE": return $"({v} >= 0)";
                    case "TR": return "true";
                    default: return "false";
                }
            }

            void Statement(string line)
            {
                string op = line.Split(' ')[0];
                string rest = line.Substring(op.Length).Trim();
                switch (op)
                {
                    case "IF": Emit($"if {Test(rest, 0)}"); Emit("{"); _indent++; return;
                    case "ELSE": _indent--; Emit("}"); Emit("else"); Emit("{"); _indent++; return;
                    case "ENDIF": _indent--; Emit("}"); return;
                    case "REP.S":
                    case "REP":
                        if (rest.Length > 0) throw new NotSupportedException("counted REP");
                        int n = _loops++;
                        Emit($"[loop] for (int rep{n} = 0; rep{n} < 4096; rep{n}++)"); Emit("{"); _indent++; return;
                    case "ENDREP": _indent--; Emit("}"); return;
                    case "BRK": Emit(rest.Length == 0 ? "break;" : $"if {Test(rest, 0)} break;"); return;
                    case "KIL": Emit(rest.Length == 0 ? "discard;" : $"if {Test(rest, 0)} discard;"); return;
                }
                var parts = SplitOperands(rest);
                var mods = op.Split('.');
                string name = mods[0];
                bool sat = mods.Contains("SAT"), setCc = mods.Contains("CC");
                char type = TypeOf(mods); // 'f', 'i', 'u'
                var dst = ParseDst(parts[0]);
                if (name == "LDC") { Ldc(mods, dst, parts[1]); return; }
                if (name == "TEX" || name == "TXP" || name == "TXL" || name == "TXF" || name == "LOD") { Tex(name, type, dst, parts, setCc); return; }

                var srcs = parts.Skip(1).Select(ParseSrc).ToArray();
                string[] lanes = new string[4];
                string resultType = type == 'f' ? "float" : type == 'i' ? "int" : "uint";
                char outType = type;
                string scalar = null; // dot products / scalar ops: one value for every lane
                switch (name)
                {
                    case "DP2": scalar = Dot(srcs, 2); break;
                    case "DP3": scalar = Dot(srcs, 3); break;
                    case "DP4": scalar = Dot(srcs, 4); break;
                    case "RCP": scalar = $"(1.0 / {S(srcs[0], 0, 'f')})"; break;
                    case "RSQ": scalar = $"rsqrt(abs({S(srcs[0], 0, 'f')}))"; break;
                    case "LG2": scalar = $"log2(abs({S(srcs[0], 0, 'f')}))"; break;
                    case "EX2": scalar = $"exp2({S(srcs[0], 0, 'f')})"; break;
                    case "COS": scalar = $"cos({S(srcs[0], 0, 'f')})"; break;
                    case "SIN": scalar = $"sin({S(srcs[0], 0, 'f')})"; break;
                    case "POW": scalar = $"pow({S(srcs[0], 0, 'f')}, {S(srcs[1], 0, 'f')})"; break;
                }
                for (int i = 0; i < 4; i++)
                {
                    if (!dst.Mask.Contains("xyzw"[i])) continue;
                    if (scalar != null) { lanes[i] = scalar; continue; }
                    string a = srcs.Length > 0 ? S(srcs[0], i, type) : null, b = srcs.Length > 1 ? S(srcs[1], i, type) : null,
                        c = srcs.Length > 2 ? S(srcs[2], i, type) : null;
                    switch (name)
                    {
                        case "MOV": lanes[i] = a; break;
                        case "ADD": lanes[i] = $"({a} + {b})"; break;
                        case "MUL": lanes[i] = $"({a} * {b})"; break;
                        case "MAD": lanes[i] = type == 'f' ? $"mad({a}, {b}, {c})" : $"({a} * {b} + {c})"; break;
                        case "MIN": lanes[i] = $"min({a}, {b})"; break;
                        case "MAX": lanes[i] = $"max({a}, {b})"; break;
                        case "DIV": lanes[i] = $"({a} / {S(srcs[1], 0, type)})"; break; // vector / scalar
                        case "MOD": lanes[i] = $"({a} % {S(srcs[1], 0, type)})"; break;
                        case "FLR": lanes[i] = $"floor({a})"; break;
                        case "CEIL": lanes[i] = $"ceil({a})"; break;
                        case "FRC": lanes[i] = $"frac({a})"; break;
                        case "AND": lanes[i] = $"({a} & {b})"; break;
                        case "OR": lanes[i] = $"({a} | {b})"; break;
                        case "XOR": lanes[i] = $"({a} ^ {b})"; break;
                        case "SHL": lanes[i] = $"({a} << {S(srcs[1], i, 'u')})"; break;
                        case "SHR": lanes[i] = $"({a} >> {S(srcs[1], i, 'u')})"; break;
                        case "TRUNC":
                        case "ROUND":
                        {
                            // float in; .S / .U results are integers (truncated toward zero / rounded), .F a float
                            string f = S(srcs[0], i, 'f');
                            string v = name == "TRUNC" ? $"trunc({f})" : $"round({f})";
                            lanes[i] = type == 'f' ? v : type == 'i' ? $"(int){v}" : $"(uint)max({v}, 0.0)";
                            break;
                        }
                        case "I2F":
                            lanes[i] = $"(float){S(srcs[0], i, type)}"; outType = 'f'; break;
                        case "F2I":
                            lanes[i] = type == 'i' ? $"(int){S(srcs[0], i, 'f')}" : $"(uint){S(srcs[0], i, 'f')}"; break;
                        case "SLT": case "SGT": case "SLE": case "SGE": case "SEQ": case "SNE":
                        {
                            string o = name == "SLT" ? "<" : name == "SGT" ? ">" : name == "SLE" ? "<=" : name == "SGE" ? ">=" : name == "SEQ" ? "==" : "!=";
                            // floats: 1.0 / 0.0; integers: ~0 / 0
                            lanes[i] = type == 'f' ? $"(({a} {o} {b}) ? 1.0 : 0.0)" : type == 'i' ? $"(({a} {o} {b}) ? -1 : 0)" : $"(({a} {o} {b}) ? 0xFFFFFFFFu : 0u)";
                            break;
                        }
                        default: throw new NotSupportedException("opcode " + op);
                    }
                }
                if (scalar != null) outType = 'f';
                Write(dst, lanes, outType, sat, setCc);
            }

            // Every written lane computed first (a source may be the destination), then stored.
            void Write(Dst dst, string[] lanes, char type, bool sat, bool setCc)
            {
                Emit("{");
                _indent++;
                string vt = type == 'f' ? "float" : type == 'i' ? "int" : "uint";
                for (int i = 0; i < 4; i++)
                {
                    if (lanes[i] == null) continue;
                    string v = lanes[i];
                    if (sat && type == 'f') v = $"saturate({v})";
                    Emit($"{vt} v{"xyzw"[i]} = {v};");
                }
                for (int i = 0; i < 4; i++)
                {
                    if (lanes[i] == null) continue;
                    char c = "xyzw"[i];
                    string store = type == 'u' ? $"{dst.Name}.{c} = v{c};" : $"{dst.Name}.{c} = asuint(v{c});";
                    if (setCc) store += $" cc.{c} = (float)v{c};";
                    Emit(dst.Cond != null ? $"if {Test(dst.Cond, i)} {{ {store} }}" : store);
                }
                _indent--;
                Emit("}");
            }

            string Dot(Src[] s, int n)
            {
                var terms = new List<string>();
                for (int i = 0; i < n; i++) terms.Add($"{S(s[0], i, 'f')} * {S(s[1], i, 'f')}");
                return "(" + string.Join(" + ", terms) + ")";
            }

            static char TypeOf(string[] mods)
            {
                foreach (var m in mods.Skip(1))
                {
                    if (m == "F" || m.StartsWith("F32") || m == "F16") return 'f';
                    if (m == "S" || m.StartsWith("S32")) return 'i';
                    if (m == "U" || m.StartsWith("U32") || m == "U64") return 'u';
                }
                return 'f';
            }

            // LDC.<type>[X2|X4] dst, bufN[offset] or bufN[R.c + offset]: the vector of floats from that byte offset on;
            // the write mask picks its lanes by position (R0.y <- second float).
            void Ldc(string[] mods, Dst dst, string src)
            {
                var m = Regex.Match(src.Trim(), @"^buf(\d+)\[(?:([A-Z]+\d*)\.([xyzw])\s*\+\s*)?(\d+)\]$");
                if (!m.Success) throw new NotSupportedException("LDC " + src);
                int buf = int.Parse(m.Groups[1].Value), off = int.Parse(m.Groups[4].Value);
                _r.Buffers.Add(buf);
                if (mods[1] == "U64")
                {
                    if (buf != 14 || m.Groups[2].Success) throw new NotSupportedException("handle load " + src);
                    _handles[dst.Name + "." + dst.Mask[0]] = off / 8;
                    return;
                }
                int count = mods[1].EndsWith("X4") ? 4 : mods[1].EndsWith("X2") ? 2 : 1;
                Need(buf, off + 16 * (m.Groups[2].Success ? 8 : 1)); // indexed loads walk key arrays (8 x 16 bytes)
                string idx = m.Groups[2].Success ? $"asint({Reg(m.Groups[2].Value)}.{m.Groups[3].Value}) + " : "";
                var lanes = new string[4];
                for (int i = 0; i < 4; i++)
                {
                    if (!dst.Mask.Contains("xyzw"[i])) continue;
                    if (i >= count) throw new NotSupportedException("LDC lane past its width " + src);
                    lanes[i] = $"SvLdc(_SvB{buf}, {idx}{off + 4 * i})";
                }
                // bits as stored: the type only matters to how later instructions read them
                Emit("{");
                _indent++;
                for (int i = 0; i < 4; i++) if (lanes[i] != null) Emit($"uint v{"xyzw"[i]} = asuint({lanes[i]});");
                for (int i = 0; i < 4; i++)
                    if (lanes[i] != null)
                    {
                        char c = "xyzw"[i];
                        string store = $"{dst.Name}.{c} = v{c};";
                        Emit(dst.Cond != null ? $"if {Test(dst.Cond, i)} {{ {store} }}" : store);
                    }
                _indent--;
                Emit("}");
            }

            void Need(int buf, int bytes)
            {
                int n = (bytes + 15) / 16;
                if (!_r.BufferSize.TryGetValue(buf, out int had) || had < n) _r.BufferSize[buf] = n;
            }

            void Tex(string name, char type, Dst dst, List<string> parts, bool setCc)
            {
                var coord = ParseSrc(parts[1]);
                var hm = Regex.Match(parts[2], @"handle\((\w+)\.([xyzw])\)");
                if (!hm.Success || !_handles.TryGetValue(hm.Groups[1].Value + "." + hm.Groups[2].Value, out int slot))
                    throw new NotSupportedException("texture handle " + parts[2]);
                if (parts[3].Trim() != "2D") throw new NotSupportedException("texture target " + parts[3]);
                _r.Textures.Add(slot);
                string F(int lane) => S(coord, lane, 'f');
                string sample;
                switch (name)
                {
                    case "TEX": sample = $"SvSample{slot}(float2({F(0)}, {F(1)}))"; break;
                    case "TXP": sample = $"SvSample{slot}(float2({F(0)}, {F(1)}) / {F(3)})"; break;
                    case "TXL": sample = $"SvSampleLod{slot}(float2({F(0)}, {F(1)}), {F(3)})"; break;
                    case "LOD": sample = $"SvLod{slot}(float2({F(0)}, {F(1)}))"; break;
                    default: sample = $"SvFetch{slot}(int3({S(coord, 0, 'i')}, {S(coord, 1, 'i')}, {S(coord, 2, 'i')}))"; break; // TXF
                }
                Emit("{");
                _indent++;
                Emit($"float4 s = {sample};");
                for (int i = 0; i < 4; i++)
                {
                    if (!dst.Mask.Contains("xyzw"[i])) continue;
                    char c = "xyzw"[i];
                    string store = name == "TXF" && type != 'f' ? $"{dst.Name}.{c} = (uint)s.{c};" : $"{dst.Name}.{c} = asuint(s.{c});";
                    if (setCc) store += $" cc.{c} = s.{c};";
                    Emit(dst.Cond != null ? $"if {Test(dst.Cond, i)} {{ {store} }}" : store);
                }
                _indent--;
                Emit("}");
            }

            sealed class Dst { public string Name, Mask, Cond; }
            sealed class Src { public string Base, Swizzle = "xyzw"; public bool Neg, Abs; public string[] Literal; }

            Dst ParseDst(string s)
            {
                s = s.Trim();
                var d = new Dst();
                var cm = Regex.Match(s, @"\(([^)]*)\)$");
                if (cm.Success) { d.Cond = cm.Groups[1].Value; s = s.Substring(0, cm.Index).Trim(); }
                var m = Regex.Match(s, @"^(.*?)(?:\.([xyzw]{1,4}))?$");
                d.Name = Reg(m.Groups[1].Value);
                d.Mask = m.Groups[2].Success ? m.Groups[2].Value : "xyzw";
                return d;
            }

            Src ParseSrc(string s)
            {
                s = s.Trim();
                var r = new Src();
                if (s.StartsWith("-")) { r.Neg = true; s = s.Substring(1).Trim(); }
                if (s.StartsWith("|"))
                {
                    r.Abs = true;
                    int end = s.LastIndexOf('|');
                    string inner = s.Substring(1, end - 1);
                    string after = s.Substring(end + 1);
                    s = inner + after; // |R0|.x is not used; |R0.x| is
                }
                if (s.StartsWith("{"))
                {
                    int end = s.IndexOf('}');
                    r.Literal = s.Substring(1, end - 1).Split(',').Select(x => x.Trim()).ToArray();
                    string after = s.Substring(end + 1);
                    if (after.StartsWith(".")) r.Swizzle = after.Substring(1);
                    return r;
                }
                var m = Regex.Match(s, @"^(.*?)(?:\.([xyzw]{1,4}))?$");
                r.Base = Reg(m.Groups[1].Value);
                if (m.Groups[2].Success) r.Swizzle = m.Groups[2].Value;
                return r;
            }

            // Operand names in the generated code.
            string Reg(string n)
            {
                n = n.Trim();
                var m = Regex.Match(n, @"^R(\d+)$");
                if (m.Success) { _maxR = Math.Max(_maxR, int.Parse(m.Groups[1].Value)); return n; }
                if (n == "T" || n == "RC" || n == "HC") return n;
                if (Regex.IsMatch(n, @"^D\d+$")) return n; // only as LDC.U64 destinations / handle()
                m = Regex.Match(n, @"^(?:vertex|fragment)\.attrib\[(\d+)\]$");
                if (m.Success) { int a = int.Parse(m.Groups[1].Value); _r.Inputs.Add(a); return (_r.Vertex ? "vin[" : "fin[") + a + "]"; }
                m = Regex.Match(n, @"^result\.attrib\[(\d+)\]$");
                if (m.Success) { int a = int.Parse(m.Groups[1].Value); _r.Outputs.Add(a); return $"vout[{a}]"; }
                if (n == "result.position") return "oposb";
                if (n == "result.color" || n == "result_color0") return "ocol";
                if (n == "vertex.id") { _r.UsesVertexId = true; return "uint4(vid, vid, vid, vid)"; }
                if (n == "fragment.position") { _r.UsesPosition = true; return "asuint(fpos)"; }
                if (n == "fragment.facing") { _r.UsesFacing = true; return "asuint(float4(facing, facing, facing, facing))"; }
                m = Regex.Match(n, @"^(lmem\d+)\[(?:(\d+)|([A-Z]+\d*)\.([xyzw]))\]$");
                if (m.Success)
                    return m.Groups[2].Success ? $"{m.Groups[1].Value}[{m.Groups[2].Value}]" : $"{m.Groups[1].Value}[asint({Reg(m.Groups[3].Value)}.{m.Groups[4].Value})]";
                m = Regex.Match(n, @"^c\[(\d+)\]$");
                if (m.Success) { _r.UsesLocal = true; return $"asuint(_SvLocal[{m.Groups[1].Value}])"; }
                throw new NotSupportedException("operand " + n);
            }

            // One lane of a source as an HLSL value of the given type (f float, i int, u uint).
            string S(Src s, int lane, char type)
            {
                char c = s.Swizzle.Length == 1 ? s.Swizzle[0] : s.Swizzle[lane];
                string v;
                if (s.Literal != null)
                {
                    string lit = s.Literal["xyzw".IndexOf(c)];
                    v = Literal(lit, type);
                }
                else
                {
                    string bits = $"{s.Base}.{c}";
                    v = type == 'f' ? $"asfloat({bits})" : type == 'i' ? $"asint({bits})" : bits;
                }
                if (s.Abs) v = type == 'u' ? v : $"abs({v})";
                if (s.Neg) v = type == 'u' ? $"(uint)(-(int){v})" : $"(-{v})";
                return v;
            }

            static string Literal(string lit, char type)
            {
                if (lit.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    uint bits = Convert.ToUInt32(lit.Substring(2), 16);
                    return type == 'f' ? $"asfloat(0x{bits:X8}u)" : type == 'i' ? $"(int)0x{bits:X8}u" : $"0x{bits:X8}u";
                }
                if (type == 'f')
                {
                    float f = float.Parse(lit, CultureInfo.InvariantCulture);
                    if (float.IsInfinity(f) || float.IsNaN(f)) return $"asfloat(0x{BitConverter.SingleToInt32Bits(f):X8}u)";
                    string r = f.ToString("G9", CultureInfo.InvariantCulture);
                    if (!r.Contains('.') && !r.Contains('E')) r += ".0";
                    return r;
                }
                // integer lanes: decimal text (a float literal in an integer instruction keeps its bits)
                if (lit.Contains('.') || lit.Contains('e') || lit.Contains('E'))
                {
                    int bits = BitConverter.SingleToInt32Bits(float.Parse(lit, CultureInfo.InvariantCulture));
                    return type == 'i' ? $"(int)0x{bits:X8}" : $"0x{bits:X8}u";
                }
                long n = long.Parse(lit, CultureInfo.InvariantCulture);
                return type == 'i' ? $"(int){n}" : $"{(uint)n}u";
            }

            static List<string> SplitOperands(string s)
            {
                var r = new List<string>();
                int depth = 0;
                var cur = new StringBuilder();
                foreach (char ch in s)
                {
                    if (ch == '{' || ch == '[' || ch == '(') depth++;
                    if (ch == '}' || ch == ']' || ch == ')') depth--;
                    if (ch == ',' && depth == 0) { r.Add(cur.ToString().Trim()); cur.Clear(); }
                    else cur.Append(ch);
                }
                if (cur.Length > 0) r.Add(cur.ToString().Trim());
                return r;
            }
        }
    }
}
