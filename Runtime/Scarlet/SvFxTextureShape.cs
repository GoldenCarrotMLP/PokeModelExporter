using System;
using System.Collections.Generic;
using System.Globalization;
using PokeModelExporter.Export;

namespace PokeModelExporter.Scarlet
{
    // Describes an effect texture's shape with a few numbers, so an engine can draw its own look-alike
    // instead of using the game's image. Effect textures are mostly single-channel masks:
    //   radial: a falloff around the centre or a corner (quarter textures are mirrored by their emitter's UVs):
    //           32-sample profile from the centre outwards, optional star rays (count, depth)
    //   linear: a gradient or bar along one axis ("x": varies left to right): 32-sample profile
    //   noise:  everything else: mean, contrast, feature size (pixels), streak direction (anisotropy), coverage
    // Flipbooks (the emitter's UV divisions) are described from their first cell; every cell is drawn alike.
    // Coordinates are the image's: x right, y down; centre "br" = bottom-right corner of the image.
    public sealed class FxTextureShape
    {
        public string Kind; // radial / noise
        public int Width, Height, CellsX = 1, CellsY = 1;
        public string Center; // c, tl, tr, bl, br, l, r, t, b
        public string Axis;   // linear: x or y
        public string Source; // the particle file (archive path) holding the texture, for engines that read the game's own
        public float[] Profile;
        public int Rays;
        public float RayDepth, Symmetry, Mean, Contrast, FeatureSize, Anisotropy, Coverage, Ridge;

        const int Bins = 32;

        // mask: one value per pixel (0..1), row 0 = top.
        public static FxTextureShape Analyze(float[] mask, int width, int height, int cellsX, int cellsY)
        {
            cellsX = Math.Max(1, cellsX); cellsY = Math.Max(1, cellsY);
            int w = width / cellsX, h = height / cellsY;
            var cell = new float[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) cell[y * w + x] = mask[y * width + x];
            var s = new FxTextureShape { Width = width, Height = height, CellsX = cellsX, CellsY = cellsY };
            Stats(cell, w, h, s);

            // The most radially symmetric centre wins.
            var centres = new (string name, float x, float y)[]
            {
                ("c", 0.5f, 0.5f), ("tl", 0, 0), ("tr", 1, 0), ("bl", 0, 1), ("br", 1, 1),
                ("l", 0, 0.5f), ("r", 1, 0.5f), ("t", 0.5f, 0), ("b", 0.5f, 1),
            };
            float best = -1;
            foreach (var c in centres)
            {
                float score = Radial(cell, w, h, c.x, c.y, out var profile, out int rays, out float depth);
                if (score > best) { best = score; s.Center = c.name; s.Profile = profile; s.Rays = rays; s.RayDepth = depth; }
            }
            s.Symmetry = best;
            float alongX = Linear(cell, w, h, true, out var px), alongY = Linear(cell, w, h, false, out var py);
            float linear = Math.Max(alongX, alongY);
            if (linear > 0.85f && linear > best)
            {
                s.Kind = "linear";
                s.Axis = alongX >= alongY ? "x" : "y";
                s.Profile = alongX >= alongY ? px : py;
                s.Symmetry = linear;
                s.Center = null; s.Rays = 0; s.RayDepth = 0;
                return s;
            }
            s.Kind = best > 0.5f || best > 0.25f && s.Rays > 0 ? "radial" : "noise";
            if (s.Kind == "noise") { s.Center = null; s.Profile = null; s.Rays = 0; s.RayDepth = 0; }
            return s;
        }

        // Share of the variance explained by distance from (cx, cy) (1 = perfectly radial), the mean profile,
        // and star rays: the strongest angular frequency (3..32) at the radius with the most angular variation.
        static float Radial(float[] m, int w, int h, float cx, float cy, out float[] profile, out int rays, out float depth)
        {
            const int Angles = 64;
            float px = cx * (w - 1), py = cy * (h - 1);
            // Radius 1 = to the farthest point along an axis: half the size from the centre, the full size from an edge.
            float rx = cx == 0.5f ? w / 2f : w, ry = cy == 0.5f ? h / 2f : h;
            var sum = new double[Bins]; var sum2 = new double[Bins]; var n = new int[Bins];
            var polar = new double[Bins, Angles]; var polarN = new int[Bins, Angles];
            double total = 0, total2 = 0; int count = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float dx = (x - px) / rx, dy = (y - py) / ry;
                    float r = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (r >= 1) continue;
                    int b = (int)(r * Bins);
                    double v = m[y * w + x];
                    sum[b] += v; sum2[b] += v * v; n[b]++;
                    int a = (int)((Math.Atan2(dy, dx) / (2 * Math.PI) + 1) * Angles) % Angles;
                    polar[b, a] += v; polarN[b, a]++;
                    total += v; total2 += v * v; count++;
                }
            profile = new float[Bins];
            rays = 0; depth = 0;
            if (count == 0) return 0;
            double mean = total / count, variance = total2 / count - mean * mean;
            double within = 0;
            for (int b = 0; b < Bins; b++)
            {
                if (n[b] == 0) { profile[b] = b > 0 ? profile[b - 1] : 0; continue; }
                double mb = sum[b] / n[b];
                profile[b] = (float)mb;
                within += sum2[b] - n[b] * mb * mb;
            }
            float score = variance <= 1e-6 ? 0 : (float)(1 - within / count / variance);

            // Rays: only around the centre (corner textures are quarters of round shapes).
            if (cx == 0.5f && cy == 0.5f)
            {
                double bestPower = 0; int bestK = 0; double bestMean = 0;
                for (int b = 2; b < Bins - 4; b++)
                {
                    var ring = new double[Angles];
                    double rm = 0; int filled = 0;
                    for (int a = 0; a < Angles; a++) if (polarN[b, a] > 0) { ring[a] = polar[b, a] / polarN[b, a]; rm += ring[a]; filled++; }
                    if (filled < Angles * 3 / 4) continue;
                    rm /= filled;
                    for (int k = 5; k <= 16; k++) // fewer than 5 is the shape itself, not rays
                    {
                        double re = 0, im = 0;
                        for (int a = 0; a < Angles; a++) { double t = 2 * Math.PI * k * a / Angles; re += (ring[a] - rm) * Math.Cos(t); im += (ring[a] - rm) * Math.Sin(t); }
                        double power = Math.Sqrt(re * re + im * im) * 2 / Angles;
                        if (power > bestPower) { bestPower = power; bestK = k; bestMean = rm; }
                    }
                }
                if (bestMean > 0.004 && bestPower / Math.Max(bestMean, 1e-3) > 0.35)
                {
                    rays = bestK;
                    depth = (float)Math.Min(1, bestPower / bestMean);
                }
            }
            return score;
        }

        // Share of the variance explained by position along one axis, and the mean profile along it.
        static float Linear(float[] m, int w, int h, bool alongX, out float[] profile)
        {
            profile = new float[Bins];
            var sum = new double[Bins]; var n = new int[Bins];
            double total = 0, total2 = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int b = Math.Min(Bins - 1, (alongX ? x * Bins / w : y * Bins / h));
                    double v = m[y * w + x];
                    sum[b] += v; n[b]++; total += v; total2 += v * v;
                }
            int count = w * h;
            double mean = total / count, variance = total2 / count - mean * mean;
            if (variance <= 1e-6) return 0;
            double between = 0;
            for (int b = 0; b < Bins; b++)
            {
                if (n[b] == 0) continue;
                double mb = sum[b] / n[b];
                profile[b] = (float)mb;
                between += n[b] * (mb - mean) * (mb - mean);
            }
            return (float)(between / count / variance);
        }

        static void Stats(float[] m, int w, int h, FxTextureShape s)
        {
            double sum = 0, sum2 = 0, gx = 0, gy = 0; int covered = 0;
            for (int i = 0; i < m.Length; i++) { sum += m[i]; sum2 += m[i] * m[i]; if (m[i] > 0.1f) covered++; }
            for (int y = 0; y < h - 1; y++)
                for (int x = 0; x < w - 1; x++)
                {
                    gx += Math.Abs(m[y * w + x + 1] - m[y * w + x]);
                    gy += Math.Abs(m[(y + 1) * w + x] - m[y * w + x]);
                }
            double mean = sum / m.Length, std = Math.Sqrt(Math.Max(0, sum2 / m.Length - mean * mean));
            double grad = (gx + gy) / Math.Max(1, (w - 1) * (h - 1));
            s.Mean = (float)mean;
            s.Contrast = (float)std;
            // A blob of size L (pixels) with contrast std has gradients ~ std / L.
            s.FeatureSize = grad > 1e-6 ? (float)Math.Min(Math.Max(w, h), std / grad * 2) : Math.Max(w, h);
            s.Anisotropy = (float)((gx + 1e-6) / (gy + 1e-6)); // > 1: varies along x (vertical streaks)
            s.Coverage = (float)covered / m.Length;
            // Ridges: thin bright lines (cell edges of caustic patterns) have a strong Laplacian for their contrast.
            double lap = 0;
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                    lap += Math.Abs(4 * m[y * w + x] - m[y * w + x - 1] - m[y * w + x + 1] - m[(y - 1) * w + x] - m[(y + 1) * w + x]);
            s.Ridge = std > 1e-4 ? (float)(lap / Math.Max(1, (w - 2) * (h - 2)) / std) : 0;
        }

        public void Write(JsonWriter w)
        {
            string N(float f) => Math.Round(f, 3).ToString("0.###", CultureInfo.InvariantCulture);
            w.BeginObject().Name("kind").Value(Kind).Name("size").BeginArray().Value(Width).Value(Height).EndArray();
            if (Source != null) w.Name("source").Value(Source);
            if (CellsX > 1 || CellsY > 1) w.Name("cells").BeginArray().Value(CellsX).Value(CellsY).EndArray();
            w.Name("mean").Raw(N(Mean)).Name("contrast").Raw(N(Contrast)).Name("feature").Raw(N(FeatureSize))
                .Name("aniso").Raw(N(Anisotropy)).Name("coverage").Raw(N(Coverage)).Name("symmetry").Raw(N(Symmetry)).Name("ridge").Raw(N(Ridge));
            if (Kind == "linear") w.Name("axis").Value(Axis);
            if (Kind == "radial" || Kind == "linear")
            {
                if (Center != null) w.Name("center").Value(Center);
                w.Name("profile").BeginArray();
                foreach (var p in Profile) w.Raw(N(p));
                w.EndArray();
                if (Rays > 0) w.Name("rays").Value(Rays).Name("rayDepth").Raw(N(RayDepth));
            }
            w.EndObject();
        }

        // The mask of a decoded texture: BC4 / R8 -> red; otherwise alpha when it varies, else luminance.
        public static float[] Mask(byte[] rgba, int width, int height, uint format)
        {
            int layout = (int)(format >> 8);
            var m = new float[width * height];
            bool singleChannel = layout == 0x1D || layout == 0x02;
            bool alphaVaries = false;
            if (!singleChannel) for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] < 250) { alphaVaries = true; break; }
            for (int i = 0; i < m.Length; i++)
            {
                int p = i * 4;
                m[i] = singleChannel ? rgba[p] / 255f
                    : alphaVaries ? rgba[p + 3] / 255f
                    : (0.299f * rgba[p] + 0.587f * rgba[p + 1] + 0.114f * rgba[p + 2]) / 255f;
            }
            return m;
        }
    }
}
