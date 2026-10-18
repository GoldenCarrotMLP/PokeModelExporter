using System.Collections.Generic;
using PokeModelExporter.Export;

namespace PokeModelExporter.Ctr
{
    // A GF model pack (3DS) as one GLB per model, with its decoded textures as PNGs and the game's material setup in the
    // material extras (GfModelConvert): what CLI ctrmodel writes and what the game converts at run time.
    public static class CtrGlb
    {
        public static List<(string Name, byte[] Glb)> FromPack(GfModelPack pack, List<string> warnings = null)
        {
            var textures = new Dictionary<string, byte[]>();
            foreach (var t in pack.Textures)
                if (t.Rgba != null) textures[t.Name] = Png.Encode(t.Rgba, t.Width, t.Height);
            var o = new List<(string, byte[])>();
            foreach (var m in pack.Models)
            {
                var md = GfModelConvert.ToModel(m, pack, out var w);
                warnings?.AddRange(w);
                o.Add((m.Name, GlbWriter.Write(md, textures, mat =>
                {
                    var map = MaterialMapping.Default(mat);
                    map.Unlit = true; map.DoubleSided = true; // the game's combiner and render state come from the extras
                    return map;
                })));
            }
            return o;
        }
    }
}
