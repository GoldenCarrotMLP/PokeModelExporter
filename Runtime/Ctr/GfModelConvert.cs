using System;
using System.Collections.Generic;
using System.Numerics;
using PokeModelExporter.Model;

namespace PokeModelExporter.Ctr
{
    // A GF model (GfModelPack.Model) -> ModelData for GlbWriter. Meshes are decoded the way the 3DS GPU reads them:
    // the PICA register writes in each sub-mesh's command lists give the vertex layout (attribute formats, order,
    // stride), constant ("fixed") attributes and the index format. GF's vertex shaders take inputs in registers
    // 0 position, 1 normal, 2 tangent, 3 colour, 4-6 UV sets, 7 bone indices, 8 bone weights.
    // Units: GF models are in centimetres (a Pikachu is ~40 tall); ModelData is metres.
    public static class GfModelConvert
    {
        public const float Scale = 0.01f;

        public static ModelData ToModel(GfModelPack.Model gf, out List<string> warnings) => ToModel(gf, null, out warnings);

        // pack: for the fragment shaders (texture combiner) the materials name.
        public static ModelData ToModel(GfModelPack.Model gf, GfModelPack pack, out List<string> warnings)
        {
            warnings = new List<string>();
            var model = new ModelData { Name = gf.Name, StandardVertexColors = true };
            // Bones -> nodes (Euler angles, Z applied first, then Y, then X: as the motions, checked on Normalium Z's aura).
            var index = new Dictionary<string, int>();
            foreach (var b in gf.Bones)
            {
                var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, b.Rotation.X) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, b.Rotation.Y)
                        * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, b.Rotation.Z);
                var n = new NodeData { Name = b.Name, Translation = b.Translation * Scale, Rotation = q, Scale = b.Scale, SkinIndex = model.Nodes.Count };
                index[b.Name] = model.Nodes.Count;
                model.Nodes.Add(n);
            }
            for (int i = 0; i < gf.Bones.Count; i++)
                if (!string.IsNullOrEmpty(gf.Bones[i].Parent) && index.TryGetValue(gf.Bones[i].Parent, out int p)) model.Nodes[i].Parent = p;

            foreach (var mat in gf.Materials)
            {
                var md = new MaterialData { Name = mat.Name, Shader = mat.FragmentShader };
                foreach (var tc in mat.TexCoords)
                {
                    string slot = tc.Unit == 0 ? "BaseColorMap" : "Texture" + tc.Unit;
                    if (md.Textures.ContainsKey(slot)) continue;
                    md.Textures[slot] = tc.Texture;
                    md.Wraps[slot] = (tc.WrapU, tc.WrapV);
                    md.Colors["UVTransform" + tc.Unit] = new Vector4(tc.Scale.X, tc.Scale.Y, tc.Translation.X, tc.Translation.Y);
                    md.Floats["UVRotation" + tc.Unit] = tc.Rotation;
                    md.Floats["UVMapping" + tc.Unit] = tc.Mapping;
                    md.Floats["WrapU" + tc.Unit] = tc.WrapU; md.Floats["WrapV" + tc.Unit] = tc.WrapV; // PICA: 0 clamp, 1 border, 2 repeat, 3 mirror
                }
                Describe(mat, pack, md);
                model.Materials.Add(md);
            }

            foreach (var mesh in gf.Meshes)
                foreach (var sm in mesh.SubMeshes)
                {
                    try { model.Meshes.Add(Decode(mesh, sm)); }
                    catch (Exception e) { warnings.Add($"{mesh.Name}/{sm.Material}: {e.Message}"); }
                }
            return model;
        }

        // The PICA state a material draws with, for an engine shader that rebuilds it (material extras):
        //   options pica.tev        the six combiner stages: "source,operand,combine,scale" (hex) separated by ';'
        //   options pica.tevBuffer  0xE0 (buffer inputs / update masks) and 0xFD (buffer start colour), hex
        //   options pica.colorOp / blend / blendColor / alphaTest / depth / cull   registers 0x100, 0x101, 0x103, 0x104,
        //                           0x107, 0x040 (hex)
        //   colors Constant0..5     each stage's constant colour (the material's constant its assignment picks)
        //   colors Specular0, Specular1, BlendColor, Emission, Ambient, Diffuse
        static readonly int[] StageRegs = { 0xC0, 0xC8, 0xD0, 0xD8, 0xF0, 0xF8 };

        static void Describe(GfModelPack.Material mat, GfModelPack pack, MaterialData md)
        {
            if (pack != null && mat.FragmentShader != null && pack.FragmentShaderCommands.TryGetValue(mat.FragmentShader, out var fs))
            {
                var r = Pica.Last(fs);
                var stages = new List<string>();
                foreach (int s in StageRegs)
                    stages.Add($"{Reg(r, s):X},{Reg(r, s + 1):X},{Reg(r, s + 2):X},{Reg(r, s + 4):X}");
                md.ShaderOptions["pica.tev"] = string.Join(";", stages);
                md.ShaderOptions["pica.tevBuffer"] = $"{Reg(r, 0xE0):X},{Reg(r, 0xFD):X}";
            }
            var state = Pica.Last(mat.Commands);
            md.ShaderOptions["pica.colorOp"] = Reg(state, 0x100).ToString("X");
            md.ShaderOptions["pica.blend"] = Reg(state, 0x101).ToString("X");
            md.ShaderOptions["pica.blendColor"] = Reg(state, 0x103).ToString("X");
            md.ShaderOptions["pica.alphaTest"] = Reg(state, 0x104).ToString("X");
            md.ShaderOptions["pica.depth"] = Reg(state, 0x107).ToString("X");
            md.ShaderOptions["pica.cull"] = Reg(state, 0x040).ToString("X");
            for (int s = 0; s < 6; s++)
            {
                int c = Math.Min(5, (int)mat.ConstantAssignment[s]);
                md.Colors["Constant" + s] = Rgba(mat.Constants, c);
                md.Floats["ConstAssign" + s] = c; // which of the six constants the stage uses (colour animations name these)
            }
            string[] names = { "Specular0", "Specular1", "BlendColor", "Emission", "Ambient", "Diffuse" };
            for (int i = 0; i < 6; i++) md.Colors[names[i]] = Rgba(mat.Colors, i);
            // Blending (0x100 bit 8: blend instead of logic op) makes it transparent for previews; the engine reads the rest.
            if ((Reg(state, 0x100) & 0x100) != 0) md.AlphaType = "Transparent";
        }

        static uint Reg(Dictionary<int, uint> r, int reg) => r.TryGetValue(reg, out var v) ? v : 0;
        static Vector4 Rgba(byte[] b, int i) => new Vector4(b[i * 4], b[i * 4 + 1], b[i * 4 + 2], b[i * 4 + 3]) / 255f;

        sealed class Regs
        {
            public readonly Dictionary<int, uint> Last = new Dictionary<int, uint>();
            public readonly Dictionary<int, Vector4> Fixed = new Dictionary<int, Vector4>();
        }

        static Regs Read(params uint[][] lists)
        {
            var r = new Regs();
            int fixedIndex = -1; var fixedWords = new List<uint>();
            foreach (var cmds in lists)
            {
                int i = 0;
                while (i + 1 < cmds.Length)
                {
                    uint param = cmds[i], hdr = cmds[i + 1];
                    int id = (int)(hdr & 0xFFFF), extra = (int)(hdr >> 20 & 0x7FF);
                    bool consecutive = (hdr >> 31) != 0;
                    var values = new List<uint> { param };
                    for (int k = 0; k < extra; k++) values.Add(cmds[i + 2 + k]);
                    i += 2 + extra;
                    if ((i & 1) != 0) i++;
                    for (int k = 0; k < values.Count; k++)
                    {
                        int reg = consecutive ? id + k : id;
                        uint v = values[k];
                        r.Last[reg] = v;
                        if (reg == 0x232) { fixedIndex = (int)(v & 0xF); fixedWords.Clear(); }
                        else if (reg >= 0x233 && reg <= 0x235 && fixedIndex >= 0)
                        {
                            fixedWords.Add(v);
                            if (fixedWords.Count == 3) { r.Fixed[fixedIndex] = Fixed96(fixedWords[0], fixedWords[1], fixedWords[2]); fixedIndex = -1; }
                        }
                    }
                }
            }
            return r;
        }

        // Fixed attribute value: four 24-bit floats packed in 96 bits (w, z, y, x from the first word on).
        static Vector4 Fixed96(uint a, uint b, uint c)
        {
            float w = F24(a >> 8), z = F24((a & 0xFF) << 16 | b >> 16), y = F24((b & 0xFFFF) << 8 | c >> 24), x = F24(c & 0xFFFFFF);
            return new Vector4(x, y, z, w);
        }

        static float F24(uint raw)
        {
            raw &= 0xFFFFFF;
            if ((raw & 0x7FFFFF) == 0) return 0;
            uint sign = raw >> 23, exp = raw >> 16 & 0x7F, man = raw & 0xFFFF;
            uint bits = sign << 31 | (exp + 64) << 23 | man << 7; // rebias 63 -> 127
            return BitConverter.Int32BitsToSingle((int)bits);
        }

        static MeshData Decode(GfModelPack.Mesh mesh, GfModelPack.SubMesh sm)
        {
            var r = Read(sm.EnableCommands, sm.IndexCommands);
            uint fmtLow = Get(r, 0x201), fmtHigh = Get(r, 0x202);
            uint orderLow = Get(r, 0x204), cfg = Get(r, 0x205);
            uint permLow = Get(r, 0x2BB), permHigh = Get(r, 0x2BC);
            int attrCount = (int)(fmtHigh >> 28) + 1;
            uint fixedMask = fmtHigh >> 16 & 0xFFF;
            int stride = (int)(cfg >> 16 & 0xFF), comps = (int)(cfg >> 28);
            ulong formats = fmtLow | (ulong)(fmtHigh & 0xFFFF) << 32;
            ulong order = orderLow | (ulong)(cfg & 0xFFFF) << 32;
            ulong perm = permLow | (ulong)permHigh << 32;
            int n = sm.VertexCount;

            var data = new Vector4[12][]; // per shader input register
            int offset = 0;
            for (int c = 0; c < comps; c++)
            {
                int attr = (int)(order >> (c * 4) & 0xF);
                if (attr >= 12) { offset += (attr - 11) * 4; continue; } // padding component
                int f = (int)(formats >> (attr * 4) & 0xF), type = f & 3, size = (f >> 2) + 1;
                int elem = type == 3 ? 4 : type == 2 ? 2 : 1;
                // components are aligned to their element size
                offset = (offset + elem - 1) / elem * elem;
                int reg = (int)(perm >> (attr * 4) & 0xF);
                var arr = new Vector4[n];
                for (int v = 0; v < n; v++)
                {
                    int p = v * stride + offset;
                    var x = new float[4] { 0, 0, 0, 1 };
                    for (int k = 0; k < size; k++)
                        x[k] = type switch
                        {
                            3 => BitConverter.ToSingle(sm.Vertices, p + 4 * k),
                            2 => BitConverter.ToInt16(sm.Vertices, p + 2 * k),
                            1 => sm.Vertices[p + k],
                            _ => (sbyte)sm.Vertices[p + k],
                        };
                    arr[v] = new Vector4(x[0], x[1], x[2], x[3]);
                }
                if (reg < 12) data[reg] = arr;
                offset += size * elem;
            }
            for (int attr = 0; attr < attrCount; attr++)
            {
                if ((fixedMask >> attr & 1) == 0) continue;
                int reg = (int)(perm >> (attr * 4) & 0xF);
                if (reg < 12 && data[reg] == null && r.Fixed.TryGetValue(attr, out var fv))
                {
                    var arr = new Vector4[n];
                    for (int v = 0; v < n; v++) arr[v] = fv;
                    data[reg] = arr;
                }
            }
            if (data[0] == null) throw new NotSupportedException("no positions");

            var md = new MeshData { Name = mesh.Name + (mesh.SubMeshes.Count > 1 ? "_" + sm.Material : "") };
            md.Positions = new Vector3[n];
            for (int v = 0; v < n; v++) md.Positions[v] = new Vector3(data[0][v].X, data[0][v].Y, data[0][v].Z) * Scale;
            if (data[1] != null)
            {
                md.Normals = new Vector3[n];
                for (int v = 0; v < n; v++) md.Normals[v] = Vector3.Normalize(new Vector3(data[1][v].X, data[1][v].Y, data[1][v].Z) + new Vector3(0, 0, 1e-9f));
            }
            for (int u = 4; u <= 6; u++)
            {
                if (data[u] == null) continue;
                var t = new Vector2[n];
                for (int v = 0; v < n; v++) t[v] = new Vector2(data[u][v].X, 1 - data[u][v].Y); // GPU UVs: v up
                md.TexCoords.Add(t);
            }
            if (data[3] != null)
            {
                bool bytes = ((formats >> (AttrOf(perm, attrCount, 3) * 4) & 3) == 1);
                md.Colors = new Vector4[n];
                for (int v = 0; v < n; v++) md.Colors[v] = bytes ? data[3][v] / 255f : data[3][v];
            }
            // Skinning: the sub-mesh's bone table, indexed by the vertex's bone indices; weights normalised.
            md.Joints = new int[n * 4]; md.Weights = new float[n * 4];
            for (int v = 0; v < n; v++)
            {
                var bi = data[7]?[v] ?? Vector4.Zero;
                var bw = data[8]?[v] ?? new Vector4(1, 0, 0, 0);
                int count = Math.Max(1, Math.Min(4, mesh.BoneIndicesPerVertex));
                float sum = 0;
                for (int k = 0; k < count; k++) sum += Math.Max(0, Comp(bw, k));
                for (int k = 0; k < 4; k++)
                {
                    int local = (int)Comp(bi, k);
                    md.Joints[v * 4 + k] = k < count && local >= 0 && local < sm.BoneIndices.Length ? sm.BoneIndices[local] : 0;
                    md.Weights[v * 4 + k] = k < count ? (sum > 0 ? Math.Max(0, Comp(bw, k)) / sum : (k == 0 ? 1 : 0)) : 0;
                }
            }
            // Indices: u16 when bit 31 of the index buffer register is set, else u8; triangle lists.
            bool u16 = (Get(r, 0x227) >> 31) != 0;
            int count2 = (int)Get(r, 0x228);
            if (count2 <= 0) count2 = sm.IndexCount;
            var idx = new int[count2];
            for (int i = 0; i < count2; i++) idx[i] = u16 ? BitConverter.ToUInt16(sm.Indices, i * 2) : sm.Indices[i];
            int mode = (int)(Get(r, 0x25E) >> 8 & 3);
            if (mode == 1) idx = Strip(idx);
            md.SubMeshes.Add(new SubMeshData { Material = sm.Material, Indices = idx });
            return md;
        }

        static int AttrOf(ulong perm, int count, int reg)
        {
            for (int a = 0; a < count; a++) if ((int)(perm >> (a * 4) & 0xF) == reg) return a;
            return 0;
        }

        static float Comp(Vector4 v, int k) => k switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => v.W };
        static uint Get(Regs r, int reg) => r.Last.TryGetValue(reg, out var v) ? v : 0;

        static int[] Strip(int[] s)
        {
            var o = new List<int>();
            for (int i = 2; i < s.Length; i++)
                if ((i & 1) == 0) { o.Add(s[i - 2]); o.Add(s[i - 1]); o.Add(s[i]); }
                else { o.Add(s[i - 1]); o.Add(s[i - 2]); o.Add(s[i]); }
            return o.ToArray();
        }
    }
}
