using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using PokeModelExporter.Model;

namespace PokeModelExporter.Export
{
    // Writes ModelData as a binary glTF 2.0 file (.glb), dependency-free so it also runs in Unity.
    // Layout: one scene; a root node holding the skeleton nodes and one node per mesh, all meshes
    // skinned to a single skin whose joint k is the node with SkinIndex k (so vertex joint values
    // are used as-is). Textures are embedded PNGs supplied by the caller.
    public sealed class GlbWriter
    {
        readonly StringBuilder _json = new StringBuilder();
        readonly MemoryStream _bin = new MemoryStream();
        readonly List<string> _bufferViews = new List<string>();
        readonly List<string> _accessors = new List<string>();

        static string F(float v) => float.IsFinite(v) ? v.ToString("R", CultureInfo.InvariantCulture) : "0";

        // textures: texture file name (as referenced by materials) -> PNG bytes; may be empty.
        // mapMaterial: how each material uses the textures (default: base color + normal map by name).
        // extraImages: PNGs with no glTF role (e.g. subsurface masks, icons), kept as images with extras.role.
        public static byte[] Write(ModelData model, IReadOnlyDictionary<string, byte[]> textures, Func<MaterialData, MaterialMapping> mapMaterial = null,
            IReadOnlyDictionary<string, byte[]> extraImages = null)
        {
            var w = new GlbWriter();
            return w.Build(model, textures, mapMaterial ?? MaterialMapping.Default, extraImages);
        }

        byte[] Build(ModelData model, IReadOnlyDictionary<string, byte[]> textures, Func<MaterialData, MaterialMapping> mapMaterial,
            IReadOnlyDictionary<string, byte[]> extraImages)
        {
            // ---- Images and textures ----
            var imageIndex = new Dictionary<string, int>();
            var images = new List<string>();
            foreach (var kv in textures)
            {
                int view = AddView(kv.Value, target: 0);
                imageIndex[kv.Key] = images.Count;
                string imageName = System.IO.Path.GetFileNameWithoutExtension(kv.Key); // "pm0718_00_00_body_a_ao", not "...ao.bntx"
                images.Add($"{{\"bufferView\":{view},\"mimeType\":\"image/png\",\"name\":{Str(imageName)}}}");
            }

            int referencedImages = images.Count;
            if (extraImages != null)
                foreach (var kv in extraImages)
                {
                    if (imageIndex.ContainsKey(kv.Key)) continue;
                    int view = AddView(kv.Value, target: 0);
                    images.Add($"{{\"bufferView\":{view},\"mimeType\":\"image/png\",\"name\":{Str(System.IO.Path.GetFileNameWithoutExtension(kv.Key))},\"extras\":{{\"role\":\"unreferenced\",\"file\":{Str(kv.Key)}}}}}");
                }

            // ---- Materials ----
            var materialIndex = new Dictionary<string, int>();
            bool usesUnlit = false, usesVisibility = false;
            var materials = new List<string>();
            foreach (var mat in model.Materials)
            {
                var map = mapMaterial(mat);
                var pbr = new List<string> { $"\"metallicFactor\":{F(map.Metallic)}", $"\"roughnessFactor\":{F(map.Roughness)}" };
                if (map.BaseColorFactor != null)
                    pbr.Add($"\"baseColorFactor\":[{string.Join(",", Array.ConvertAll(map.BaseColorFactor, F))}]");
                if (map.BaseColorTexture != null && imageIndex.TryGetValue(map.BaseColorTexture, out int bc))
                    pbr.Add($"\"baseColorTexture\":{{\"index\":{bc}}}");
                if (map.MetallicRoughnessTexture != null && imageIndex.TryGetValue(map.MetallicRoughnessTexture, out int mr))
                    pbr.Add($"\"metallicRoughnessTexture\":{{\"index\":{mr}}}");
                var parts = new List<string> { $"\"name\":{Str(mat.Name)}", $"\"pbrMetallicRoughness\":{{{string.Join(",", pbr)}}}" };
                if (map.NormalTexture != null && imageIndex.TryGetValue(map.NormalTexture, out int nt))
                    parts.Add($"\"normalTexture\":{{\"index\":{nt}}}");
                if (map.OcclusionTexture != null && imageIndex.TryGetValue(map.OcclusionTexture, out int ot))
                    parts.Add($"\"occlusionTexture\":{{\"index\":{ot}}}");
                if (map.EmissiveTexture != null && imageIndex.TryGetValue(map.EmissiveTexture, out int et))
                {
                    parts.Add($"\"emissiveTexture\":{{\"index\":{et}}}");
                    parts.Add("\"emissiveFactor\":[1,1,1]");
                }
                if (map.AlphaBlend) parts.Add("\"alphaMode\":\"BLEND\"");
                if (map.Unlit) { parts.Add("\"extensions\":{\"KHR_materials_unlit\":{}}"); usesUnlit = true; }
                if (map.DoubleSided) parts.Add("\"doubleSided\":true");
                parts.Add($"\"extras\":{MaterialExtras(mat, map.Maps, imageIndex)}");
                materialIndex[mat.Name] = materials.Count;
                materials.Add("{" + string.Join(",", parts) + "}");
            }

            // ---- Nodes: 0 = root, 1..N = skeleton, then mesh nodes ----
            var nodes = new List<string>();
            var rootChildren = new List<int>();
            var childLists = new List<int>[model.Nodes.Count];
            for (int i = 0; i < model.Nodes.Count; i++) childLists[i] = new List<int>();
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                int parent = model.Nodes[i].Parent;
                if (parent >= 0 && parent < model.Nodes.Count && parent != i) childLists[parent].Add(i);
                else rootChildren.Add(1 + i);
            }
            nodes.Add(null); // root, filled in at the end
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                var n = model.Nodes[i];
                var parts = new List<string> { $"\"name\":{Str(n.Name)}" };
                if (n.Translation != Vector3.Zero) parts.Add($"\"translation\":[{F(n.Translation.X)},{F(n.Translation.Y)},{F(n.Translation.Z)}]");
                if (n.Rotation != Quaternion.Identity)
                {
                    var q = Quaternion.Normalize(n.Rotation);
                    parts.Add($"\"rotation\":[{F(q.X)},{F(q.Y)},{F(q.Z)},{F(q.W)}]");
                }
                if (n.Scale != Vector3.One) parts.Add($"\"scale\":[{F(n.Scale.X)},{F(n.Scale.Y)},{F(n.Scale.Z)}]");
                if (childLists[i].Count > 0) parts.Add($"\"children\":[{string.Join(",", childLists[i].ConvertAll(c => (1 + c).ToString()))}]");
                if (n.IsLocator) parts.Add($"\"extras\":{{\"locator\":true,\"set\":{Str(n.LocatorSet)}}}");
                nodes.Add("{" + string.Join(",", parts) + "}");
            }

            // ---- Skin ----
            string skin = null;
            int maxJoint = -1;
            foreach (var n in model.Nodes) maxJoint = Math.Max(maxJoint, n.SkinIndex);
            if (maxJoint >= 0)
            {
                var world = WorldMatrices(model);
                var jointNodes = new int[maxJoint + 1];
                for (int k = 0; k <= maxJoint; k++) jointNodes[k] = 0; // unused slots point at node 0
                for (int i = 0; i < model.Nodes.Count; i++)
                    if (model.Nodes[i].SkinIndex >= 0) jointNodes[model.Nodes[i].SkinIndex] = i;
                var ibm = new float[(maxJoint + 1) * 16];
                for (int k = 0; k <= maxJoint; k++)
                {
                    Matrix4x4.Invert(world[jointNodes[k]], out var inv);
                    WriteMatrix(ibm, k * 16, inv);
                }
                int ibmAccessor = AddAccessor(Floats(ibm), "MAT4", 5126, maxJoint + 1);
                var joints = new List<string>();
                foreach (int jn in jointNodes) joints.Add((1 + jn).ToString());
                skin = $"{{\"joints\":[{string.Join(",", joints)}],\"inverseBindMatrices\":{ibmAccessor},\"skeleton\":1}}";
            }

            // ---- Meshes ----
            // Inverted-hull outlines (a slightly bigger shell with every triangle facing inwards: Rotom's blue
            // firemask) must be single-sided, so only the far side shows round the silhouette; their material may be
            // double-sided for other meshes (Unlit sheets), so they get a single-sided copy of it.
            var singleSided = new Dictionary<int, int>();
            int SingleSided(int index)
            {
                if (!materials[index].Contains("\"doubleSided\":true")) return index;
                if (!singleSided.TryGetValue(index, out int copy))
                {
                    singleSided[index] = copy = materials.Count;
                    materials.Add(materials[index].Replace(",\"doubleSided\":true", "").Replace("\"name\":\"", "\"name\":\"hull "));
                }
                return copy;
            }
            var meshes = new List<string>();
            foreach (var mesh in model.Meshes)
            {
                if (mesh.Positions == null) continue;
                bool hull = IsInvertedHull(mesh);
                int count = mesh.Positions.Length;
                var attrs = new List<string> { $"\"POSITION\":{AddVec3(mesh.Positions, minMax: true)}" };
                if (mesh.Normals != null) attrs.Add($"\"NORMAL\":{AddVec3(mesh.Normals)}");
                if (mesh.Tangents != null) attrs.Add($"\"TANGENT\":{AddVec4(mesh.Tangents)}");
                for (int t = 0; t < mesh.TexCoords.Count; t++)
                    if (mesh.TexCoords[t] != null) attrs.Add($"\"TEXCOORD_{t}\":{AddVec2(mesh.TexCoords[t])}");
                // Trinity vertex colors drive shader effects (e.g. Braixen's flame), not tint; glTF would
                // multiply COLOR_0 into the base color, so keep them under an application-specific name.
                if (mesh.Colors != null) attrs.Add($"\"{(model.StandardVertexColors ? "COLOR_0" : "_TRINITY_COLOR")}\":{AddVec4(mesh.Colors)}");
                if (skin != null && mesh.Joints != null && mesh.Weights != null)
                {
                    var joints = new byte[count * 8];
                    for (int i = 0; i < count * 4; i++)
                        BitConverter.GetBytes((ushort)Math.Max(0, Math.Min(maxJoint, mesh.Joints[i]))).CopyTo(joints, i * 2);
                    attrs.Add($"\"JOINTS_0\":{AddAccessor(joints, "VEC4", 5123, count, target: 34962)}");
                    attrs.Add($"\"WEIGHTS_0\":{AddAccessor(Floats(mesh.Weights), "VEC4", 5126, count, target: 34962)}");
                }
                string attributes = "{" + string.Join(",", attrs) + "}";
                var prims = new List<string>();
                foreach (var sub in mesh.SubMeshes)
                {
                    var idx = new byte[sub.Indices.Length * 4];
                    for (int i = 0; i < sub.Indices.Length; i++) BitConverter.GetBytes((uint)sub.Indices[i]).CopyTo(idx, i * 4);
                    int ia = AddAccessor(idx, "SCALAR", 5125, sub.Indices.Length, target: 34963);
                    string material = materialIndex.TryGetValue(sub.Material ?? "", out int mi) ? $",\"material\":{(hull ? SingleSided(mi) : mi)}" : "";
                    prims.Add($"{{\"attributes\":{attributes},\"indices\":{ia}{material}}}");
                }
                int meshIndex = meshes.Count;
                meshes.Add($"{{\"name\":{Str(mesh.Name)},\"primitives\":[{string.Join(",", prims)}]}}");
                rootChildren.Add(nodes.Count);
                string hidden = "";
                if (mesh.Hidden) { hidden = ",\"extensions\":{\"KHR_node_visibility\":{\"visible\":false}},\"extras\":{\"hidden\":true}"; usesVisibility = true; }
                nodes.Add($"{{\"name\":{Str(mesh.Name)},\"mesh\":{meshIndex}{(skin != null && mesh.Joints != null ? ",\"skin\":0" : "")}{hidden}}}");
            }
            nodes[0] = $"{{\"name\":{Str(model.Name)},\"children\":[{string.Join(",", rootChildren)}]}}";

            // ---- Animations ----
            var nodeByName = new Dictionary<string, int>();
            for (int i = 0; i < model.Nodes.Count; i++)
                if (model.Nodes[i].Name != null && !nodeByName.ContainsKey(model.Nodes[i].Name)) nodeByName[model.Nodes[i].Name] = 1 + i;
            var animations = new List<string>();
            foreach (var anim in model.Animations)
            {
                var samplers = new List<string>();
                var channels = new List<string>();
                float fps = anim.FramesPerSecond > 0 ? anim.FramesPerSecond : 30;
                foreach (var track in anim.Tracks)
                {
                    if (track.Bone == null || !nodeByName.TryGetValue(track.Bone, out int node)) continue;
                    if (track.Translation != null)
                        AddChannel(samplers, channels, node, "translation", track.Translation.Times, fps, AddVec3Raw(track.Translation.Values));
                    if (track.Rotation != null)
                        AddChannel(samplers, channels, node, "rotation", track.Rotation.Times, fps, AddRotations(track.Rotation.Values));
                    if (track.Scale != null)
                        AddChannel(samplers, channels, node, "scale", track.Scale.Times, fps, AddVec3Raw(track.Scale.Values));
                }
                if (channels.Count > 0)
                    animations.Add($"{{\"name\":{Str(anim.Name)},\"samplers\":[{string.Join(",", samplers)}],\"channels\":[{string.Join(",", channels)}]{AnimationExtras(anim, fps)}}}");
            }

            // ---- JSON ----
            var j = new StringBuilder();
            j.Append("{\"asset\":{\"version\":\"2.0\",\"generator\":\"PokeModelExporter\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}]");
            var used = new List<string>();
            if (usesUnlit) used.Add("\"KHR_materials_unlit\"");
            if (usesVisibility) used.Add("\"KHR_node_visibility\"");
            if (used.Count > 0) j.Append($",\"extensionsUsed\":[{string.Join(",", used)}]");
            j.Append($",\"nodes\":[{string.Join(",", nodes)}]");
            if (meshes.Count > 0) j.Append($",\"meshes\":[{string.Join(",", meshes)}]");
            if (skin != null) j.Append($",\"skins\":[{skin}]");
            if (animations.Count > 0) j.Append($",\"animations\":[{string.Join(",", animations)}]");
            if (materials.Count > 0) j.Append($",\"materials\":[{string.Join(",", materials)}]");
            if (images.Count > 0) j.Append($",\"images\":[{string.Join(",", images)}]");
            if (referencedImages > 0)
            {
                var tex = new List<string>();
                for (int i = 0; i < referencedImages; i++) tex.Add($"{{\"source\":{i},\"sampler\":0}}");
                j.Append($",\"textures\":[{string.Join(",", tex)}]");
                j.Append(",\"samplers\":[{\"magFilter\":9729,\"minFilter\":9987,\"wrapS\":10497,\"wrapT\":10497}]");
            }
            j.Append($",\"accessors\":[{string.Join(",", _accessors)}]");
            j.Append($",\"bufferViews\":[{string.Join(",", _bufferViews)}]");
            j.Append($",\"buffers\":[{{\"byteLength\":{_bin.Length}}}]}}");
            return Pack(j.ToString(), _bin.ToArray());
        }

        // ---- Buffers ----

        int AddView(byte[] data, int target)
        {
            while (_bin.Length % 4 != 0) _bin.WriteByte(0);
            long offset = _bin.Length;
            _bin.Write(data, 0, data.Length);
            _bufferViews.Add($"{{\"buffer\":0,\"byteOffset\":{offset},\"byteLength\":{data.Length}{(target != 0 ? $",\"target\":{target}" : "")}}}");
            return _bufferViews.Count - 1;
        }

        int AddAccessor(byte[] data, string type, int componentType, int count, int target = 0, string minMax = null)
        {
            int view = AddView(data, target);
            _accessors.Add($"{{\"bufferView\":{view},\"componentType\":{componentType},\"count\":{count},\"type\":\"{type}\"{minMax ?? ""}}}");
            return _accessors.Count - 1;
        }

        int AddVec2(Vector2[] v)
        {
            var f = new float[v.Length * 2];
            for (int i = 0; i < v.Length; i++) { f[i * 2] = v[i].X; f[i * 2 + 1] = v[i].Y; }
            return AddAccessor(Floats(f), "VEC2", 5126, v.Length, target: 34962);
        }

        int AddVec3(Vector3[] v, bool minMax = false)
        {
            var f = new float[v.Length * 3];
            Vector3 min = new Vector3(float.MaxValue), max = new Vector3(float.MinValue);
            for (int i = 0; i < v.Length; i++)
            {
                f[i * 3] = v[i].X; f[i * 3 + 1] = v[i].Y; f[i * 3 + 2] = v[i].Z;
                min = Vector3.Min(min, v[i]);
                max = Vector3.Max(max, v[i]);
            }
            string mm = minMax ? $",\"min\":[{F(min.X)},{F(min.Y)},{F(min.Z)}],\"max\":[{F(max.X)},{F(max.Y)},{F(max.Z)}]" : null;
            return AddAccessor(Floats(f), "VEC3", 5126, v.Length, target: 34962, minMax: mm);
        }

        int AddVec4(Vector4[] v)
        {
            var f = new float[v.Length * 4];
            for (int i = 0; i < v.Length; i++) { f[i * 4] = v[i].X; f[i * 4 + 1] = v[i].Y; f[i * 4 + 2] = v[i].Z; f[i * 4 + 3] = v[i].W; }
            return AddAccessor(Floats(f), "VEC4", 5126, v.Length, target: 34962);
        }

        void AddChannel(List<string> samplers, List<string> channels, int node, string path, float[] frames, float fps, int output)
        {
            var times = new float[frames.Length];
            for (int i = 0; i < frames.Length; i++) times[i] = frames[i] / fps;
            string minMax = $",\"min\":[{F(times[0])}],\"max\":[{F(times[times.Length - 1])}]";
            int input = AddAccessor(Floats(times), "SCALAR", 5126, times.Length, minMax: minMax);
            channels.Add($"{{\"sampler\":{samplers.Count},\"target\":{{\"node\":{node},\"path\":\"{path}\"}}}}");
            samplers.Add($"{{\"input\":{input},\"output\":{output},\"interpolation\":\"LINEAR\"}}");
        }

        int AddVec3Raw(Vector3[] v)
        {
            var f = new float[v.Length * 3];
            for (int i = 0; i < v.Length; i++) { f[i * 3] = v[i].X; f[i * 3 + 1] = v[i].Y; f[i * 3 + 2] = v[i].Z; }
            return AddAccessor(Floats(f), "VEC3", 5126, v.Length);
        }

        // Unit quaternions, each flipped to the same hemisphere as the previous so LINEAR (slerp) takes the short path.
        int AddRotations(Quaternion[] q)
        {
            var f = new float[q.Length * 4];
            var prev = Quaternion.Identity;
            for (int i = 0; i < q.Length; i++)
            {
                var r = Quaternion.Normalize(q[i]);
                if (i > 0 && Quaternion.Dot(prev, r) < 0) r = Quaternion.Negate(r);
                prev = r;
                f[i * 4] = r.X; f[i * 4 + 1] = r.Y; f[i * 4 + 2] = r.Z; f[i * 4 + 3] = r.W;
            }
            return AddAccessor(Floats(f), "VEC4", 5126, q.Length);
        }

        static byte[] Floats(float[] f)
        {
            var b = new byte[f.Length * 4];
            Buffer.BlockCopy(f, 0, b, 0, b.Length);
            return b;
        }

        // glTF matrices are column-major; System.Numerics is row-vector (M41..M43 = translation).
        static void WriteMatrix(float[] dst, int at, Matrix4x4 m)
        {
            float[] v = { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 };
            Array.Copy(v, 0, dst, at, 16);
        }

        // A closed shell whose triangles (nearly) all face its middle: an inverted-hull outline. Over 336 cached
        // models, ordinary meshes reach at most 0.79 inward; hulls 0.94 (Rotom-Wash's aura, not convex) to 1.
        static bool IsInvertedHull(MeshData mesh)
        {
            var c = Vector3.Zero;
            foreach (var p in mesh.Positions) c += p;
            c /= mesh.Positions.Length;
            int inward = 0, total = 0;
            foreach (var sub in mesh.SubMeshes)
                for (int t = 0; t + 2 < sub.Indices.Length; t += 3)
                {
                    Vector3 a = mesh.Positions[sub.Indices[t]], b = mesh.Positions[sub.Indices[t + 1]], d = mesh.Positions[sub.Indices[t + 2]];
                    var normal = Vector3.Cross(b - a, d - a);
                    if (Vector3.Dot(normal, (a + b + d) / 3 - c) < 0) inward++;
                    total++;
                }
            return total >= 20 && inward >= total * 0.9f;
        }

        internal static Matrix4x4[] WorldMatrices(ModelData model)
        {
            var world = new Matrix4x4[model.Nodes.Count];
            var done = new bool[model.Nodes.Count];
            Matrix4x4 Get(int i, int depth)
            {
                if (done[i]) return world[i];
                var n = model.Nodes[i];
                var local = Matrix4x4.CreateScale(n.Scale) * Matrix4x4.CreateFromQuaternion(n.Rotation) * Matrix4x4.CreateTranslation(n.Translation);
                world[i] = n.Parent >= 0 && n.Parent < model.Nodes.Count && n.Parent != i && depth < 512 ? local * Get(n.Parent, depth + 1) : local;
                done[i] = true;
                return world[i];
            }
            for (int i = 0; i < model.Nodes.Count; i++) Get(i, 0);
            return world;
        }

        // Notifies as animation extras: {"loop":bool,"events":[{"type","time","duration","params":{...}}]} (seconds).
        static string AnimationExtras(AnimationData anim, float fps)
        {
            var events = new List<string>();
            foreach (var e in anim.Events)
            {
                var ps = new List<string>();
                foreach (var kv in e.Parameters) ps.Add($"{Str(kv.Key)}:{Str(kv.Value)}");
                events.Add($"{{\"type\":{Str(e.Type)},\"time\":{F(e.StartFrame / fps)},\"duration\":{F(e.DurationFrames / fps)},\"params\":{{{string.Join(",", ps)}}}}}");
            }
            var materials = new List<string>();
            foreach (var t in anim.MaterialTracks)
            {
                var ch = new List<string>();
                foreach (var (name, keys) in new[] { ("r", t.R), ("g", t.G), ("b", t.B), ("a", t.A) })
                    if (keys != null) ch.Add($"\"{name}\":{KeysJson(keys.Times, keys.Values, fps)}");
                materials.Add($"{{\"mesh\":{Str(t.Mesh)},\"material\":{Str(t.Material)},\"parameter\":{Str(t.Parameter)},{string.Join(",", ch)}}}");
            }
            var visibility = new List<string>();
            foreach (var v in anim.VisibilityTracks)
            {
                if (v.Fixed.HasValue) { visibility.Add($"{{\"mesh\":{Str(v.Mesh)},\"visible\":{(v.Fixed.Value ? "true" : "false")}}}"); continue; }
                var keys = new List<string>();
                for (int k = 0; k < v.Keys.Times.Length; k++) keys.Add($"[{F(v.Keys.Times[k] / fps)},{(v.Keys.Values[k] ? "true" : "false")}]");
                visibility.Add($"{{\"mesh\":{Str(v.Mesh)},\"keys\":[{string.Join(",", keys)}]}}");
            }
            var morphs = new List<string>();
            foreach (var m in anim.MorphTracks)
                morphs.Add($"{{\"mesh\":{Str(m.Mesh)},\"shape\":{Str(m.Shape)},\"weight\":{KeysJson(m.Weight.Times, m.Weight.Values, fps)}}}");
            return $",\"extras\":{{\"loop\":{(anim.Loop ? "true" : "false")},\"events\":[{string.Join(",", events)}]" +
                   $",\"materialTracks\":[{string.Join(",", materials)}],\"visibilityTracks\":[{string.Join(",", visibility)}]" +
                   $",\"morphTracks\":[{string.Join(",", morphs)}]}}";
        }

        // [[seconds, value], ...]
        static string KeysJson(float[] frames, float[] values, float fps)
        {
            var parts = new List<string>();
            for (int i = 0; i < frames.Length && i < values.Length; i++) parts.Add($"[{F(frames[i] / fps)},{F(values[i])}]");
            return "[" + string.Join(",", parts) + "]";
        }

        // maps: game texture slots shipped for engine shaders (SSS mask, layer mask...) -> their glTF texture index.
        static string MaterialExtras(MaterialData m, Dictionary<string, string> maps, Dictionary<string, int> imageIndex)
        {
            var parts = new List<string>();
            var mapParts = new List<string>();
            foreach (var kv in maps)
                if (imageIndex.TryGetValue(kv.Value, out int index)) mapParts.Add($"{Str(kv.Key)}:{index}");
            if (mapParts.Count > 0) parts.Add($"\"maps\":{{{string.Join(",", mapParts)}}}");
            if (m.Shader != null) parts.Add($"\"shader\":{Str(m.Shader)}");
            if (m.AlphaType != null) parts.Add($"\"alphaType\":{Str(m.AlphaType)}");
            var tex = new List<string>();
            foreach (var kv in m.Textures) tex.Add($"{Str(kv.Key)}:{Str(kv.Value)}");
            parts.Add($"\"textures\":{{{string.Join(",", tex)}}}");
            var floats = new List<string>();
            foreach (var kv in m.Floats) floats.Add($"{Str(kv.Key)}:{F(kv.Value)}");
            parts.Add($"\"floats\":{{{string.Join(",", floats)}}}");
            var colors = new List<string>();
            foreach (var kv in m.Colors) colors.Add($"{Str(kv.Key)}:[{F(kv.Value.X)},{F(kv.Value.Y)},{F(kv.Value.Z)},{F(kv.Value.W)}]");
            parts.Add($"\"colors\":{{{string.Join(",", colors)}}}");
            var options = new List<string>();
            foreach (var kv in m.ShaderOptions) options.Add($"{Str(kv.Key)}:{Str(kv.Value)}");
            parts.Add($"\"options\":{{{string.Join(",", options)}}}");
            return "{" + string.Join(",", parts) + "}";
        }

        static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        // GLB container: header, JSON chunk (space-padded), BIN chunk (zero-padded).
        static byte[] Pack(string json, byte[] bin)
        {
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            int jsonPad = (4 - jsonBytes.Length % 4) % 4, binPad = (4 - bin.Length % 4) % 4;
            using var o = new MemoryStream();
            using var w = new BinaryWriter(o);
            int total = 12 + 8 + jsonBytes.Length + jsonPad + 8 + bin.Length + binPad;
            w.Write(0x46546C67); w.Write(2); w.Write(total);
            w.Write(jsonBytes.Length + jsonPad); w.Write(0x4E4F534A); w.Write(jsonBytes);
            for (int i = 0; i < jsonPad; i++) w.Write((byte)0x20);
            w.Write(bin.Length + binPad); w.Write(0x004E4942); w.Write(bin);
            for (int i = 0; i < binPad; i++) w.Write((byte)0);
            return o.ToArray();
        }
    }

    // Which game textures fill the standard glTF material slots (game-specific values stay in extras).
    public sealed class MaterialMapping
    {
        public string BaseColorTexture, NormalTexture, EmissiveTexture;
        // glTF packing: occlusion in R (OcclusionTexture), roughness in G, metallic in B (MetallicRoughnessTexture).
        public string MetallicRoughnessTexture, OcclusionTexture;
        public float Roughness = 0.8f, Metallic;
        public float[] BaseColorFactor; // rgba; null = white (the texture alone)
        public bool AlphaBlend, DoubleSided, Unlit;
        // Game texture slots without a glTF role that engine shaders use (SSSMaskMap, LayerMaskMap, raw BaseColorMap,
        // DisplacementMap, NormalMap1) -> texture name; written as plain (linear) textures, listed in extras.maps.
        public readonly Dictionary<string, string> Maps = new Dictionary<string, string>();

        public static MaterialMapping Default(MaterialData m)
        {
            m.Textures.TryGetValue("BaseColorMap", out var bc);
            m.Textures.TryGetValue("NormalMap", out var nm);
            return new MaterialMapping
            {
                BaseColorTexture = bc,
                NormalTexture = nm,
                AlphaBlend = m.AlphaType != null && m.AlphaType.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0,
                DoubleSided = m.Shader == "Unlit",
            };
        }
    }
}
