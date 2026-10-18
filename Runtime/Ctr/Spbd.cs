using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PokeModelExporter.Export;

namespace PokeModelExporter.Ctr
{
    // Ultra Sun/Moon particle effects: NintendoWare's 3DS effect binary ("SPBD", version 0x33; the battle scripts' particle
    // resources in GARC a/0/8/7), the 3DS sibling of the Wii U eft whose resource layout the community decompilation
    // describes (nw::eft SimpleEmitterData). Our own reading of it; field offsets checked against 15930 emitters
    // (Docs/usum-research.md "Particles"). Each emitter becomes the move effect emitter description our particle player
    // plays (FxEmitter's JSON: 60 frames a second, metres, speeds per frame), its textures PNGs, and its render state
    // (the MATC block of PICA register writes: combiner stages, blending, depth, alpha test) the same material
    // description the stage models carry, so it draws with the PICA combiner shader.
    public sealed class Spbd
    {
        public sealed class Texture { public int Width, Height, Format; public byte[] Rgba; public string Name; }

        public sealed class Emitter
        {
            public string Name;
            public int Type, Flags;
            public Texture[] Textures = new Texture[2];
            public Dictionary<string, string> Pica = new Dictionary<string, string>();
            public readonly Dictionary<string, object> Json = new Dictionary<string, object>();
        }

        public sealed class EmitterSet { public string Name; public List<Emitter> Emitters = new List<Emitter>(); }

        public int Version;
        public readonly List<EmitterSet> Sets = new List<EmitterSet>();
        public readonly Dictionary<string, Texture> Textures = new Dictionary<string, Texture>();

        // The 3DS format codes of the texture descriptions -> PICA formats (each decoded and looked at; 8-bit single
        // channels are L8 / A8, drawn as both intensity and alpha).
        static readonly Dictionary<int, int> Formats = new Dictionary<int, int>
        {
            [0x12] = PicaTexture.ETC1A4, [0x13] = PicaTexture.ETC1, [0x14] = PicaTexture.A4, [0x15] = PicaTexture.L4, [0x16] = PicaTexture.LA4,
            [0x17] = PicaTexture.L8, [0x18] = PicaTexture.HiLo8, [0x19] = PicaTexture.A8, [0x1A] = PicaTexture.LA8, [0x1B] = PicaTexture.RGBA5551,
            [0x1C] = PicaTexture.RGBA4, [0x1D] = PicaTexture.RGBA8, [0x1E] = PicaTexture.RGB8, [0x1F] = PicaTexture.RGB565,
        };

        byte[] _d;
        int _strings, _texBlock, _matc;

        public static bool IsSpbd(byte[] d) => d != null && d.Length > 0x48 && d[0] == 'S' && d[1] == 'P' && d[2] == 'B' && d[3] == 'D';

        // prefix names the textures (unique per resource: "usum4271_t2600")
        public static Spbd Read(byte[] d, string prefix)
        {
            if (!IsSpbd(d)) throw new System.IO.InvalidDataException("not SPBD");
            var s = new Spbd { _d = d, Version = I(d, 4), _strings = I(d, 0x10), _texBlock = I(d, 0x14), _matc = I(d, 0x40) };
            int sets = I(d, 8);
            for (int k = 0; k < sets; k++)
            {
                int b = 0x48 + 0x1C * k;
                var set = new EmitterSet { Name = s.Str(I(d, b + 8)) };
                int n = I(d, b + 0x10), table = I(d, b + 0x14);
                for (int i = 0; i < n; i++)
                {
                    int o = I(d, table + 8 * i);
                    try { set.Emitters.Add(s.ReadEmitter(o, prefix)); }
                    catch (Exception e) when (e is ArgumentException || e is IndexOutOfRangeException) { }
                }
                s.Sets.Add(set);
            }
            return s;
        }

        string Str(int off)
        {
            int at = _strings + off;
            if (off < 0 || at >= _d.Length) return "";
            int z = Array.IndexOf(_d, (byte)0, at);
            return Encoding.ASCII.GetString(_d, at, (z < 0 ? _d.Length : z) - at);
        }

        static int I(byte[] d, int o) => BitConverter.ToInt32(d, o);
        int I(int o) => BitConverter.ToInt32(_d, o);
        float F(int o) => BitConverter.ToSingle(_d, o);
        float[] V(int o, int n, float scale = 1)
        {
            var v = new float[n];
            for (int i = 0; i < n; i++) v[i] = F(o + 4 * i) * scale;
            return v;
        }

        const float Cm = 0.01f; // the game's centimetres -> metres

        Emitter ReadEmitter(int o, string prefix)
        {
            var e = new Emitter { Type = I(o), Flags = I(o + 4), Name = Str(I(o + 0x38)) };
            var j = e.Json;
            j["name"] = e.Name;

            // ---- Render state: the emitter's MATC (an offset into the MATC table at 0x158) ----
            int matc = _matc + I(o + 0x158);
            Dictionary<int, uint> regs = null;
            if (matc + 12 < _d.Length && _d[matc] == 'M' && _d[matc + 1] == 'A' && _d[matc + 2] == 'T' && _d[matc + 3] == 'C')
            {
                int count = I(matc + 4);
                var words = new uint[count * 2];
                for (int k = 0; k < words.Length && matc + 12 + 4 * k + 4 <= _d.Length; k++) words[k] = BitConverter.ToUInt32(_d, matc + 12 + 4 * k);
                regs = Pica.Last(words);
                Describe(regs, e.Pica);
            }

            // ---- Textures (two descriptions of 0x7C bytes from 0x40) ----
            var texJson = new List<object>();
            for (int slot = 0; slot < 2; slot++)
            {
                int t = o + 0x40 + 0x7C * slot;
                int w = BitConverter.ToUInt16(_d, t), h = BitConverter.ToUInt16(_d, t + 2);
                if (w == 0 || h == 0) continue;
                int code = I(t + 0x6C), size = I(t + 0x70), off = I(t + 0x74);
                // the MATC's own format register for the unit wins (0x08E unit 0, 0x096 unit 1)
                int format = regs != null && regs.TryGetValue(slot == 0 ? 0x08E : 0x096, out var fr) ? PicaFromRegister((int)fr & 0xF) : -1;
                if (format < 0 && !Formats.TryGetValue(code, out format)) continue;
                string name = $"{prefix}_t{off:X}";
                if (!Textures.TryGetValue(name, out var tex))
                {
                    tex = new Texture { Width = w, Height = h, Format = format, Name = name };
                    try { tex.Rgba = PicaTexture.Decode(_d, _texBlock + off, w, h, format); }
                    catch (IndexOutOfRangeException) { continue; }
                    // single channel formats: intensity and alpha alike (a particle mask)
                    if (format == PicaTexture.L8 || format == PicaTexture.A8 || format == PicaTexture.L4 || format == PicaTexture.A4)
                        for (int p = 0; p < w * h; p++)
                        {
                            byte v = format == PicaTexture.A8 || format == PicaTexture.A4 ? tex.Rgba[4 * p + 3] : tex.Rgba[4 * p];
                            tex.Rgba[4 * p] = tex.Rgba[4 * p + 1] = tex.Rgba[4 * p + 2] = tex.Rgba[4 * p + 3] = v;
                        }
                    // PICA rows run bottom-up: flipped so the image's top is the texture's top (the Normal type's
                    // mark pointed down before)
                    int stride = w * 4;
                    var row = new byte[stride];
                    for (int y = 0; y < h / 2; y++)
                    {
                        Buffer.BlockCopy(tex.Rgba, y * stride, row, 0, stride);
                        Buffer.BlockCopy(tex.Rgba, (h - 1 - y) * stride, tex.Rgba, y * stride, stride);
                        Buffer.BlockCopy(row, 0, tex.Rgba, (h - 1 - y) * stride, stride);
                    }
                    Textures[name] = tex;
                }
                e.Textures[slot] = tex;
                // texture animation block (0x78 bytes) of this slot
                int a = o + 0x338 + 0x78 * slot;
                bool pattern = _d[a] != 0;
                int divisions = Math.Max(1, (int)_d[a + 3]);
                float[] uvScale = V(a + 48, 2);
                var tj = new Dictionary<string, object>
                {
                    ["slot"] = slot, ["name"] = name, // UVs scale from the top-left corner, like the effects' static path
                    ["wrap"] = new object[] { WrapOf(regs, slot, 0), WrapOf(regs, slot, 1) },
                    ["uvScale"] = new object[] { Num(uvScale[0] == 0 ? 1 : uvScale[0]), Num(uvScale[1] == 0 ? 1 : uvScale[1]) },
                    ["scrollAdd"] = new object[] { Num(F(a + 60) * 0.5f), Num(F(a + 64) * 0.5f) }, // per 60 fps frame
                    ["scroll"] = new object[] { Num(F(a + 68)), Num(F(a + 72)) },
                    ["scrollRandom"] = new object[] { Num(F(a + 76)), Num(F(a + 80)) },
                    ["uvScaleAdd"] = new object[] { Num(F(a + 84) * 0.5f), Num(F(a + 88) * 0.5f) },
                    ["uvRotateAdd"] = Num(F(a + 108) * 0.5f),
                    ["uvRotate"] = Num(F(a + 112)),
                    ["uvRotateRandom"] = Num(F(a + 116)),
                };
                if (pattern)
                {
                    int num = _d[a + 5], period = BitConverter.ToInt16(_d, a + 8), used = BitConverter.ToInt16(_d, a + 10);
                    int rows = Math.Max(1, num / Math.Max(1, divisions));
                    tj["divisions"] = new object[] { divisions, rows };
                    // the game's UV scale is the cell's size (1 / divisions); our shader cuts the cells itself
                    tj["uvScale"] = new object[] { Num((uvScale[0] == 0 ? 1 : uvScale[0]) * divisions), Num((uvScale[1] == 0 ? 1 : uvScale[1]) * rows) };
                    tj["pattern"] = _d[a + 1] != 0 ? 3 : _d[a + 2] != 0 ? 1 : 2; // random start / clamp / loop
                    tj["patternCount"] = Math.Max(1, used);
                    tj["patternFrames"] = Math.Max(1, period * 2);
                    var table = new List<object>();
                    for (int k = 0; k < Math.Min(32, Math.Max(1, used)); k++) table.Add((int)_d[a + 12 + k]);
                    tj["patternTable"] = table;
                }
                texJson.Add(tj);
            }
            j["textures"] = texJson;

            // ---- Emission ----
            int start = I(o + 0x2C8), end = I(o + 0x2CC), interval = I(o + 0x2D0);
            j["start"] = start * 2;
            bool endless = end == int.MaxValue || end < 0;
            j["oneTime"] = !endless;
            j["duration"] = endless ? 0 : Math.Max(1, end - start) * 2;
            j["rate"] = Num(F(o + 0x2C4));
            j["interval"] = Math.Max(0, interval * 2 - 1); // the player emits every interval + 1 frames at 60 fps
            int life = I(o + 0x31C), lifeRandom = I(o + 0x320);
            if (life == int.MaxValue || life < 0) j["infiniteLife"] = true;
            j["life"] = life * 2;
            j["lifeRandom"] = life > 0 ? Math.Min(100, lifeRandom * 100 / life) : 0;
            j["follow"] = I(o + 0x190); // 0 all, 1 none (world), 2 position only

            // ---- Emitter transform ----
            j["scale"] = Arr(V(o + 0x208, 3));
            j["rotate"] = Arr(V(o + 0x214, 3));
            j["translate"] = Arr(V(o + 0x220, 3, Cm));
            j["rotateRandom"] = Arr(V(o + 0x22C, 3));
            j["translateRandom"] = Arr(V(o + 0x238, 3, Cm));
            j["emitterColor0"] = Arr(V(o + 0x290, 4));
            j["emitterColor1"] = Arr(V(o + 0x2A0, 4));

            // ---- Shape ----
            j["shape"] = I(o + 0x250);
            j["radius"] = Arr(V(o + 0x254, 3, Cm));
            j["sweepStart"] = Num((uint)I(o + 0x260) / 4294967296f * 6.2832f);
            uint arc = (uint)I(o + 0x264);
            j["sweep"] = Num(arc == 0xFFFFFFFF ? 6.2832f : arc / 4294967296f * 6.2832f);
            j["caliber"] = Num(F(o + 0x26C));
            j["latitude"] = Num(F(o + 0x270));
            j["shapeScale"] = Arr(V(o + 0x284, 3));

            // ---- Motion: per 30 fps frame -> per 60 fps frame ----
            j["speed"] = Num(F(o + 0x2D8) * Cm * 0.5f);
            j["dirSpeed"] = Num(F(o + 0x2DC) * Cm * 0.5f);
            j["speedRandom"] = Num(F(o + 0x2E0) * 100);
            j["direction"] = Arr(V(o + 0x2E4, 3));
            j["diffusion"] = Num(F(o + 0x2F0));
            float air = F(o + 0x300);
            j["air"] = Num(air > 0 ? (float)Math.Sqrt(air) : 1);
            j["gravity"] = Arr(V(o + 0x304, 3, Cm * 0.25f));
            j["positionRandom"] = Num(F(o + 0x314) * Cm);

            // ---- Look ----
            j["billboard"] = I(o + 0x328);
            int blend = I(o + 0x244); // eft: 0 normal, 1 add, 2 sub, 3 screen, 4 multiply -> ours: 3 multiply, 4 screen
            j["blend"] = blend == 3 ? 4 : blend == 4 ? 3 : blend;
            j["side"] = I(o + 0x1A0);
            if (regs != null) j["pica"] = e.Pica;

            // colour 0 / 1: constant, random of three, or three keys over the life (time2..4 in percent of the life)
            for (int c = 0; c < 2; c++)
            {
                int src = I(o + 0x428 + 4 * c);
                int t2 = I(o + 0x490 + 4 * c), t3 = I(o + 0x498 + 4 * c), t4 = I(o + 0x4A0 + 4 * c);
                var cols = new float[3][];
                for (int k = 0; k < 3; k++) cols[k] = V(o + 0x430 + 0x30 * c + 0x10 * k, 4);
                j[$"color{c}"] = Arr(new[] { cols[0][0], cols[0][1], cols[0][2] });
                j[$"color{c}Type"] = src == 1 ? 1 : src == 2 ? 2 : 0;
                if (src == 1)
                    j[$"color{c}Keys"] = new List<object> { Key(0, cols[0]), Key(0.5f, cols[1]), Key(1, cols[2]) };
                else if (src == 2)
                    j[$"color{c}Keys"] = new List<object> { Key(0, cols[0]), Key(t2 / 100f, cols[0]), Key(t3 / 100f, cols[1]), Key(t4 / 100f, cols[2]), Key(1, cols[2]) };
            }
            j["colorScale"] = Num(F(o + 0x4B8));
            // alpha: start, +startDiff by time2, held to time3, +endDiff by the end
            float a0 = F(o + 0x4BC), ad1 = F(o + 0x4C0), ad2 = F(o + 0x4C4);
            int at2 = I(o + 0x4C8), at3 = I(o + 0x4CC);
            j["alpha0Type"] = 2;
            j["alpha0"] = Num(a0);
            j["alpha0Keys"] = new List<object>
            {
                new object[] { 0, Num(a0) }, new object[] { Num(at2 / 100f), Num(a0 + ad1) },
                new object[] { Num(at3 / 100f), Num(a0 + ad1) }, new object[] { 1, Num(a0 + ad1 + ad2) },
            };

            // size: the base x (start, +startDiff by time2, held to time3, +endDiff by the end)
            // the 3DS quad spans -1..1 (the size is a half width; the screen-filling mask behind the type's mark is 4 x 2.4,
            // the screen's 5:3): our particles are as wide as their size
            float[] baseSize = V(o + 0x4E4, 2, Cm * 2), s0 = V(o + 0x4EC, 2), sd1 = V(o + 0x4F4, 2), sd2 = V(o + 0x4FC, 2);
            int st2 = I(o + 0x4D8), st3 = I(o + 0x4DC);
            j["size"] = Arr(new[] { baseSize[0], baseSize[1], baseSize[0] });
            j["sizeRandom"] = Arr(new[] { F(o + 0x4E0) * 100, F(o + 0x4E0) * 100, F(o + 0x4E0) * 100 });
            j["sizeKeys"] = new List<object>
            {
                SizeKey(0, s0[0], s0[1]), SizeKey(st2 / 100f, s0[0] + sd1[0], s0[1] + sd1[1]),
                SizeKey(st3 / 100f, s0[0] + sd1[0], s0[1] + sd1[1]), SizeKey(1, s0[0] + sd1[0] + sd2[0], s0[1] + sd1[1] + sd2[1]),
            };

            // rotation: the mode (0 none, 1 X, 2 Y, 3 Z, 4 XYZ); a single axis keeps its values in the first component
            int mode = I(o + 0x18C);
            j["rotationType"] = mode;
            float[] rot = V(o + 0x504, 3), rotRandom = V(o + 0x510, 3), spin = V(o + 0x51C, 3, 0.5f), spinRandom = V(o + 0x528, 3, 0.5f);
            if (mode >= 1 && mode <= 3)
            {
                int axis = mode - 1;
                rot = OnAxis(rot, axis); rotRandom = OnAxis(rotRandom, axis); spin = OnAxis(spin, axis); spinRandom = OnAxis(spinRandom, axis);
            }
            j["rotation"] = Arr(rot); j["rotationRandom"] = Arr(rotRandom); j["spin"] = Arr(spin); j["spinRandom"] = Arr(spinRandom);
            float inertia = F(o + 0x534);
            j["spinResistance"] = Num(inertia > 0 ? (float)Math.Sqrt(inertia) : 1);
            if (regs != null) Classify(regs, j);
            return e;
        }

        // The particle combiner (174 setups over every emitter; six cover 80%) in our particle shader's terms. The game
        // fills the stages' constant colour with the emitter's colour 1 at draw time (the files hold zeros):
        //   texture (x / + texture 1) x colour 0, then buffer + (1 - buffer) x constant  -> mode 2 (colour 1 screens)
        //   texture x constant, then buffer + (1 - buffer) x colour 0                    -> mode 2 with the colours swapped
        //   texture x colour 0 (x constant)                                              -> mode 1 (colour 0 x the constant)
        // an alpha stage subtracting colour 0's alpha (a dissolve)                         -> alpha mode 2.
        static void Classify(Dictionary<int, uint> r, Dictionary<string, object> j)
        {
            uint Reg(int reg) => r.TryGetValue(reg, out var v) ? v : 0;
            bool screenConst = false, screenPrimary = false, timesConst = false, alphaDissolve = false;
            int tex1Op = -1, tex1AlphaOp = -1;
            for (int s = 0; s < StageRegs.Length; s++)
            {
                uint src = Reg(StageRegs[s]), opnd = Reg(StageRegs[s] + 1), op = Reg(StageRegs[s] + 2);
                int c0 = (int)(src & 0xF), c1 = (int)(src >> 4 & 0xF), a0 = (int)(src >> 16 & 0xF), a1 = (int)(src >> 20 & 0xF);
                int oc0 = (int)(opnd & 0xF), opC = (int)(op & 0xF), opA = (int)(op >> 16 & 0xF);
                int oa1 = (int)(opnd >> 16 & 7);
                if (s == 0 && (c0 == 4 || c1 == 4)) tex1Op = opC;
                if (s == 0 && (a0 == 4 || a1 == 4)) tex1AlphaOp = opA;
                // the buffer, inverted, times something: the screen of the previous result with that something
                if (c0 == 0xD && oc0 == 1 && opC == 1)
                {
                    if (c1 == 0xE) screenConst = true;
                    else if (c1 == 0) screenPrimary = true;
                }
                if (opC == 1 && (c0 == 0xE || c1 == 0xE) && !(c0 == 0xD)) timesConst = true;
                if (opA == 5 && (a0 == 0 || a1 == 0) && oa1 == 1) alphaDissolve = true;
            }
            if (screenConst) j["colorCombiner"] = 2;
            else if (screenPrimary && timesConst)
            {
                // colour 0 is the backdrop, the constant (colour 1) tints the texture: swap them
                j["colorCombiner"] = 2;
                foreach (var k in new[] { "", "Type", "Keys" })
                {
                    j.TryGetValue("color0" + k, out var x); j.TryGetValue("color1" + k, out var y);
                    if (y != null) j["color0" + k] = y; else j.Remove("color0" + k);
                    if (x != null) j["color1" + k] = x; else j.Remove("color1" + k);
                }
            }
            else
            {
                j["colorCombiner"] = 1;
                if (timesConst && j["color0"] is List<object> c && j["color1"] is List<object> k1)
                    for (int i = 0; i < 3; i++) c[i] = Num(Convert.ToSingle(c[i]) * Convert.ToSingle(k1[i]));
            }
            j["alphaCombiner"] = alphaDissolve ? 2 : 0;
            if (tex1Op >= 0 || tex1AlphaOp >= 0)
            {
                int Blend(int pica) => pica == 2 ? 1 : pica == 5 ? 2 : 0; // PICA add / subtract / (modulate)
                j["textureBlend"] = new List<object> { Blend(tex1Op), 0, Blend(tex1AlphaOp), 0 };
            }
        }

        // A single-axis value stored in the first component moved to its axis (the others zero).
        static float[] OnAxis(float[] v, int axis)
        {
            var o = new float[3];
            o[axis] = v[axis] != 0 ? v[axis] : v[0];
            return o;
        }

        static object Key(float t, float[] c) => new object[] { Num(t), Num(c[0]), Num(c[1]), Num(c[2]) };
        static object SizeKey(float t, float x, float y) => new object[] { Num(t), Num(x), Num(y), Num(x) };

        // The texture unit's wrap (0x083 unit 0, 0x093 unit 1: bits 8-10 T, 12-14 S in PICA order 0 clamp, 1 border,
        // 2 repeat, 3 mirror) -> the move effects' codes (0 mirror, 1 repeat, 2 clamp).
        static int WrapOf(Dictionary<int, uint> regs, int slot, int axis)
        {
            if (regs == null || !regs.TryGetValue(slot == 0 ? 0x083 : 0x093, out var v)) return 1; // repeat
            int pica = (int)(v >> (axis == 0 ? 12 : 8) & 7);
            return pica == 2 ? 1 : pica == 3 ? 0 : 2;
        }

        // PICA texture unit format register value -> our PicaTexture code.
        static int PicaFromRegister(int f) => f switch
        {
            0 => PicaTexture.RGBA8, 1 => PicaTexture.RGB8, 2 => PicaTexture.RGBA5551, 3 => PicaTexture.RGB565, 4 => PicaTexture.RGBA4,
            5 => PicaTexture.LA8, 6 => PicaTexture.HiLo8, 7 => PicaTexture.L8, 8 => PicaTexture.A8, 9 => PicaTexture.LA4,
            10 => PicaTexture.L4, 11 => PicaTexture.A4, 12 => PicaTexture.ETC1, 13 => PicaTexture.ETC1A4, _ => -1,
        };

        static readonly int[] StageRegs = { 0xC0, 0xC8, 0xD0, 0xD8, 0xF0, 0xF8 };

        // The PICA state as the stage models' material description (GfModelConvert.Describe): combiner stages
        // ("src,operand,op,scale" per stage), buffer, colour operation, blending, alpha test, depth, cull; the stages'
        // constant colours as Constant0..5.
        static void Describe(Dictionary<int, uint> r, Dictionary<string, string> o)
        {
            uint Reg(int reg) => r.TryGetValue(reg, out var v) ? v : 0;
            var stages = new List<string>();
            foreach (int s in StageRegs) stages.Add($"{Reg(s):X},{Reg(s + 1):X},{Reg(s + 2):X},{Reg(s + 4):X}");
            o["pica.tev"] = string.Join(";", stages);
            o["pica.tevBuffer"] = $"{Reg(0xE0):X},{Reg(0xFD):X}";
            o["pica.colorOp"] = Reg(0x100).ToString("X");
            o["pica.blend"] = Reg(0x101).ToString("X");
            o["pica.blendColor"] = Reg(0x103).ToString("X");
            o["pica.alphaTest"] = Reg(0x104).ToString("X");
            o["pica.depth"] = Reg(0x107).ToString("X");
            o["pica.cull"] = Reg(0x040).ToString("X");
            for (int s = 0; s < 6; s++)
            {
                uint c = Reg(StageRegs[s] + 3); // the stage's constant colour, RGBA bytes
                o["Constant" + s] = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", (c & 0xFF) / 255f, (c >> 8 & 0xFF) / 255f, (c >> 16 & 0xFF) / 255f, (c >> 24) / 255f);
            }
        }

        static object Num(float f) => float.IsNaN(f) || float.IsInfinity(f) ? 0 : Math.Round(f, 5);
        static List<object> Arr(float[] v) { var o = new List<object>(); foreach (var f in v) o.Add(Num(f)); return o; }

        // The whole file as our effect JSON: {"effects": {set name: [emitters]}}, textures named for the caller to save.
        public string ToJson()
        {
            var effects = new Dictionary<string, object>();
            foreach (var set in Sets)
            {
                var list = new List<object>();
                foreach (var e in set.Emitters) list.Add(e.Json);
                effects[set.Name] = list;
            }
            var w = new JsonWriter(false);
            Write(w, new Dictionary<string, object> { ["version"] = 1, ["fps"] = 60, ["source"] = "usum", ["effects"] = effects });
            return w.ToString();
        }

        static void Write(JsonWriter w, object v)
        {
            switch (v)
            {
                case null: w.Value((string)null); break;
                case string s: w.Value(s); break;
                case bool b: w.Value(b); break;
                case int i: w.Value(i); break;
                case double d: w.Value(d); break;
                case float f: w.Value((double)f); break;
                case Dictionary<string, object> o: w.BeginObject(); foreach (var kv in o) { w.Name(kv.Key); Write(w, kv.Value); } w.EndObject(); break;
                case Dictionary<string, string> o: w.BeginObject(); foreach (var kv in o) { w.Name(kv.Key); w.Value(kv.Value); } w.EndObject(); break;
                case System.Collections.IEnumerable list: w.BeginArray(); foreach (var x in list) Write(w, x); w.EndArray(); break;
                default: w.Value(Convert.ToDouble(v, CultureInfo.InvariantCulture)); break;
            }
        }

        public byte[] Png(Texture t) => Export.Png.Encode(t.Rgba, t.Width, t.Height);
    }
}
