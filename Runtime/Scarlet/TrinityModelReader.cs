using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using PokeModelExporter.Formats;
using PokeModelExporter.Model;

namespace PokeModelExporter.Scarlet
{
    // Reads a Trinity model (Scarlet/Violet) into ModelData.
    //   .trmdl  root: mesh files, skeleton file, material files
    //   .trmsh  mesh shapes: vertex layout (attribute, type, byte offset, stride), index type,
    //           index ranges per material
    //   .trmbf  vertex/index buffers, one entry per .trmsh shape
    //   .trskl  transform nodes (local scale / Euler XYZ radians / translation, parent, rig index)
    //   .trmtr  materials: shader, texture slots, parameters
    // Field numbers follow the declaration order in pkZukan/PokeDocs SV/Flatbuffers/model/*.fbs.
    // Vertex joint indices are rig indices: the node whose rig_idx matches.
    public static class TrinityModelReader
    {
        // read(path relative to the model folder) -> bytes, or null when missing.
        // locatorFiles: effect-locator skeletons to attach (paths relative to the model folder).
        public static ModelData Read(string trmdlName, Func<string, byte[]> read, bool shiny = false, bool lowestLodOnly = false,
            IEnumerable<string> locatorFiles = null)
        {
            var model = new ModelData { Name = Path.GetFileNameWithoutExtension(trmdlName) };
            var mdl = FbTable.Root(Require(read, trmdlName));

            var skeleton = mdl.Table(2).String(0);
            if (skeleton != null) ReadSkeleton(FbTable.Root(Require(read, skeleton)), model);
            if (locatorFiles != null)
                foreach (var file in locatorFiles)
                {
                    var data = read(file);
                    if (data != null) ReadLocators(FbTable.Root(data), model, LocatorSetName(file));
                }

            // Materials: the shiny variant is a sibling file named *_rare.trmtr.
            var materialFiles = mdl.Vector(3);
            for (int i = 0; i < materialFiles.Length; i++)
            {
                string file = materialFiles.String(i);
                if (shiny)
                {
                    string rare = Path.GetFileNameWithoutExtension(file) + "_rare.trmtr";
                    if (read(rare) != null) file = rare;
                }
                var data = read(file);
                if (data != null) ReadMaterials(FbTable.Root(data), model);
            }

            // Meshes: the first entry is the full-detail one; the rest are LODs.
            var meshes = mdl.Vector(1);
            if (meshes.Length > 0)
            {
                string trmsh = meshes.Table(lowestLodOnly ? meshes.Length - 1 : 0).String(0);
                var msh = FbTable.Root(Require(read, trmsh));
                var mbf = FbTable.Root(Require(read, msh.String(2)));
                ReadMeshes(msh, mbf, model);
            }
            return model;
        }

        static byte[] Require(Func<string, byte[]> read, string name) =>
            read(name) ?? throw new FileNotFoundException("Missing model file " + name);

        // ---- Skeleton ----

        static void ReadSkeleton(FbTable skl, ModelData model)
        {
            var nodes = skl.Vector(1);
            for (int i = 0; i < nodes.Length; i++)
            {
                var n = nodes.Table(i);
                var t = n.Table(1);
                var scale = Vec3(t, 0, Vector3.One);
                var euler = Vec3(t, 1, Vector3.Zero);
                var translate = Vec3(t, 2, Vector3.Zero);
                model.Nodes.Add(new NodeData
                {
                    Name = n.String(0),
                    Parent = n.I32(4, -1),
                    SkinIndex = n.I32(5, -1),
                    Translation = translate,
                    Rotation = EulerXyz(euler),
                    Scale = scale,
                });
            }
        }

        // An effect-locator skeleton (.trskl under locators/): named points like eff_mouth01 whose
        // effect_node field names the main-skeleton bone they ride on; transforms are local to it.
        static void ReadLocators(FbTable skl, ModelData model, string set)
        {
            var nodes = skl.Vector(1);
            int first = model.Nodes.Count;
            for (int i = 0; i < nodes.Length; i++)
            {
                var n = nodes.Table(i);
                var t = n.Table(1);
                int localParent = n.I32(4, -1);
                string bone = n.String(6);
                int parent = localParent >= 0 && localParent < nodes.Length ? first + localParent
                    : bone != null ? model.Nodes.FindIndex(x => x.Name == bone && !x.IsLocator) : -1;
                model.Nodes.Add(new NodeData
                {
                    Name = n.String(0),
                    Parent = parent,
                    Translation = Vec3(t, 2, Vector3.Zero),
                    Rotation = EulerXyz(Vec3(t, 1, Vector3.Zero)),
                    Scale = Vec3(t, 0, Vector3.One),
                    IsLocator = true,
                    LocatorSet = set,
                });
            }
        }

        // "locators/pm0718_00_00_00000_eff.trskl" -> "00000"
        static string LocatorSetName(string file)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.EndsWith("_eff")) name = name.Substring(0, name.Length - 4);
            int us = name.LastIndexOf('_');
            return us >= 0 ? name.Substring(us + 1) : name;
        }

        // Rotation applied X, then Y, then Z (intrinsic order of the reference Blender importer's Euler 'XYZ').
        static Quaternion EulerXyz(Vector3 e) =>
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, e.Z) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, e.Y) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, e.X);

        static Vector3 Vec3(FbTable table, int field, Vector3 fallback)
        {
            int p = table.InlineStruct(field);
            if (p == 0) return fallback;
            var b = table.Buffer;
            return new Vector3(BitConverter.ToSingle(b, p), BitConverter.ToSingle(b, p + 4), BitConverter.ToSingle(b, p + 8));
        }

        // ---- Materials ----

        static void ReadMaterials(FbTable mtr, ModelData model)
        {
            var mats = mtr.Vector(1);
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats.Table(i);
                var mat = new MaterialData { Name = m.String(0), AlphaType = m.String(15) };

                var shaders = m.Vector(1);
                if (shaders.Length > 0)
                {
                    var shader = shaders.Table(0);
                    mat.Shader = shader.String(0);
                    var values = shader.Vector(1);
                    for (int v = 0; v < values.Length; v++)
                        mat.ShaderOptions[values.Table(v).String(0)] = values.Table(v).String(1);
                }
                var textures = m.Vector(2);
                for (int t = 0; t < textures.Length; t++)
                    mat.Textures[textures.Table(t).String(0)] = textures.Table(t).String(1);
                var floats = m.Vector(4);
                for (int f = 0; f < floats.Length; f++)
                    mat.Floats[floats.Table(f).String(0)] = floats.Table(f).F32(1);
                var colors = m.Vector(7);
                for (int c = 0; c < colors.Length; c++)
                {
                    var entry = colors.Table(c);
                    int p = entry.InlineStruct(1);
                    var b = entry.Buffer;
                    mat.Colors[entry.String(0)] = p == 0 ? Vector4.Zero : new Vector4(
                        BitConverter.ToSingle(b, p), BitConverter.ToSingle(b, p + 4), BitConverter.ToSingle(b, p + 8), BitConverter.ToSingle(b, p + 12));
                }
                // Later files (shiny) replace same-named materials.
                model.Materials.RemoveAll(x => x.Name == mat.Name);
                model.Materials.Add(mat);
            }
        }

        // ---- Meshes ----

        enum Attr { Position = 1, Normal = 2, Tangent = 3, Binormal = 4, Color = 5, TexCoord = 6, BlendIndices = 7, BlendWeights = 8 }

        static void ReadMeshes(FbTable msh, FbTable mbf, ModelData model)
        {
            var shapes = msh.Vector(1);
            var buffers = mbf.Vector(1);
            for (int s = 0; s < shapes.Length; s++)
            {
                var shape = shapes.Table(s);
                var buffer = buffers.Table(s);
                var mesh = new MeshData { Name = shape.String(0) ?? shape.String(12) ?? $"mesh{s}" };

                // Vertex streams: attributes[k] describes vertex_buffer[k].
                var streams = shape.Vector(3);
                var vertexBuffers = buffer.Vector(1);
                int vertexCount = -1;
                for (int k = 0; k < streams.Length; k++)
                {
                    var stream = streams.Table(k);
                    var sizes = stream.Vector(1);
                    int stride = (int)sizes.Table(0).U32(0);
                    var bytes = vertexBuffers.Table(k).Vector(0).Bytes();
                    int count = bytes.Count / stride;
                    if (vertexCount < 0) vertexCount = count;
                    var accessors = stream.Vector(0);
                    for (int a = 0; a < accessors.Length; a++)
                        ReadAttribute(mesh, accessors.Table(a), bytes, stride, count);
                }

                // Indices, split into one list per material range.
                var indexBytes = buffer.Vector(0).Table(0).Vector(0).Bytes();
                int indexSize = 1 << (int)shape.U32(2); // 0 uint8, 1 uint16, 2 uint32
                var materials = shape.Vector(4);
                for (int m = 0; m < materials.Length; m++)
                {
                    var info = materials.Table(m);
                    int polyCount = (int)info.U32(0), polyOffset = (int)info.U32(1);
                    var indices = new int[polyCount];
                    for (int i = 0; i < polyCount; i++)
                    {
                        int at = indexBytes.Offset + (polyOffset + i) * indexSize;
                        indices[i] = indexSize == 1 ? indexBytes.Array[at]
                            : indexSize == 2 ? BitConverter.ToUInt16(indexBytes.Array, at)
                            : (int)BitConverter.ToUInt32(indexBytes.Array, at);
                    }
                    mesh.SubMeshes.Add(new SubMeshData { Material = info.String(3), Indices = indices });
                }
                model.Meshes.Add(mesh);
            }
        }

        static void ReadAttribute(MeshData mesh, FbTable accessor, ArraySegment<byte> data, int stride, int count)
        {
            var attr = (Attr)accessor.U32(1);
            int layer = (int)accessor.U32(2);
            uint type = accessor.U32(3);
            int offset = (int)accessor.U32(4);
            var values = new Vector4[count];
            for (int v = 0; v < count; v++)
                values[v] = Decode(data.Array, data.Offset + v * stride + offset, type);

            switch (attr)
            {
                case Attr.Position: mesh.Positions = Map(values, x => new Vector3(x.X, x.Y, x.Z)); break;
                case Attr.Normal: mesh.Normals = Map(values, x => Vector3.Normalize(new Vector3(x.X, x.Y, x.Z))); break;
                case Attr.Tangent: mesh.Tangents = Map(values, x => new Vector4(Vector3.Normalize(new Vector3(x.X, x.Y, x.Z)), x.W >= 0 ? 1 : -1)); break;
                case Attr.Color: if (layer == 0) mesh.Colors = values; break;
                case Attr.TexCoord:
                    while (mesh.TexCoords.Count <= layer) mesh.TexCoords.Add(null);
                    // Trinity UVs have V pointing up (origin bottom-left); glTF's origin is top-left.
                    mesh.TexCoords[layer] = Map(values, x => new Vector2(x.X, 1 - x.Y));
                    break;
                case Attr.BlendIndices:
                    if (layer != 0) break;
                    mesh.Joints = new int[count * 4];
                    for (int v = 0; v < count; v++)
                    {
                        mesh.Joints[v * 4] = (int)values[v].X; mesh.Joints[v * 4 + 1] = (int)values[v].Y;
                        mesh.Joints[v * 4 + 2] = (int)values[v].Z; mesh.Joints[v * 4 + 3] = (int)values[v].W;
                    }
                    break;
                case Attr.BlendWeights:
                    if (layer != 0) break;
                    mesh.Weights = new float[count * 4];
                    for (int v = 0; v < count; v++)
                    {
                        var w = values[v];
                        float sum = w.X + w.Y + w.Z + w.W;
                        if (sum > 0) w /= sum;
                        mesh.Weights[v * 4] = w.X; mesh.Weights[v * 4 + 1] = w.Y;
                        mesh.Weights[v * 4 + 2] = w.Z; mesh.Weights[v * 4 + 3] = w.W;
                    }
                    break;
            }
        }

        static T[] Map<T>(Vector4[] values, Func<Vector4, T> f)
        {
            var result = new T[values.Length];
            for (int i = 0; i < values.Length; i++) result[i] = f(values[i]);
            return result;
        }

        // Type values from trmsh.fbs.
        static Vector4 Decode(byte[] b, int p, uint type)
        {
            switch (type)
            {
                case 20: return new Vector4(b[p], b[p + 1], b[p + 2], b[p + 3]) / 255f;          // RGBA_8_UNORM
                case 22: return new Vector4(b[p], b[p + 1], b[p + 2], b[p + 3]);                 // RGBA_8_UNSIGNED
                case 36: return new Vector4(BitConverter.ToUInt32(b, p), 0, 0, 0);               // R_32_UINT
                case 37: return new Vector4(BitConverter.ToInt32(b, p), 0, 0, 0);                // R_32_INT
                case 39: return new Vector4(BitConverter.ToUInt16(b, p), BitConverter.ToUInt16(b, p + 2),
                    BitConverter.ToUInt16(b, p + 4), BitConverter.ToUInt16(b, p + 6)) / 65535f;  // RGBA_16_UNORM
                case 43: return new Vector4(Half(b, p), Half(b, p + 2), Half(b, p + 4), Half(b, p + 6)); // RGBA_16_FLOAT
                case 48: return new Vector4(BitConverter.ToSingle(b, p), BitConverter.ToSingle(b, p + 4), 0, 0); // RG_32_FLOAT
                case 51: return new Vector4(BitConverter.ToSingle(b, p), BitConverter.ToSingle(b, p + 4),
                    BitConverter.ToSingle(b, p + 8), 0);                                         // RGB_32_FLOAT
                case 54: return new Vector4(BitConverter.ToSingle(b, p), BitConverter.ToSingle(b, p + 4),
                    BitConverter.ToSingle(b, p + 8), BitConverter.ToSingle(b, p + 12));          // RGBA_32_FLOAT
                default: throw new NotSupportedException($"Vertex type {type}");
            }
        }

        // IEEE 754 half -> float (System.Half isn't in .NET Standard 2.1).
        static float Half(byte[] b, int p)
        {
            int h = BitConverter.ToUInt16(b, p);
            int sign = (h >> 15) & 1, exp = (h >> 10) & 0x1F, mant = h & 0x3FF;
            float v = exp == 0 ? mant / 1024f * (float)Math.Pow(2, -14)
                : exp == 31 ? (mant == 0 ? float.PositiveInfinity : float.NaN)
                : (1 + mant / 1024f) * (float)Math.Pow(2, exp - 15);
            return sign == 1 ? -v : v;
        }
    }
}
