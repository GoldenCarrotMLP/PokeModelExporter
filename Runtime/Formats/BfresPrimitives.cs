using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace PokeModelExporter.Formats
{
    // The small meshes ("primitives": rings, cylinders, beams) inside a particle file's G3PR section: a Switch
    // BFRES (v0.9) with one model per primitive. Only what's needed to describe their shape is read:
    //   FRES  0x28 model array, 0x30 model dict (count at dict+4), 0xB0 buffer info { _, size, u64 offset }
    //         (vertex and index data live in that region)
    //   FMDL  (0x78 bytes each) 0x08 name, 0x20 vertex buffers (FVTX), 0x28 shapes (FSHP)
    //   FVTX  0x08 attributes { name, u16 format, _, u16 offset, u16 buffer }, 0x30 buffer sizes, 0x38 strides,
    //         0x48 u32 data offset, 0x4C u8 attribute count, 0x4D u8 buffer count, 0x50 u32 vertex count
    //   FSHP  0x18 meshes (LODs): { _, _, _, _, u32 index data offset, primitive type (3 triangles),
    //         index format (0 u8, 1 u16, 2 u32), index count, first vertex }
    // Attribute formats: high byte = layout (0x02 8, 0x09 8_8, 0x0B 8_8_8_8, 0x0E 10_10_10_2, 0x12 16_16,
    // 0x15 16_16_16_16, 0x17 32_32, 0x18 32_32_32, 0x19 32_32_32_32), low byte = type (1 unorm, 2 snorm, 5 float).
    // Found by inspection of SV's particle files (2026-10-13).
    public static class BfresPrimitives
    {
        public sealed class Primitive
        {
            public string Name;
            public Vector3[] Positions;
            public Vector2[] Uv0;
            public Vector4[] Colors; // _c0 vertex colour (null when absent): the particle shaders multiply colour and alpha by it
            public int[] Indices;
        }

        // G3PR section: descriptor table (child) id -> model index by order, then the BFRES (binary).
        // Returns primitive id -> primitive.
        public static Dictionary<ulong, Primitive> ReadSection(byte[] data, int g3pr)
        {
            var result = new Dictionary<ulong, Primitive>();
            uint children = BitConverter.ToUInt32(data, g3pr + 8), binary = BitConverter.ToUInt32(data, g3pr + 20);
            if (children == uint.MaxValue || binary == uint.MaxValue) return result;
            var ids = new List<(ulong id, sbyte[] attrs)>();
            int table = g3pr + (int)children;
            int p = table + (int)BitConverter.ToUInt32(data, table + 20), end = p + (int)BitConverter.ToUInt32(data, table + 4);
            while (p < end)
            {
                ulong id = BitConverter.ToUInt64(data, p);
                uint next = BitConverter.ToUInt32(data, p + 8);
                var attrs = new sbyte[6];
                for (int i = 0; i < 6; i++) attrs[i] = (sbyte)data[p + 16 + i];
                ids.Add((id, attrs));
                if (next == 0) break;
                p += (int)next;
            }
            int fres = g3pr + (int)binary;
            if (Encoding.ASCII.GetString(data, fres, 4) != "FRES") return result;
            var models = ReadModels(data, fres);
            for (int i = 0; i < ids.Count && i < models.Count; i++) result[ids[i].id] = models[i];
            return result;
        }

        static List<Primitive> ReadModels(byte[] d, int fres)
        {
            long Ptr(int at) => BitConverter.ToInt64(d, at);
            var list = new List<Primitive>();
            int modelArray = (int)Ptr(fres + 0x28), modelDict = (int)Ptr(fres + 0x30), bufferInfo = (int)Ptr(fres + 0xB0);
            int count = modelDict > 0 ? BitConverter.ToInt32(d, fres + modelDict + 4) : 0;
            int region = fres + (int)Ptr(fres + bufferInfo + 8);
            for (int m = 0; m < count; m++)
            {
                int fmdl = fres + modelArray + m * 0x78;
                if (Encoding.ASCII.GetString(d, fmdl, 4) != "FMDL") break;
                var prim = new Primitive { Name = Name(d, fres + (int)Ptr(fmdl + 0x08)) };
                int fvtx = fres + (int)Ptr(fmdl + 0x20), fshp = fres + (int)Ptr(fmdl + 0x28);
                int attrArray = fres + (int)Ptr(fvtx + 0x08), sizes = fres + (int)Ptr(fvtx + 0x30), strides = fres + (int)Ptr(fvtx + 0x38);
                int dataOffset = BitConverter.ToInt32(d, fvtx + 0x48), attrCount = d[fvtx + 0x4C], bufferCount = d[fvtx + 0x4D];
                int vertices = BitConverter.ToInt32(d, fvtx + 0x50);
                var bufferStart = new int[bufferCount];
                int at = region + dataOffset;
                for (int b = 0; b < bufferCount; b++)
                {
                    bufferStart[b] = at;
                    at += BitConverter.ToInt32(d, sizes + b * 0x10);
                    at = (at + 7) & ~7;
                }
                for (int a = 0; a < attrCount; a++)
                {
                    int attr = attrArray + a * 0x10;
                    string name = Name(d, fres + (int)Ptr(attr));
                    int format = BitConverter.ToUInt16(d, attr + 8), offset = BitConverter.ToUInt16(d, attr + 12), buffer = BitConverter.ToUInt16(d, attr + 14);
                    if (buffer >= bufferCount) continue;
                    int stride = BitConverter.ToInt32(d, strides + buffer * 0x10);
                    if (name == "_p0") prim.Positions = Read(d, bufferStart[buffer] + offset, stride, vertices, format, v => new Vector3(v[0], v[1], v[2]));
                    else if (name == "_u0") prim.Uv0 = Read(d, bufferStart[buffer] + offset, stride, vertices, format, v => new Vector2(v[0], v[1]));
                    else if (name == "_c0") prim.Colors = Read(d, bufferStart[buffer] + offset, stride, vertices, format, v => new Vector4(v[0], v[1], v[2], v[3]));
                }
                // First mesh (LOD 0) of the first shape.
                int mesh = fres + (int)Ptr(fshp + 0x18);
                int faceOffset = BitConverter.ToInt32(d, mesh + 0x20), primType = BitConverter.ToInt32(d, mesh + 0x24);
                int indexFormat = BitConverter.ToInt32(d, mesh + 0x28), indexCount = BitConverter.ToInt32(d, mesh + 0x2C), first = BitConverter.ToInt32(d, mesh + 0x30);
                if (primType == 3 && indexCount > 0 && indexCount < 1 << 20)
                {
                    prim.Indices = new int[indexCount];
                    int ip = region + faceOffset;
                    for (int i = 0; i < indexCount; i++)
                        prim.Indices[i] = first + (indexFormat == 0 ? d[ip + i] : indexFormat == 1 ? BitConverter.ToUInt16(d, ip + 2 * i) : BitConverter.ToInt32(d, ip + 4 * i));
                }
                list.Add(prim);
            }
            return list;
        }

        static T[] Read<T>(byte[] d, int start, int stride, int count, int format, Func<float[], T> make)
        {
            var result = new T[count];
            int layout = format >> 8, type = format & 0xFF;
            for (int i = 0; i < count; i++)
            {
                int p = start + i * stride;
                if (p + 4 > d.Length) break;
                result[i] = make(Decode(d, p, layout, type));
            }
            return result;
        }

        static float[] Decode(byte[] d, int p, int layout, int type)
        {
            var v = new float[4];
            switch (layout)
            {
                case 0x02: case 0x09: case 0x0B:
                {
                    int n = layout == 0x02 ? 1 : layout == 0x09 ? 2 : 4;
                    for (int i = 0; i < n; i++) v[i] = type == 2 ? Math.Max(-1, (sbyte)d[p + i] / 127f) : d[p + i] / 255f;
                    break;
                }
                case 0x0E:
                {
                    uint x = BitConverter.ToUInt32(d, p);
                    for (int i = 0; i < 3; i++)
                    {
                        int c = (int)((x >> (10 * i)) & 0x3FF);
                        v[i] = type == 2 ? Math.Max(-1, (c >= 512 ? c - 1024 : c) / 511f) : c / 1023f;
                    }
                    break;
                }
                case 0x12: case 0x15:
                {
                    int n = layout == 0x12 ? 2 : 4;
                    for (int i = 0; i < n; i++)
                    {
                        ushort raw = BitConverter.ToUInt16(d, p + 2 * i);
                        v[i] = type == 5 ? Half(raw) : type == 2 ? Math.Max(-1, (short)raw / 32767f) : raw / 65535f;
                    }
                    break;
                }
                case 0x17: case 0x18: case 0x19:
                {
                    int n = layout - 0x15;
                    for (int i = 0; i < n; i++) v[i] = BitConverter.ToSingle(d, p + 4 * i);
                    break;
                }
            }
            return v;
        }

        // IEEE half -> float (no System.Half in .NET Standard 2.1, which Unity uses).
        static float Half(ushort h)
        {
            int sign = (h >> 15) & 1, exp = (h >> 10) & 0x1F, mant = h & 0x3FF;
            float v = exp == 0 ? mant / 1024f * (float)Math.Pow(2, -14) : exp == 31 ? float.PositiveInfinity : (1 + mant / 1024f) * (float)Math.Pow(2, exp - 15);
            return sign == 1 ? -v : v;
        }

        static string Name(byte[] d, int at) => at <= 0 || at + 2 > d.Length ? "" : Encoding.UTF8.GetString(d, at + 2, BitConverter.ToUInt16(d, at));
    }
}
