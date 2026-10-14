using System;
using System.Collections.Generic;
using System.Text;

namespace PokeModelExporter.Formats
{
    // NintendoWare particle effects (.ptcl, magic "VFXB"), as used by Scarlet/Violet (VFX version 22).
    // Emitter layout ported from KillzXGaming/EffectLibrary (MIT, Copyright (c) 2024 KillzXGaming), which
    // reads versions up to 0x16; only the fields a re-creation needs are kept, the rest is skipped by size.
    //
    // File: 0x40-byte header, then sections. Each section: magic, size, children offset, next offset,
    // attribute offset, binary offset (all relative to the section, 0xFFFFFFFF = none), pad, child count.
    //   ESTA -> ESET (name at binary+0x10) -> EMTR (EmitterData at binary; children = child emitters;
    //   attributes = "EA**" emitter animations and others) ; GRTF (textures: GTNT id -> name, then BNTX) ;
    //   PRMA / G3PR (primitive meshes) ; GRSN / GRSC (Switch shaders, unused).
    public sealed class VfxbFile
    {
        public int Version;
        public List<VfxbEmitterSet> Sets = new List<VfxbEmitterSet>();
        public Dictionary<ulong, string> TextureNames = new Dictionary<ulong, string>();
        public ArraySegment<byte> Bntx; // the textures, a whole BNTX file (empty when there are none)
        public Dictionary<ulong, BfresPrimitives.Primitive> Primitives = new Dictionary<ulong, BfresPrimitives.Primitive>();

        public static VfxbFile Read(byte[] data)
        {
            if (Encoding.ASCII.GetString(data, 0, 4) != "VFXB") throw new InvalidOperationException("not a VFXB file");
            var file = new VfxbFile { Version = BitConverter.ToUInt16(data, 0x0A) };
            if (file.Version != 22) throw new NotSupportedException($"VFXB version {file.Version} (only 22, Scarlet/Violet, is mapped)");
            int pos = BitConverter.ToUInt16(data, 0x16);
            while (true)
            {
                var s = new Section(data, pos);
                if (s.Magic == "ESTA") file.ReadSets(data, s);
                else if (s.Magic == "GRTF") file.ReadTextures(data, s);
                else if (s.Magic == "G3PR") file.Primitives = BfresPrimitives.ReadSection(data, s.Pos);
                if (s.Next == uint.MaxValue) break;
                pos += (int)s.Next;
            }
            return file;
        }

        void ReadSets(byte[] data, Section esta)
        {
            int pos = esta.Pos + (int)esta.Children;
            for (int i = 0; i < esta.ChildCount; i++)
            {
                var s = new Section(data, pos);
                var set = new VfxbEmitterSet { Name = FixedString(data, s.Pos + (int)s.Binary + 0x10, 64) };
                int e = s.Pos + (int)s.Children;
                for (int k = 0; k < s.ChildCount; k++) e = ReadEmitter(data, e, set.Emitters);
                Sets.Add(set);
                if (s.Next == uint.MaxValue) break;
                pos += (int)s.Next;
            }
        }

        // Reads the emitter at pos (and its children), returns the next sibling's position.
        int ReadEmitter(byte[] data, int pos, List<VfxbEmitter> into)
        {
            var s = new Section(data, pos);
            var emitter = VfxbEmitter.Parse(data, s.Pos + (int)s.Binary);
            emitter.BinarySize = (int)((s.Attributes != uint.MaxValue ? s.Attributes : s.Size) - s.Binary);
            if (s.Attributes != uint.MaxValue)
            {
                int a = s.Pos + (int)s.Attributes;
                while (true)
                {
                    var attr = new Section(data, a);
                    if (attr.Magic.StartsWith("EA", StringComparison.Ordinal))
                        emitter.Animations[attr.Magic] = VfxbAnimation.Parse(data, attr.Pos + (int)attr.Binary);
                    else
                    {
                        emitter.Attributes.Add(attr.Magic);
                        int start = attr.Pos + (int)attr.Binary, end = attr.Pos + (int)attr.Size;
                        if (attr.Binary != uint.MaxValue && end > start && end <= data.Length)
                        {
                            var floats = new float[(end - start) / 4];
                            for (int i = 0; i < floats.Length; i++) floats[i] = BitConverter.ToSingle(data, start + 4 * i);
                            emitter.AttributeData[attr.Magic] = floats;
                        }
                    }
                    if (attr.Next == uint.MaxValue) break;
                    a += (int)attr.Next;
                }
            }
            if (s.Children != uint.MaxValue)
            {
                int c = s.Pos + (int)s.Children;
                for (int k = 0; k < s.ChildCount; k++) c = ReadEmitter(data, c, emitter.Children);
            }
            into.Add(emitter);
            return s.Next == uint.MaxValue ? -1 : pos + (int)s.Next;
        }

        // GRTF: child GTNT = descriptors { u64 id, u32 offset to next (0 = last), i32 length, name };
        // binary = the BNTX file.
        void ReadTextures(byte[] data, Section grtf)
        {
            if (grtf.Children != uint.MaxValue)
            {
                var table = new Section(data, grtf.Pos + (int)grtf.Children);
                int p = table.Pos + (int)table.Binary, end = p + (int)table.Size;
                while (p < end)
                {
                    ulong id = BitConverter.ToUInt64(data, p);
                    uint next = BitConverter.ToUInt32(data, p + 8);
                    int length = BitConverter.ToInt32(data, p + 12);
                    TextureNames[id] = FixedString(data, p + 16, length);
                    if (next == 0) break;
                    p += (int)next;
                }
            }
            if (grtf.Size > 0 && grtf.Binary != uint.MaxValue)
            {
                int start = grtf.Pos + (int)grtf.Binary;
                Bntx = new ArraySegment<byte>(data, start, Math.Min((int)grtf.Size, data.Length - start));
            }
        }

        internal static string FixedString(byte[] data, int at, int max)
        {
            int n = 0;
            while (n < max && at + n < data.Length && data[at + n] != 0) n++;
            return Encoding.UTF8.GetString(data, at, n);
        }

        readonly struct Section
        {
            public readonly int Pos;
            public readonly string Magic;
            public readonly uint Size, Children, Next, Attributes, Binary;
            public readonly int ChildCount;

            public Section(byte[] d, int pos)
            {
                Pos = pos;
                Magic = Encoding.ASCII.GetString(d, pos, 4);
                Size = BitConverter.ToUInt32(d, pos + 4);
                Children = BitConverter.ToUInt32(d, pos + 8);
                Next = BitConverter.ToUInt32(d, pos + 12);
                Attributes = BitConverter.ToUInt32(d, pos + 16);
                Binary = BitConverter.ToUInt32(d, pos + 20);
                ChildCount = BitConverter.ToUInt16(d, pos + 28);
            }
        }
    }

    public sealed class VfxbEmitterSet
    {
        public string Name;
        public List<VfxbEmitter> Emitters = new List<VfxbEmitter>();
    }

    // Key frames: XYZ + time (0..1 of the particle's life for particle tables; frames for emitter animations).
    public struct VfxbKey
    {
        public float X, Y, Z, Time;
    }

    public sealed class VfxbAnimation
    {
        public bool Enabled, Loop, RandomStart;
        public VfxbKey[] Keys;

        internal static VfxbAnimation Parse(byte[] d, int p)
        {
            var a = new VfxbAnimation { Enabled = d[p] != 0, Loop = d[p + 1] != 0, RandomStart = d[p + 2] != 0 };
            int n = BitConverter.ToInt32(d, p + 4);
            a.Keys = new VfxbKey[Math.Max(0, Math.Min(n, 64))];
            for (int i = 0; i < a.Keys.Length; i++)
            {
                int k = p + 12 + 16 * i;
                a.Keys[i] = new VfxbKey { X = F(d, k), Y = F(d, k + 4), Z = F(d, k + 8), Time = F(d, k + 12) };
            }
            return a;
        }

        static float F(byte[] d, int p) => BitConverter.ToSingle(d, p);
    }

    // One emitter's parameters (EffectLibrary EFT2 EmitterData, fields valid for version 22). Times are frames
    // (60 fps); colours linear 0..n (can exceed 1 for HDR); per-particle tables are 8 keys with time 0..1 of life.
    public sealed class VfxbEmitter
    {
        public string Name;
        public List<VfxbEmitter> Children = new List<VfxbEmitter>();
        public Dictionary<string, VfxbAnimation> Animations = new Dictionary<string, VfxbAnimation>();
        public List<string> Attributes = new List<string>();
        // Raw attribute payloads as floats (CSDP = the custom shader's per-emitter parameters, read by its fragment program).
        public Dictionary<string, float[]> AttributeData = new Dictionary<string, float[]>();

        // EmitterStatic
        public uint[] StaticFlags = new uint[4]; // EmitterStatic +0 (vertex programs read buf6[80]: per-texture UV options)
        public float ColorScale; // multiplies colour 0 and 1 (vertex program: attrib0/1 = colour x emitter colour x ColorScale)
        // Shader type 2: texture 0's red/alpha (-1..1) offset texture 1's UVs by this (u, v), times the ParamKeys curve.
        public float[] DistortionStrength = new float[2];
        public int NumColor0Keys, NumAlpha0Keys, NumColor1Keys, NumAlpha1Keys, NumScaleKeys, NumParamKeys;
        public float[] Gravity = new float[3]; // direction * scale (EmitterStatic)
        public float AirResistance;
        public VfxbTexturePattern[] Patterns = new VfxbTexturePattern[3];
        public VfxbTextureScroll[] Scrolls = new VfxbTextureScroll[3];
        public VfxbKey[] Color0Keys, Alpha0Keys, Color1Keys, Alpha1Keys, ScaleKeys, ParamKeys;
        public float[] RotateInit = new float[3], RotateInitRandom = new float[3], RotateAdd = new float[3], RotateAddRandom = new float[3];
        public float RotateResistance;

        // EmitterInfo. Draw: the emitter draws its own particles (0 on parents that only spawn children: Soft-Boiled's
        // white "oya" squares, Mega Drain's gp; every other emitter has 1).
        public bool Draw = true;
        public byte SortType, CalcType, FollowType;
        public bool FadeOnEmitEnd, AlphaFadeOnFade, ScaleFadeOnFade, AlphaFadeIn, ScaleFadeIn;
        public int AlphaFadeTime, FadeInTime;
        public float[] Translate = new float[3], TranslateRandom = new float[3], Rotate = new float[3], RotateRandom = new float[3], Scale = new float[3];
        public float[] EmitterColor0 = new float[4], EmitterColor1 = new float[4];

        // Inheritance (child emitters)
        public bool InheritVelocity, InheritScale, InheritRotate, InheritColor0, InheritColor1, InheritAlpha0, InheritAlpha1;
        public float InheritVelocityRate, InheritScaleRate;
        // Drawing order and visibility around children: DrawPath, PreDraw (draw before the parent), alpha inherited each
        // frame, and EnableEmitterParticle (EffectLibrary's name; whether this emitter draws its own particles).
        public bool InheritDrawPath, PreDraw, InheritAlpha0EachFrame, InheritAlpha1EachFrame, EnableEmitterParticle;

        // Emission
        public bool OneTime, WorldGravity, EmitByDistance;
        public int EmitStart, EmitTiming, EmitDuration, Interval;
        public float Rate, RateRandom, IntervalRandom, PositionRandom, EmissionGravityScale;
        public float[] EmissionGravityDir = new float[3];

        // Shape
        public byte VolumeType, ArcType;
        public float SweepLongitude, SweepLatitude, SweepStart, CaliberRatio, LineCenter, LineLength;
        public float[] VolumeRadius = new float[3], VolumeFormScale = new float[3];
        public int CircleDivisions;

        // Render state
        public bool BlendEnabled, DepthTest, DepthWrite, AlphaTest;
        public byte BlendType, DisplaySide, AlphaFunc;
        public float AlphaThreshold;

        // ParticleData
        public bool InfiniteLife;
        public byte BillboardType, RotationType, OffsetType;
        public int Life, LifeRandom;
        public float MomentumRandom;
        public ulong PrimitiveId, PrimitiveExId;

        // Combiner
        public byte ColorCombiner, AlphaCombiner, Texture1ColorBlend, Texture2ColorBlend, PrimitiveColorBlend,
            Texture1AlphaBlend, Texture2AlphaBlend, PrimitiveAlphaBlend, ShaderType, ApplyAlpha;
        // How each texture's colour / alpha enters the combiner (texture 0, 1, 2, then primitive colour / alpha).
        public byte[] TextureColorInput = new byte[3], TextureAlphaInput = new byte[3];
        public byte PrimitiveColorInput, PrimitiveAlphaInput;

        // Velocity
        public float AllDirection, DirectionalScale, DiffusionAngle, XZDiffusion, VelocityRandom, EmitterVelocityInherit;
        public float[] Direction = new float[3], Diffusion = new float[3];

        // Colour / scale
        public bool SoftParticle, FresnelAlpha;
        public byte Color0Type, Color1Type, Alpha0Type, Alpha1Type; // 0 constant, 1 random, 2 animated (8 keys)
        public float[] Color0 = new float[3], Color1 = new float[3];
        public float Alpha0, Alpha1;
        public float[] ParticleScale = new float[3], ParticleScaleRandom = new float[3];

        public VfxbSampler[] Samplers = new VfxbSampler[3];
        public VfxbTextureAnim[] TextureAnims = new VfxbTextureAnim[3];

        public int ShaderIndex, ComputeShaderIndex, UserShaderIndex1, UserShaderIndex2, CustomShaderIndex;
        public byte ShaderRefType;
        public int ParsedSize, BinarySize; // bytes read vs. the section's binary size (equal when the layout is right)

        internal static VfxbEmitter Parse(byte[] d, int start)
        {
            var r = new Cursor(d, start);
            var e = new VfxbEmitter();
            r.Skip(16);
            e.Name = VfxbFile.FixedString(d, r.Pos, 64);
            r.Skip(64);

            // EmitterStatic
            e.StaticFlags = new[] { (uint)r.I32(), (uint)r.I32(), (uint)r.I32(), (uint)r.I32() }; // flags 1-4 (shaders test bits of the first)
            e.NumColor0Keys = r.I32(); e.NumAlpha0Keys = r.I32(); e.NumColor1Keys = r.I32(); e.NumAlpha1Keys = r.I32();
            e.NumScaleKeys = r.I32(); e.NumParamKeys = r.I32();
            r.Skip(8);
            r.Skip(4 * 10); // loop rates, loop randoms
            r.Skip(8);
            var gravityDir = r.Floats(3);
            float gravityScale = r.F32();
            for (int i = 0; i < 3; i++) e.Gravity[i] = gravityDir[i] * gravityScale;
            e.AirResistance = r.F32();
            r.Skip(4 * 3 + 4 * 4 + 4 * 8); // unknowns, center, offset, fluctuation amplitude/cycle/phase
            e.DistortionStrength = r.Floats(2); // EmitterStatic +176 (fragment buf6[256]): UV offset per unit of the distortion texture
            r.Skip(4 * 2); // coefficients
            for (int i = 0; i < 3; i++)
            {
                e.Patterns[i] = new VfxbTexturePattern { Count = r.F32(), Frequency = r.F32(), CountRandom = r.F32() };
                r.Skip(4);
                e.Patterns[i].Table = new int[32];
                for (int k = 0; k < 32; k++) e.Patterns[i].Table[k] = r.I32();
            }
            for (int i = 0; i < 3; i++)
            {
                var f = r.Floats(20);
                e.Scrolls[i] = new VfxbTextureScroll
                {
                    ScrollAdd = new[] { f[0], f[1] }, Scroll = new[] { f[2], f[3] }, ScrollRandom = new[] { f[4], f[5] },
                    ScaleAdd = new[] { f[6], f[7] }, Scale = new[] { f[8], f[9] }, ScaleRandom = new[] { f[10], f[11] },
                    RotationAdd = f[12], Rotation = f[13], RotationRandom = f[14], RotationType = f[15],
                    Unused = new[] { f[16], f[17] }, UVDivisions = new[] { f[18], f[19] },
                };
            }
            e.ColorScale = r.F32(); r.Skip(12); // colour scale + 3
            e.Color0Keys = r.Keys(e.NumColor0Keys);
            e.Alpha0Keys = r.Keys(e.NumAlpha0Keys);
            e.Color1Keys = r.Keys(e.NumColor1Keys);
            e.Alpha1Keys = r.Keys(e.NumAlpha1Keys);
            r.Skip(4 * 8 + 4 * 2 + 4 * 2 + 4 * 4); // soft/fresnel/near/far, decal, alpha threshold, vel-to-scale, soft particle
            e.ScaleKeys = r.Keys(e.NumScaleKeys);
            e.ParamKeys = r.Keys(Math.Max(1, e.NumParamKeys)); // parameter animation (custom shaders: e.g. distortion strength)
            e.RotateInit = r.Floats(3); r.Skip(4);
            e.RotateInitRandom = r.Floats(3); r.Skip(4);
            e.RotateAdd = r.Floats(3); e.RotateResistance = r.F32();
            e.RotateAddRandom = r.Floats(3); r.Skip(4);
            r.Skip(16); // scale limit by distance, padding

            // EmitterInfo
            e.Draw = r.U8() != 0; e.SortType = r.U8(); e.CalcType = r.U8(); e.FollowType = r.U8();
            e.FadeOnEmitEnd = r.U8() != 0; e.AlphaFadeOnFade = r.U8() != 0; e.ScaleFadeOnFade = r.U8() != 0; r.U8();
            r.U8(); r.U8(); r.U8(); e.AlphaFadeIn = r.U8() != 0; e.ScaleFadeIn = r.U8() != 0; r.Skip(3);
            r.Skip(8); // random seed, draw path
            e.AlphaFadeTime = r.I32(); e.FadeInTime = r.I32();
            e.Translate = r.Floats(3); e.TranslateRandom = r.Floats(3);
            e.Rotate = r.Floats(3); e.RotateRandom = r.Floats(3);
            e.Scale = r.Floats(3);
            e.EmitterColor0 = r.Floats(4); e.EmitterColor1 = r.Floats(4);
            r.Skip(12); // emission range near/far/ratio

            // Inheritance
            e.InheritVelocity = r.U8() != 0; e.InheritScale = r.U8() != 0; e.InheritRotate = r.U8() != 0; r.U8();
            e.InheritColor0 = r.U8() != 0; e.InheritColor1 = r.U8() != 0; e.InheritAlpha0 = r.U8() != 0; e.InheritAlpha1 = r.U8() != 0;
            e.InheritDrawPath = r.U8() != 0; e.PreDraw = r.U8() != 0; e.InheritAlpha0EachFrame = r.U8() != 0; e.InheritAlpha1EachFrame = r.U8() != 0;
            e.EnableEmitterParticle = r.U8() != 0; r.Skip(3);
            e.InheritVelocityRate = r.F32(); e.InheritScaleRate = r.F32();

            // Emission
            e.OneTime = r.U8() != 0; e.WorldGravity = r.U8() != 0; e.EmitByDistance = r.U8() != 0; r.U8();
            e.EmitStart = r.I32(); e.EmitTiming = r.I32(); e.EmitDuration = r.I32();
            e.Rate = r.F32(); e.RateRandom = r.F32(); e.Interval = r.I32(); e.IntervalRandom = r.F32();
            e.PositionRandom = r.F32(); e.EmissionGravityScale = r.F32(); e.EmissionGravityDir = r.Floats(3);
            r.Skip(4 * 5); // emit by distance

            // Shape
            e.VolumeType = r.U8(); r.U8(); e.ArcType = r.U8(); r.Skip(5);
            e.SweepLongitude = r.F32(); e.SweepLatitude = r.F32(); e.SweepStart = r.F32(); r.F32();
            e.CaliberRatio = r.F32(); e.LineCenter = r.F32(); e.LineLength = r.F32();
            e.VolumeRadius = r.Floats(3); e.VolumeFormScale = r.Floats(3);
            r.Skip(4 + 8); // primitive emit type, primitive index
            e.CircleDivisions = r.I32(); r.Skip(12);
            r.Skip(8);

            // Render state
            e.BlendEnabled = r.U8() != 0; e.DepthTest = r.U8() != 0; r.U8(); e.DepthWrite = r.U8() != 0;
            e.AlphaTest = r.U8() != 0; e.AlphaFunc = r.U8(); e.BlendType = r.U8(); e.DisplaySide = r.U8();
            e.AlphaThreshold = r.F32(); r.Skip(4);

            // ParticleData
            e.InfiniteLife = r.U8() != 0; r.U8(); e.BillboardType = r.U8(); e.RotationType = r.U8(); e.OffsetType = r.U8();
            r.Skip(11);
            e.Life = r.I32(); e.LifeRandom = r.I32(); e.MomentumRandom = r.F32(); r.Skip(4);
            e.PrimitiveId = r.U64(); e.PrimitiveExId = r.U64();
            r.Skip(12 + 4 * 5); // loop flags, loop rates

            // Combiner (version < 36)
            e.ColorCombiner = r.U8(); e.AlphaCombiner = r.U8(); e.Texture1ColorBlend = r.U8(); e.Texture2ColorBlend = r.U8();
            e.PrimitiveColorBlend = r.U8(); e.Texture1AlphaBlend = r.U8(); e.Texture2AlphaBlend = r.U8(); e.PrimitiveAlphaBlend = r.U8();
            for (int i = 0; i < 3; i++) e.TextureColorInput[i] = r.U8();
            for (int i = 0; i < 3; i++) e.TextureAlphaInput[i] = r.U8();
            e.PrimitiveColorInput = r.U8(); e.PrimitiveAlphaInput = r.U8();
            e.ShaderType = r.U8(); e.ApplyAlpha = r.U8(); r.Skip(2 + 4);

            // Shader references (version 22..49): type, then the shader variation index (GRSC), compute, user 1 / 2,
            // custom; flags, switch, extra index, _, user defines. Then the action.
            e.ShaderRefType = r.U8(); r.Skip(3);
            e.ShaderIndex = r.I32(); e.ComputeShaderIndex = r.I32(); e.UserShaderIndex1 = r.I32(); e.UserShaderIndex2 = r.I32(); e.CustomShaderIndex = r.I32();
            r.Skip(8 + 8 + 4 + 4 + 32);
            r.Skip(4);

            // Velocity
            e.AllDirection = r.F32(); e.DirectionalScale = r.F32(); e.Direction = r.Floats(3);
            e.DiffusionAngle = r.F32(); e.XZDiffusion = r.F32(); e.Diffusion = r.Floats(3);
            e.VelocityRandom = r.F32(); e.EmitterVelocityInherit = r.F32();

            // Colour
            e.SoftParticle = r.U8() != 0; e.FresnelAlpha = r.U8() != 0; r.Skip(6);
            e.Color0Type = r.U8(); e.Color1Type = r.U8(); e.Alpha0Type = r.U8(); e.Alpha1Type = r.U8();
            e.Color0 = r.Floats(3); e.Alpha0 = r.F32(); e.Color1 = r.Floats(3); e.Alpha1 = r.F32();

            // Scale
            e.ParticleScale = r.Floats(3); e.ParticleScaleRandom = r.Floats(3); r.Skip(4 + 8);

            r.Skip(12); // fluctuation flags

            for (int i = 0; i < 3; i++)
            {
                var s = new VfxbSampler { TextureId = r.U64(), WrapU = r.U8(), WrapV = r.U8(), Filter = r.U8() };
                r.Skip(1 + 8 + 4 + 8);
                e.Samplers[i] = s;
            }
            for (int i = 0; i < 3; i++)
            {
                e.TextureAnims[i] = new VfxbTextureAnim
                {
                    PatternType = r.U8(), Scroll = r.U8() != 0, Rotate = r.U8() != 0, Scale = r.U8() != 0,
                    Repeat = r.U8(), InvertRandomU = r.U8() != 0, InvertRandomV = r.U8() != 0, PatternLoopRandom = r.U8() != 0,
                };
                r.Skip(8);
            }
            r.Skip(0x40);
            e.ParsedSize = r.Pos - start;
            return e;
        }

        sealed class Cursor
        {
            readonly byte[] _d;
            public int Pos;
            public Cursor(byte[] d, int pos) { _d = d; Pos = pos; }
            public void Skip(int n) => Pos += n;
            public byte U8() => _d[Pos++];
            public int I32() { int v = BitConverter.ToInt32(_d, Pos); Pos += 4; return v; }
            public ulong U64() { ulong v = BitConverter.ToUInt64(_d, Pos); Pos += 8; return v; }
            public float F32() { float v = BitConverter.ToSingle(_d, Pos); Pos += 4; return v; }
            public float[] Floats(int n) { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = F32(); return a; }

            // An 8-key table (always 8 slots of XYZ + time); returns the first `used` keys.
            public VfxbKey[] Keys(int used)
            {
                var keys = new VfxbKey[Math.Max(0, Math.Min(used, 8))];
                for (int i = 0; i < 8; i++)
                {
                    var k = new VfxbKey { X = F32(), Y = F32(), Z = F32(), Time = F32() };
                    if (i < keys.Length) keys[i] = k;
                }
                return keys;
            }
        }
    }

    public struct VfxbTexturePattern
    {
        public float Count, Frequency, CountRandom; // flipbook frames, frames per pattern
        public int[] Table;
    }

    public struct VfxbTextureScroll
    {
        // Per frame: scroll and scale change; Scale = the UV scale (1 = once across, -1 flips); Random = per-particle range.
        public float[] ScrollAdd, Scroll, ScrollRandom, ScaleAdd, Scale, ScaleRandom, Unused, UVDivisions;
        public float RotationAdd, Rotation, RotationRandom, RotationType;
    }

    public struct VfxbSampler
    {
        public ulong TextureId; // ulong.MaxValue = unused
        public byte WrapU, WrapV, Filter; // wrap: 0 mirror, 1 repeat, 2 clamp, 3 mirror once
        public bool Used => TextureId != ulong.MaxValue;
    }

    public struct VfxbTextureAnim
    {
        public byte PatternType, Repeat; // repeat: 0 1x1, 1 2x1, 2 1x2, 3 2x2 (quarter textures + mirror wrap = whole shapes)
        public bool Scroll, Rotate, Scale, InvertRandomU, InvertRandomV, PatternLoopRandom;
    }
}
