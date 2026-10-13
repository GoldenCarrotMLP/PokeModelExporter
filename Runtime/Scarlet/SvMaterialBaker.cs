using System;
using System.Collections.Generic;
using System.Numerics;
using PokeModelExporter.Export;
using PokeModelExporter.Formats;
using PokeModelExporter.Model;

namespace PokeModelExporter.Scarlet
{
    // Turns Scarlet/Violet materials into plain glTF textures.
    //
    // Many Trinity materials are "layered": a LayerMaskMap (the *_lym texture) holds one mask per
    // channel and the material holds a color per layer, e.g. Braixen's eye:
    //   R = sclera -> BaseColorLayer1, G = iris -> Layer2, B = iris ring -> Layer3, A = pupil -> Layer4
    // with EmissionColorLayerN * EmissionIntensityLayerN as glow. The game's shader mixes these at
    // runtime; we bake the result once (base color and emission PNGs) so any glTF renderer shows it.
    // The original parameters stay in the GLB's material extras for a future Unity shader.
    public static class SvMaterialBaker
    {
        public sealed class Result
        {
            public readonly Dictionary<string, byte[]> Textures = new Dictionary<string, byte[]>(); // name -> PNG
            public readonly Dictionary<string, MaterialMapping> Mappings = new Dictionary<string, MaterialMapping>();
        }

        // readTexture: texture file name (as in the material) -> BNTX bytes, or null.
        public static Result Bake(ModelData model, Func<string, byte[]> readTexture)
        {
            var result = new Result();
            var decoded = new Dictionary<string, Bntx.Texture>();
            Bntx.Texture Load(string file, bool normal)
            {
                if (file == null) return null;
                string key = file + (normal ? "#n" : "");
                if (decoded.TryGetValue(key, out var t)) return t;
                var bytes = readTexture(file);
                return decoded[key] = bytes == null ? null : Bntx.Read(bytes, normal);
            }

            foreach (var mat in model.Materials)
            {
                var map = MaterialMapping.Default(mat);
                map.Unlit = mat.Shader == "Unlit";
                mat.Textures.TryGetValue("BaseColorMap", out var baseFile);
                mat.Textures.TryGetValue("LayerMaskMap", out var maskFile);
                var baseTex = Load(baseFile, false);
                var mask = Load(maskFile, false);

                if (mask != null && HasLayers(mat))
                {
                    // Layer 5 (eye highlight) has no channel in the 4-channel layer mask; its mask is the
                    // sibling *_msk texture (eye_lym -> eye_msk), used when the material enables a highlight.
                    Bntx.Texture highlight = null;
                    if (HasHighlight(mat) && maskFile.Contains("_lym"))
                        highlight = Load(maskFile.Replace("_lym", "_msk"), false);
                    var (color, emission) = BakeLayers(mat, baseTex, mask, highlight);
                    string colorName = mat.Name + "_color.png";
                    result.Textures[colorName] = Png.Encode(color, mask.Width, mask.Height);
                    map.BaseColorTexture = colorName;
                    if (emission != null)
                    {
                        string emissionName = mat.Name + "_emission.png";
                        result.Textures[emissionName] = Png.Encode(emission, mask.Width, mask.Height);
                        map.EmissiveTexture = emissionName;
                    }
                }
                else if (baseTex != null)
                {
                    result.Textures[baseFile] = Png.Encode(baseTex.Rgba, baseTex.Width, baseTex.Height);
                }

                // PBR: roughness / AO / metallic maps packed into one glTF ORM texture.
                if (!map.Unlit) BakeOrm(mat, map, Load, result);

                if (map.NormalTexture != null && !map.Unlit)
                {
                    var n = Load(map.NormalTexture, normal: true);
                    if (n != null) result.Textures[map.NormalTexture] = Png.Encode(n.Rgba, n.Width, n.Height);
                    else map.NormalTexture = null;
                }
                result.Mappings[mat.Name] = map;
            }
            return result;
        }

        // AOMap -> its own occlusion texture (kept under its game name so it's easy to find);
        // RoughnessMap / MetallicMap -> one glTF metallic-roughness texture (G roughness, B metallic).
        // All are single-channel BC4 maps (value in R). Without maps, the material's Roughness /
        // Metallic floats become the factors.
        static void BakeOrm(MaterialData m, MaterialMapping map, Func<string, bool, Bntx.Texture> load, Result result)
        {
            m.Textures.TryGetValue("AOMap", out var aoFile);
            m.Textures.TryGetValue("RoughnessMap", out var roughFile);
            m.Textures.TryGetValue("MetallicMap", out var metalFile);
            var ao = load(aoFile, false);
            var rough = load(roughFile, false);
            var metal = load(metalFile, false);
            map.Roughness = rough != null ? 1f : (m.Floats.TryGetValue("Roughness", out var r) ? r : 0.8f);
            map.Metallic = metal != null ? 1f : (m.Floats.TryGetValue("Metallic", out var mt) ? mt : 0f);

            if (ao != null)
            {
                result.Textures[aoFile] = Png.Encode(ao.Rgba, ao.Width, ao.Height); // gray: R = G = B = AO
                map.OcclusionTexture = aoFile;
            }
            if (rough == null && metal == null) return;

            int w = Math.Max(rough?.Width ?? 1, metal?.Width ?? 1), h = Math.Max(rough?.Height ?? 1, metal?.Height ?? 1);
            var mr = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w, v = (y + 0.5f) / h;
                int p = (y * w + x) * 4;
                mr[p] = 255;
                mr[p + 1] = rough != null ? ToByte(Sample(rough, u, v).X) : (byte)255;
                mr[p + 2] = metal != null ? ToByte(Sample(metal, u, v).X) : (byte)0;
                mr[p + 3] = 255;
            }
            string name = m.Name + "_metallic_roughness.png";
            result.Textures[name] = Png.Encode(mr, w, h);
            map.MetallicRoughnessTexture = name;
        }

        // Every texture the given files reference or list that the GLB doesn't already use, as PNG
        // (normal maps get Z rebuilt). files: texture file names relative to the model folder.
        public static Dictionary<string, byte[]> ExtraTextures(IEnumerable<string> files, Func<string, byte[]> read, Result used)
        {
            var extra = new Dictionary<string, byte[]>();
            foreach (var file in files)
            {
                if (file == null || extra.ContainsKey(file) || used.Textures.ContainsKey(file)) continue;
                var bytes = read(file);
                if (bytes == null) continue;
                try
                {
                    var t = Bntx.Read(bytes, normalMap: file.Contains("_nrm"));
                    extra[file] = Png.Encode(t.Rgba, t.Width, t.Height);
                }
                catch (Exception) { /* unsupported format: skip */ }
            }
            return extra;
        }

        static bool HasHighlight(MaterialData m) =>
            m.ShaderOptions.TryGetValue("EnableHighlight", out var h) && h == "True" &&
            (m.Colors.ContainsKey("EmissionColorLayer5") || m.Colors.ContainsKey("BaseColorLayer5"));

        static bool HasLayers(MaterialData m)
        {
            for (int i = 1; i <= 4; i++)
                if (m.Colors.ContainsKey("BaseColorLayer" + i)) return true;
            return false;
        }

        // Base color: start from the base map, then for each layer i blend toward base * BaseColorLayer_i by
        // mask channel i (R,G,B,A) scaled by LayerMaskScale_i; layer 5 (highlight) uses its own mask.
        // Emission: sum of mask_i * EmissionColorLayer_i * EmissionIntensityLayer_i.
        // HDR colors (flames go up to 4.0) are tone-mapped so their excess brightness reads as glow.
        static (byte[] color, byte[] emission) BakeLayers(MaterialData m, Bntx.Texture baseTex, Bntx.Texture mask, Bntx.Texture highlight)
        {
            int w = mask.Width, h = mask.Height;
            var color = new byte[w * h * 4];
            var emission = new byte[w * h * 4];
            bool anyEmission = false;

            var layers = new Vector4?[4];
            var glow = new Vector3[4];
            var scales = new float[4];
            for (int i = 0; i < 4; i++)
            {
                if (m.Colors.TryGetValue("BaseColorLayer" + (i + 1), out var c)) layers[i] = c;
                scales[i] = m.Floats.TryGetValue("LayerMaskScale" + (i + 1), out var s) ? s : 1f;
                if (m.Colors.TryGetValue("EmissionColorLayer" + (i + 1), out var e))
                {
                    float intensity = m.Floats.TryGetValue("EmissionIntensityLayer" + (i + 1), out var k) ? k : 0f;
                    glow[i] = new Vector3(e.X, e.Y, e.Z) * intensity;
                }
            }

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = (y * w + x) * 4;
                var b = Sample(baseTex, (x + 0.5f) / w, (y + 0.5f) / h);
                var c = b;
                var e = Vector3.Zero;
                for (int i = 0; i < 4; i++)
                {
                    float weight = Math.Min(1f, mask.Rgba[p + i] / 255f * scales[i]);
                    if (weight <= 0) continue;
                    if (layers[i] is Vector4 layer)
                        c = Vector4.Lerp(c, new Vector4(b.X * layer.X, b.Y * layer.Y, b.Z * layer.Z, b.W), weight);
                    e += glow[i] * weight;
                }
                if (highlight != null)
                {
                    float hw = Sample(highlight, (x + 0.5f) / w, (y + 0.5f) / h).X;
                    if (hw > 0)
                    {
                        var hc = m.Colors.TryGetValue("BaseColorLayer5", out var b5) ? b5 :
                            m.Colors.TryGetValue("EmissionColorLayer5", out var e5) ? e5 : Vector4.One;
                        c = Vector4.Lerp(c, new Vector4(hc.X, hc.Y, hc.Z, c.W), hw);
                        float hi = m.Floats.TryGetValue("EmissionIntensityLayer5", out var k5) ? k5 : 1f;
                        var he = m.Colors.TryGetValue("EmissionColorLayer5", out var ec5) ? ec5 : hc;
                        e += new Vector3(he.X, he.Y, he.Z) * hi * hw;
                    }
                }
                var mapped = ToneMap(new Vector3(c.X, c.Y, c.Z));
                c = new Vector4(mapped, c.W);
                color[p] = ToByte(c.X); color[p + 1] = ToByte(c.Y); color[p + 2] = ToByte(c.Z); color[p + 3] = ToByte(c.W);
                emission[p] = ToByte(e.X * c.X); emission[p + 1] = ToByte(e.Y * c.Y); emission[p + 2] = ToByte(e.Z * c.Z); emission[p + 3] = 255;
                if (e != Vector3.Zero) anyEmission = true;
            }
            return (color, anyEmission ? emission : null);
        }

        // Keeps hue for colors brighter than 1: the excess spills into the other channels, the way a
        // bright emissive looks with bloom (4.0 red -> hot red-orange, 2.5 orange -> yellow).
        static Vector3 ToneMap(Vector3 c)
        {
            var over = Vector3.Max(c - Vector3.One, Vector3.Zero);
            float spill = (over.X + over.Y + over.Z) * 0.06f;
            return Vector3.Min(Vector3.Max(c, Vector3.Zero) + new Vector3(spill), Vector3.One);
        }

        static Vector4 Sample(Bntx.Texture t, float u, float v)
        {
            if (t == null) return Vector4.One;
            int x = Math.Min(t.Width - 1, (int)(u * t.Width)), y = Math.Min(t.Height - 1, (int)(v * t.Height));
            int p = (y * t.Width + x) * 4;
            return new Vector4(t.Rgba[p], t.Rgba[p + 1], t.Rgba[p + 2], t.Rgba[p + 3]) / 255f;
        }

        static byte ToByte(float v) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v * 255)));
    }
}
