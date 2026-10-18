using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using PokeModelExporter.Model;

namespace PokeModelExporter.Ctr
{
    // GameFreak's 3DS model package (Sun/Moon, Ultra Sun/Moon): magic 0x00010000, five sections (models, textures,
    // vertex shaders, unused, fragment shaders), each entry a length-prefixed name and an offset. Every blob starts with
    // a 16-byte header (version 0x15041213 / 0x15122117, section count) and named sections ("gfmodel", "material",
    // "mesh", "texture", "shader": 8-byte name, u32 length, u32 padding).
    // Our own reader, from the layouts in the files; see Docs/usum-research.md.
    public sealed class GfModelPack
    {
        public sealed class Texture { public string Name; public int Width, Height, Format, Mips; public byte[] Rgba; }

        public sealed class TexCoord
        {
            public string Texture; public int Unit, Mapping; public Vector2 Scale, Translation; public float Rotation;
            public uint WrapU, WrapV, MagFilter, MinFilter; // PICA wrap: 0 clamp to edge, 1 clamp to border, 2 repeat, 3 mirror
        }

        public sealed class Material
        {
            public string Name, Shader, VertexShader, FragmentShader;
            public List<TexCoord> TexCoords = new List<TexCoord>();
            public uint[] Commands = Array.Empty<uint>(); // its PICA render state (blending, alpha / depth test...)
        }

        public sealed class Bone { public string Name, Parent; public byte Flags; public Vector3 Scale, Rotation, Translation; }

        public sealed class SubMesh
        {
            public string Material; public byte[] BoneIndices = Array.Empty<byte>();
            public int VertexCount, IndexCount; public byte[] Vertices, Indices;
            public uint[] EnableCommands, DisableCommands, IndexCommands;
        }

        public sealed class Mesh { public string Name; public Vector4 BoxMin, BoxMax; public int BoneIndicesPerVertex; public List<SubMesh> SubMeshes = new List<SubMesh>(); }

        public sealed class Model
        {
            public string Name;
            public List<string> ShaderNames = new List<string>(), TextureNames = new List<string>(), MaterialNames = new List<string>(), MeshNames = new List<string>();
            public Vector4 BoxMin, BoxMax; public Matrix4x4 Transform;
            public List<Bone> Bones = new List<Bone>();
            public List<Material> Materials = new List<Material>();
            public List<Mesh> Meshes = new List<Mesh>();
        }

        public List<Model> Models = new List<Model>();
        public List<Texture> Textures = new List<Texture>();
        public List<string> VertexShaders = new List<string>(), FragmentShaders = new List<string>();

        public static bool IsPack(byte[] d) => d != null && d.Length > 24 && BitConverter.ToUInt32(d, 0) == 0x00010000;

        public static GfModelPack Read(byte[] d)
        {
            if (!IsPack(d)) throw new InvalidDataException("not a GF model pack");
            var pack = new GfModelPack();
            var counts = new int[5];
            for (int i = 0; i < 5; i++) counts[i] = BitConverter.ToInt32(d, 4 + 4 * i);
            int ptr = 24;
            for (int sec = 0; sec < 5; sec++)
                for (int j = 0; j < counts[sec]; j++, ptr += 4)
                {
                    int entry = BitConverter.ToInt32(d, ptr);
                    int len = d[entry];
                    string name = Encoding.ASCII.GetString(d, entry + 1, len);
                    int at = BitConverter.ToInt32(d, entry + 1 + len);
                    switch (sec)
                    {
                        case 0: pack.Models.Add(ReadModel(d, at, name)); break;
                        case 1: pack.Textures.Add(ReadTexture(d, at, name)); break;
                        case 2: pack.VertexShaders.Add(name); break;
                        case 4: pack.FragmentShaders.Add(name); break;
                    }
                }
            return pack;
        }

        // ---- Sections ----
        static string SectionName(byte[] d, int o) => Encoding.ASCII.GetString(d, o, 8).TrimEnd('\0');

        // A blob's first section: after the version and section count (8 bytes), or 8 more bytes of padding (models).
        static int FirstSection(byte[] d, int at, string name)
        {
            if (SectionName(d, at + 8) == name) return at + 8;
            if (SectionName(d, at + 0x10) == name) return at + 0x10;
            throw new InvalidDataException(name + " section expected");
        }

        static Texture ReadTexture(byte[] d, int at, string name)
        {
            int o = FirstSection(d, at, "texture");
            o += 0x10;
            int size = BitConverter.ToInt32(d, o);
            string texName = CString(d, o + 0x10, 0x40);
            int p = o + 0x50;
            var t = new Texture
            {
                Name = string.IsNullOrEmpty(texName) ? name : texName,
                Width = BitConverter.ToUInt16(d, p), Height = BitConverter.ToUInt16(d, p + 2),
                Format = BitConverter.ToUInt16(d, p + 4), Mips = BitConverter.ToUInt16(d, p + 6),
            };
            int data = p + 0x18;
            try { t.Rgba = PicaTexture.Decode(d, data, t.Width, t.Height, t.Format); }
            catch (NotSupportedException) { t.Rgba = null; }
            return t;
        }

        static Model ReadModel(byte[] d, int at, string name)
        {
            var m = new Model { Name = name };
            int count = BitConverter.ToInt32(d, at + 4); // sections in this blob
            int o = FirstSection(d, at, "gfmodel");
            for (int s = 0; s < count && o + 16 <= d.Length; s++)
            {
                string sec = SectionName(d, o);
                int len = BitConverter.ToInt32(d, o + 8);
                int body = o + 0x10;
                switch (sec)
                {
                    case "gfmodel": ReadGfModel(d, body, m); break;
                    case "material": m.Materials.Add(ReadMaterial(d, body, body + len, m)); break;
                    case "mesh": m.Meshes.Add(ReadMesh(d, body)); break;
                }
                o = body + len;
            }
            return m;
        }

        static List<string> HashTable(byte[] d, ref int o)
        {
            int n = BitConverter.ToInt32(d, o); o += 4;
            var list = new List<string>(n);
            for (int i = 0; i < n; i++, o += 0x44) list.Add(CString(d, o + 4, 0x40));
            return list;
        }

        static void ReadGfModel(byte[] d, int o, Model m)
        {
            m.ShaderNames = HashTable(d, ref o);
            m.TextureNames = HashTable(d, ref o);
            m.MaterialNames = HashTable(d, ref o);
            m.MeshNames = HashTable(d, ref o);
            m.BoxMin = V4(d, o); m.BoxMax = V4(d, o + 16); o += 32;
            m.Transform = new Matrix4x4(F(d, o), F(d, o + 4), F(d, o + 8), F(d, o + 12), F(d, o + 16), F(d, o + 20), F(d, o + 24), F(d, o + 28),
                F(d, o + 32), F(d, o + 36), F(d, o + 40), F(d, o + 44), F(d, o + 48), F(d, o + 52), F(d, o + 56), F(d, o + 60));
            o += 64;
            // Look-up tables (fragment lighting): a length, a count, padding, then the tables.
            int lutLength = BitConverter.ToInt32(d, o), lutCount = BitConverter.ToInt32(d, o + 4);
            o += 0x20 + lutLength * lutCount;
            int bones = BitConverter.ToInt32(d, o); o += 0x10;
            for (int i = 0; i < bones; i++)
            {
                var b = new Bone { Name = LString(d, ref o), Parent = LString(d, ref o) };
                b.Flags = d[o++];
                b.Scale = V3(d, o); b.Rotation = V3(d, o + 12); b.Translation = V3(d, o + 24); o += 36;
                m.Bones.Add(b);
            }
        }

        static Material ReadMaterial(byte[] d, int o, int end, Model model)
        {
            var mat = new Material();
            o += 4; mat.Name = LString(d, ref o);
            o += 4; mat.Shader = LString(d, ref o);
            o += 4; mat.VertexShader = LString(d, ref o);
            o += 4; mat.FragmentShader = LString(d, ref o);
            // Texture coordinate entries: a hash, a length-prefixed texture name (one the model lists), then the unit,
            // mapping, UV scale / rotation / translation and the sampler's wrap and filters.
            for (int p = o; p < end - 8; p++)
            {
                int len = d[p + 4];
                if (len < 5 || p + 5 + len + 34 > end) continue;
                string s = Encoding.ASCII.GetString(d, p + 5, len);
                if (!model.TextureNames.Contains(s)) continue;
                int q = p + 5 + len;
                var tc = new TexCoord
                {
                    Texture = s, Unit = d[q], Mapping = d[q + 1],
                    Scale = new Vector2(F(d, q + 2), F(d, q + 6)), Rotation = F(d, q + 10), Translation = new Vector2(F(d, q + 14), F(d, q + 18)),
                    WrapU = BitConverter.ToUInt32(d, q + 22), WrapV = BitConverter.ToUInt32(d, q + 26),
                    MagFilter = BitConverter.ToUInt32(d, q + 30), MinFilter = q + 38 <= end ? BitConverter.ToUInt32(d, q + 34) : 0,
                };
                mat.TexCoords.Add(tc);
                p = q + 30;
            }
            return mat;
        }

        static Mesh ReadMesh(byte[] d, int o)
        {
            var mesh = new Mesh { Name = CString(d, o + 4, 0x40) };
            o += 0x48;
            mesh.BoxMin = V4(d, o); mesh.BoxMax = V4(d, o + 16); o += 32;
            int subCount = BitConverter.ToInt32(d, o); mesh.BoneIndicesPerVertex = BitConverter.ToInt32(d, o + 4); o += 8 + 16;
            var lists = new List<uint[]>();
            while (true)
            {
                int len = BitConverter.ToInt32(d, o), index = BitConverter.ToInt32(d, o + 4), total = BitConverter.ToInt32(d, o + 8); o += 16;
                var cmds = new uint[len / 4];
                for (int i = 0; i < cmds.Length; i++) cmds[i] = BitConverter.ToUInt32(d, o + 4 * i);
                o += len; lists.Add(cmds);
                if (index >= total - 1) break;
            }
            for (int i = 0; i < subCount; i++)
            {
                var sm = new SubMesh();
                o += 4; // hash
                int nameLen = BitConverter.ToInt32(d, o); o += 4;
                sm.Material = CString(d, o, nameLen); o += nameLen;
                int boneCount = d[o];
                sm.BoneIndices = new byte[boneCount];
                Array.Copy(d, o + 1, sm.BoneIndices, 0, boneCount);
                o += 32;
                sm.VertexCount = BitConverter.ToInt32(d, o); sm.IndexCount = BitConverter.ToInt32(d, o + 4);
                int vlen = BitConverter.ToInt32(d, o + 8), ilen = BitConverter.ToInt32(d, o + 12); o += 16;
                sm.Vertices = new byte[vlen]; sm.Indices = new byte[ilen];
                sm.EnableCommands = lists[i * 3]; sm.DisableCommands = lists[i * 3 + 1]; sm.IndexCommands = lists[i * 3 + 2];
                mesh.SubMeshes.Add(sm);
            }
            foreach (var sm in mesh.SubMeshes)
            {
                Array.Copy(d, o, sm.Vertices, 0, sm.Vertices.Length); o += sm.Vertices.Length;
                Array.Copy(d, o, sm.Indices, 0, sm.Indices.Length); o += sm.Indices.Length;
            }
            return mesh;
        }

        // ---- Helpers ----
        static float F(byte[] d, int o) => BitConverter.ToSingle(d, o);
        static Vector3 V3(byte[] d, int o) => new Vector3(F(d, o), F(d, o + 4), F(d, o + 8));
        static Vector4 V4(byte[] d, int o) => new Vector4(F(d, o), F(d, o + 4), F(d, o + 8), F(d, o + 12));
        static string CString(byte[] d, int o, int max)
        {
            int n = 0;
            while (n < max && o + n < d.Length && d[o + n] != 0) n++;
            return Encoding.ASCII.GetString(d, o, n);
        }
        static string LString(byte[] d, ref int o)
        {
            int len = d[o];
            string s = Encoding.ASCII.GetString(d, o + 1, len);
            o += 1 + len;
            return s;
        }
    }
}
