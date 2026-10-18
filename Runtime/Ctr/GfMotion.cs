using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PokeModelExporter.Export;

namespace PokeModelExporter.Ctr
{
    // GameFreak's 3DS motion packs (magic 0x00060000): a section table (u32 type, length, offset) and per section:
    //   0 header    u32 frame count, u8 loop, u8 blend, 2 bytes, bounding box (2 x vec3), hash
    //   1 bones     u32 count, u32 names length, names (length-prefixed); per bone u32 flags, u32 length and nine
    //               channels: scale XYZ, rotation XYZ (Euler, radians), translation XYZ
    //   3 UVs       u32 materials, u32 names length, u32 tracks per material, names; per track u32 texture unit,
    //               u32 flags, u32 length and five channels: scale U / V, rotation, translation U / V
    //   5 colours   same layout as UVs; per track u32 constant index, flags, length, four channels R G B A
    //   8 lights    u32 count, u32 names length, names; per light u32 type, u32 ?, u32 flags, u32 0, u32 length,
    //               u32 RGBA colour, then ten channels (constant in every pack: position XYZ, direction XYZ, ...)
    //   9 cameras   u32 count, u32 names length, names; per camera u32 flags, u32 0, u32 length, then ten channels:
    //               near, far (cm), vertical field of view (radians), aspect, rotation XYZ (Euler, radians), position
    //               XYZ (cm). Every camera (486) and light (183) section parses exactly.
    //   6 visibility, 7 material switches (not read yet)
    // Each channel's 3-bit code (flags >> 3 x channel): 0 not animated, 1 constant 0, 2 constant 1, 3 a float,
    // 4 quantized Hermite keys (u32 count, frames as u8 (<= 255 frames) or u16, aligned to 4, value scale / offset,
    // slope scale / offset, then u16 value + u16 slope per key, each u16 / 65535 x scale + offset), 5 float keys
    // (frames, then float value + float slope per key). Every pack in Ultra Sun/Moon's a/0/8/7 parses exactly.
    public sealed class GfMotion
    {
        public sealed class Curve
        {
            public bool Animated;           // false: channel not animated (keep the bind / material value)
            public float Constant;          // when Frames is null
            public float[] Frames, Values, Slopes; // Hermite keys (slopes per frame)
        }

        public sealed class BoneTrack { public string Bone; public Curve[] Channels = new Curve[9]; }
        public sealed class UvTrack { public string Material; public int Unit; public Curve[] Channels = new Curve[5]; }
        public sealed class ColorTrack { public string Material; public int Index; public Curve[] Channels = new Curve[4]; }
        public sealed class CameraTrack { public string Name; public Curve[] Channels = new Curve[10]; }
        public sealed class LightTrack { public string Name; public int Type, Unknown; public uint Color; public Curve[] Channels = new Curve[10]; }

        public int FrameCount;
        public bool Loop;
        public List<BoneTrack> Bones = new List<BoneTrack>();
        public List<UvTrack> Uvs = new List<UvTrack>();
        public List<ColorTrack> Colors = new List<ColorTrack>();
        public List<CameraTrack> Cameras = new List<CameraTrack>();
        public List<LightTrack> Lights = new List<LightTrack>();
        public List<int> OtherSections = new List<int>();

        public static bool IsMotion(byte[] d) => d != null && d.Length > 16 && BitConverter.ToUInt32(d, 0) == 0x00060000;

        public static GfMotion Read(byte[] d)
        {
            if (!IsMotion(d)) throw new InvalidDataException("not a GF motion pack");
            var m = new GfMotion();
            int n = BitConverter.ToInt32(d, 4);
            var sections = new List<(int Type, int Length, int At)>();
            for (int i = 0; i < n; i++) sections.Add((BitConverter.ToInt32(d, 8 + 12 * i), BitConverter.ToInt32(d, 12 + 12 * i), BitConverter.ToInt32(d, 16 + 12 * i)));
            foreach (var s in sections)
                if (s.Type == 0) { m.FrameCount = BitConverter.ToInt32(d, s.At); m.Loop = d[s.At + 4] != 0; }
            foreach (var s in sections)
                switch (s.Type)
                {
                    case 0: break;
                    case 1: ReadBones(d, s.At, m); break;
                    case 3: ReadTracks(d, s.At, m, 5, (name, index, curves) => m.Uvs.Add(new UvTrack { Material = name, Unit = index, Channels = curves })); break;
                    case 5: ReadTracks(d, s.At, m, 4, (name, index, curves) => m.Colors.Add(new ColorTrack { Material = name, Index = index, Channels = curves })); break;
                    case 8: ReadLights(d, s.At, m); break;
                    case 9: ReadCameras(d, s.At, m); break;
                    default: m.OtherSections.Add(s.Type); break;
                }
            return m;
        }

        static List<string> Names(byte[] d, int o, int count)
        {
            var list = new List<string>(count);
            for (int i = 0; i < count; i++) { int len = d[o]; list.Add(Encoding.ASCII.GetString(d, o + 1, len)); o += 1 + len; }
            return list;
        }

        static void ReadBones(byte[] d, int o, GfMotion m)
        {
            int count = BitConverter.ToInt32(d, o), namesLength = BitConverter.ToInt32(d, o + 4);
            var names = Names(d, o + 8, count);
            int p = o + 8 + namesLength;
            for (int b = 0; b < count; b++)
            {
                uint flags = BitConverter.ToUInt32(d, p); int length = BitConverter.ToInt32(d, p + 4);
                var t = new BoneTrack { Bone = names[b] };
                int q = p + 8;
                for (int c = 0; c < 9; c++) t.Channels[c] = ReadCurve(d, ref q, (int)(flags >> (3 * c) & 7), m.FrameCount);
                m.Bones.Add(t);
                p += 8 + length;
            }
        }

        static void ReadCameras(byte[] d, int o, GfMotion m)
        {
            int count = BitConverter.ToInt32(d, o), namesLength = BitConverter.ToInt32(d, o + 4);
            var names = Names(d, o + 8, count);
            int p = o + 8 + namesLength;
            for (int i = 0; i < count; i++)
            {
                uint flags = BitConverter.ToUInt32(d, p); int length = BitConverter.ToInt32(d, p + 8);
                var t = new CameraTrack { Name = names[i] };
                int q = p + 12;
                for (int c = 0; c < 10; c++) t.Channels[c] = ReadCurve(d, ref q, (int)(flags >> (3 * c) & 7), m.FrameCount);
                m.Cameras.Add(t);
                p += 12 + length;
            }
        }

        static void ReadLights(byte[] d, int o, GfMotion m)
        {
            int count = BitConverter.ToInt32(d, o), namesLength = BitConverter.ToInt32(d, o + 4);
            var names = Names(d, o + 8, count);
            int p = o + 8 + namesLength;
            for (int i = 0; i < count; i++)
            {
                var t = new LightTrack { Name = names[i], Type = BitConverter.ToInt32(d, p), Unknown = BitConverter.ToInt32(d, p + 4) };
                uint flags = BitConverter.ToUInt32(d, p + 8); int length = BitConverter.ToInt32(d, p + 16);
                t.Color = BitConverter.ToUInt32(d, p + 20);
                int q = p + 24;
                for (int c = 0; c < 10; c++) t.Channels[c] = ReadCurve(d, ref q, (int)(flags >> (3 * c) & 7), m.FrameCount);
                m.Lights.Add(t);
                p += 24 + length;
            }
        }

        static void ReadTracks(byte[] d, int o, GfMotion m, int channels, Action<string, int, Curve[]> add)
        {
            int count = BitConverter.ToInt32(d, o), namesLength = BitConverter.ToInt32(d, o + 4);
            var perName = new int[count];
            for (int i = 0; i < count; i++) perName[i] = BitConverter.ToInt32(d, o + 8 + 4 * i);
            var names = Names(d, o + 8 + 4 * count, count);
            int p = o + 8 + 4 * count + namesLength;
            for (int i = 0; i < count; i++)
                for (int k = 0; k < perName[i]; k++)
                {
                    int index = BitConverter.ToInt32(d, p); uint flags = BitConverter.ToUInt32(d, p + 4); int length = BitConverter.ToInt32(d, p + 8);
                    var curves = new Curve[channels];
                    int q = p + 12;
                    for (int c = 0; c < channels; c++) curves[c] = ReadCurve(d, ref q, (int)(flags >> (3 * c) & 7), m.FrameCount);
                    add(names[i], index, curves);
                    p += 12 + length;
                }
        }

        static Curve ReadCurve(byte[] d, ref int p, int code, int frameCount)
        {
            switch (code)
            {
                case 0: return new Curve();
                case 1: return new Curve { Animated = true, Constant = 0 };
                case 2: return new Curve { Animated = true, Constant = 1 };
                case 3: { var c = new Curve { Animated = true, Constant = BitConverter.ToSingle(d, p) }; p += 4; return c; }
                case 4:
                case 5:
                {
                    int n = BitConverter.ToInt32(d, p); p += 4;
                    var c = new Curve { Animated = true, Frames = new float[n], Values = new float[n], Slopes = new float[n] };
                    bool wide = frameCount > 0xFF;
                    for (int i = 0; i < n; i++) { c.Frames[i] = wide ? BitConverter.ToUInt16(d, p) : d[p]; p += wide ? 2 : 1; }
                    p = (p + 3) & ~3;
                    if (code == 4)
                    {
                        float vs = BitConverter.ToSingle(d, p), vo = BitConverter.ToSingle(d, p + 4), ss = BitConverter.ToSingle(d, p + 8), so = BitConverter.ToSingle(d, p + 12);
                        p += 16;
                        for (int i = 0; i < n; i++, p += 4)
                        {
                            c.Values[i] = BitConverter.ToUInt16(d, p) / 65535f * vs + vo;
                            c.Slopes[i] = BitConverter.ToUInt16(d, p + 2) / 65535f * ss + so;
                        }
                    }
                    else
                        for (int i = 0; i < n; i++, p += 8) { c.Values[i] = BitConverter.ToSingle(d, p); c.Slopes[i] = BitConverter.ToSingle(d, p + 4); }
                    return c;
                }
                default: throw new InvalidDataException("motion channel code " + code);
            }
        }

        // JSON for PokeSim (CtrMotion): {"frames","loop","bones":[{"bone","sx".."tz"}],"uvs":[{"material","unit","su","sv",
        // "rot","tu","tv"}],"colors":[{"material","index","r","g","b","a"}]}; a channel is absent (not animated), a
        // number, or {"f":[frames],"v":[values],"s":[slopes]}.
        public string ToJson()
        {
            var w = new JsonWriter(indent: false);
            w.BeginObject().Name("frames").Value(FrameCount).Name("loop").Value(Loop);
            string[] bone = { "sx", "sy", "sz", "rx", "ry", "rz", "tx", "ty", "tz" }, uv = { "su", "sv", "rot", "tu", "tv" }, col = { "r", "g", "b", "a" };
            w.Name("bones").BeginArray();
            foreach (var b in Bones) { w.BeginObject().Name("bone").Value(b.Bone); Channels(w, b.Channels, bone); w.EndObject(); }
            w.EndArray().Name("uvs").BeginArray();
            foreach (var u in Uvs) { w.BeginObject().Name("material").Value(u.Material).Name("unit").Value(u.Unit); Channels(w, u.Channels, uv); w.EndObject(); }
            w.EndArray().Name("colors").BeginArray();
            foreach (var c in Colors) { w.BeginObject().Name("material").Value(c.Material).Name("index").Value(c.Index); Channels(w, c.Channels, col); w.EndObject(); }
            w.EndArray().Name("cameras").BeginArray();
            string[] cam = { "near", "far", "fovy", "aspect", "rx", "ry", "rz", "tx", "ty", "tz" };
            foreach (var c in Cameras) { w.BeginObject().Name("name").Value(c.Name); Channels(w, c.Channels, cam); w.EndObject(); }
            w.EndArray().Name("lights").BeginArray();
            string[] light = { "c0", "c1", "c2", "c3", "c4", "c5", "c6", "c7", "c8", "c9" };
            foreach (var l in Lights)
            {
                w.BeginObject().Name("name").Value(l.Name).Name("type").Value(l.Type).Name("color").Value(l.Color.ToString("X8"));
                Channels(w, l.Channels, light); w.EndObject();
            }
            w.EndArray();
            if (OtherSections.Count > 0) { w.Name("unread").BeginArray(); foreach (int s in OtherSections) w.Value(s); w.EndArray(); }
            w.EndObject();
            return w.ToString();
        }

        static void Channels(JsonWriter w, Curve[] cs, string[] names)
        {
            for (int i = 0; i < cs.Length; i++)
            {
                var c = cs[i];
                if (c == null || !c.Animated) continue;
                w.Name(names[i]);
                if (c.Frames == null) { w.Value(c.Constant); continue; }
                w.BeginObject();
                w.Name("f").BeginArray(); foreach (var f in c.Frames) w.Value(f); w.EndArray();
                w.Name("v").BeginArray(); foreach (var v in c.Values) w.Value(Math.Round(v, 5)); w.EndArray();
                w.Name("s").BeginArray(); foreach (var s in c.Slopes) w.Value(Math.Round(s, 6)); w.EndArray();
                w.EndObject();
            }
        }
    }
}
