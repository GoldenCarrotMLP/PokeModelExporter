using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using PokeModelExporter.Export;
using PokeModelExporter.Formats;

namespace PokeModelExporter.Scarlet
{
    // Describes a particle primitive's shape with a few numbers, so an engine can build its own mesh instead of
    // using the game's. Effect primitives are mostly rings, cylinders, cones, domes, spheres and planes:
    //   revolution: a profile curve (height along the axis, radius) turned around X, Y or Z over a sweep;
    //               u or v runs around (angle) and the other along the curve, each as offset + scale
    //   plane:      a flat quad (the two largest extents of the bounds) with UVs as a linear map of position
    // Anything else (rocks, leaves, fists) is described as a plane through its bounds: a flat stand-in.
    public sealed class FxPrimitiveShape
    {
        public string Kind; // revolution / plane
        public int Axis;    // 0 X, 1 Y, 2 Z
        public float[][] Profile; // revolution: [height, radius] along the curve
        public float SweepStart, Sweep = 360; // degrees
        public bool UAround;      // u runs around (else v)
        public float SeamStart; public int SeamDirection = 1; // around t = (direction * (angle - seam)) mod 360 / sweep
        public float[] AroundMap = { 0, 1 }, AlongMap = { 0, 1 }; // uv = offset + scale * t (t 0..1)
        public float Score;
        public Vector3 Min, Max;
        public float[] UMap, VMap; // plane: uv = dot(map.xyz, p) + map.w
        public string Source;      // the particle file (archive path) holding it, for engines that read the game's own

        const int Bins = 12, Sectors = 36;

        public static FxPrimitiveShape Analyze(BfresPrimitives.Primitive prim)
        {
            var p = prim.Positions;
            var uv = prim.Uv0 ?? new Vector2[p.Length];
            var s = new FxPrimitiveShape { Min = new Vector3(float.MaxValue), Max = new Vector3(float.MinValue) };
            foreach (var v in p) { s.Min = Vector3.Min(s.Min, v); s.Max = Vector3.Max(s.Max, v); }
            FxPrimitiveShape best = null;
            for (int axis = 0; axis < 3; axis++)
            {
                var r = Revolution(p, uv, axis);
                if (r != null && (best == null || r.Score > best.Score)) best = r;
            }
            if (best != null && best.Score > 0.75f)
            {
                best.Min = s.Min; best.Max = s.Max;
                return best;
            }
            return Plane(p, uv, s);
        }

        static float H(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
        static (float a, float b) Cross(Vector3 v, int axis) => axis == 0 ? (v.Y, v.Z) : axis == 1 ? (v.Z, v.X) : (v.X, v.Y);

        static FxPrimitiveShape Revolution(Vector3[] p, Vector2[] uv, int axis)
        {
            int n = p.Length;
            if (n < 6) return null;
            var h = new float[n]; var r = new float[n]; var angle = new float[n];
            float maxR = 0;
            for (int i = 0; i < n; i++)
            {
                var (a, b) = Cross(p[i], axis);
                h[i] = H(p[i], axis);
                r[i] = (float)Math.Sqrt(a * a + b * b);
                angle[i] = (float)((Math.Atan2(b, a) * 180 / Math.PI + 360) % 360);
                maxR = Math.Max(maxR, r[i]);
            }
            if (maxR < 1e-4f) return null;

            // The meridian: (h, r) points ordered along their main direction, averaged into bins.
            float mh = h.Average(), mr = r.Average();
            double shh = 0, srr = 0, shr = 0;
            for (int i = 0; i < n; i++) { shh += (h[i] - mh) * (h[i] - mh); srr += (r[i] - mr) * (r[i] - mr); shr += (h[i] - mh) * (r[i] - mr); }
            double theta = 0.5 * Math.Atan2(2 * shr, shh - srr);
            float dh = (float)Math.Cos(theta), dr = (float)Math.Sin(theta);
            var t = new float[n];
            for (int i = 0; i < n; i++) t[i] = (h[i] - mh) * dh + (r[i] - mr) * dr;
            float t0 = t.Min(), t1 = t.Max();
            var sumH = new double[Bins]; var sumR = new double[Bins]; var count = new int[Bins];
            var sectorsBy = new HashSet<int>[Bins];
            for (int b = 0; b < Bins; b++) sectorsBy[b] = new HashSet<int>();
            var sectors = new bool[Sectors];
            for (int i = 0; i < n; i++)
            {
                int b = t1 > t0 ? Math.Min(Bins - 1, (int)((t[i] - t0) / (t1 - t0) * Bins)) : 0;
                sumH[b] += h[i]; sumR[b] += r[i]; count[b]++;
                if (r[i] > maxR * 0.05f) { int sec = Math.Min(Sectors - 1, (int)(angle[i] / 360 * Sectors)); sectors[sec] = true; sectorsBy[b].Add(sec); }
            }
            var profile = new List<float[]>();
            for (int b = 0; b < Bins; b++) if (count[b] > 0) profile.Add(new[] { (float)(sumH[b] / count[b]), (float)(sumR[b] / count[b]) });
            if (profile.Count < 2) return null;

            // How well the profile explains the points: distance of each point's (h, r) to the polyline.
            double error = 0;
            float scale = Math.Max(maxR, Math.Max(h.Max() - h.Min(), 1e-3f));
            for (int i = 0; i < n; i++) error += DistanceToPolyline(h[i], r[i], profile) / scale;
            error /= n;
            // Round shapes cover every direction; arcs cover a contiguous part.
            int covered = sectors.Count(x => x);
            var (sweepStart, sweep) = SweepOf(sectors);
            float roundness = covered / (float)Math.Max(1, (int)Math.Round(sweep / 360 * Sectors));
            float score = (float)Math.Max(0, 1 - error * 8) * roundness * (sweep >= 90 ? 1 : 0.5f);

            var shape = new FxPrimitiveShape { Kind = "revolution", Axis = axis, Profile = profile.ToArray(), SweepStart = sweepStart, Sweep = sweep, Score = score };
            // UVs: which coordinate goes around, and how each maps from 0..1.
            var around = new float[n]; var along = new float[n];
            for (int i = 0; i < n; i++)
            {
                around[i] = ((angle[i] - sweepStart + 360) % 360) / Math.Max(1, sweep);
                along[i] = t1 > t0 ? (t[i] - t0) / (t1 - t0) : 0;
            }
            var u = uv.Select(x => x.X).ToArray(); var v = uv.Select(x => x.Y).ToArray();
            // The UV seam can be at any angle and run either way: try each sector boundary and direction.
            float bestCorrelation = -1; float[] bestAround = around; bool bestU = true;
            foreach (bool useU in new[] { true, false })
                for (int seam = 0; seam < Sectors; seam++)
                    foreach (int dir in new[] { 1, -1 })
                    {
                        float start = sweepStart + seam * Math.Max(1, sweep) / Sectors;
                        var a = new float[n];
                        for (int i = 0; i < n; i++) a[i] = ((dir * (angle[i] - start) % 360 + 720) % 360) / Math.Max(1, sweep);
                        float c = Math.Abs(Correlation(a, useU ? u : v)) + Math.Abs(Correlation(along, useU ? v : u));
                        if (c > bestCorrelation) { bestCorrelation = c; bestAround = a; bestU = useU; shape.SeamStart = start; shape.SeamDirection = dir; }
                    }
            shape.UAround = bestU;
            shape.AroundMap = Fit(bestAround, bestU ? u : v);
            shape.AlongMap = Fit(along, bestU ? v : u);
            return shape;
        }

        static (float start, float sweep) SweepOf(bool[] sectors)
        {
            int n = sectors.Length, covered = sectors.Count(x => x);
            if (covered >= n - 1) return (0, 360);
            // The longest empty gap (circularly) is outside the sweep.
            int bestGap = 0, gapEnd = 0;
            for (int i = 0; i < n; i++)
            {
                if (sectors[i]) continue;
                int len = 0;
                while (len < n && !sectors[(i + len) % n]) len++;
                if (len > bestGap) { bestGap = len; gapEnd = (i + len) % n; }
            }
            return (gapEnd * 360f / n, (n - bestGap) * 360f / n);
        }

        static float DistanceToPolyline(float x, float y, List<float[]> line)
        {
            double best = double.MaxValue;
            for (int i = 0; i + 1 < line.Count; i++)
            {
                double ax = line[i][0], ay = line[i][1], bx = line[i + 1][0], by = line[i + 1][1];
                double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
                double k = len2 > 0 ? Math.Max(0, Math.Min(1, ((x - ax) * dx + (y - ay) * dy) / len2)) : 0;
                double ex = ax + k * dx - x, ey = ay + k * dy - y;
                best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
            }
            return (float)best;
        }

        static float Correlation(float[] a, float[] b)
        {
            double ma = a.Average(), mb = b.Average(), sab = 0, saa = 0, sbb = 0;
            for (int i = 0; i < a.Length; i++) { sab += (a[i] - ma) * (b[i] - mb); saa += (a[i] - ma) * (a[i] - ma); sbb += (b[i] - mb) * (b[i] - mb); }
            return saa < 1e-9 || sbb < 1e-9 ? 0 : (float)(sab / Math.Sqrt(saa * sbb));
        }

        // Least squares y = offset + scale * x.
        static float[] Fit(float[] x, float[] y)
        {
            double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0;
            for (int i = 0; i < x.Length; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); }
            double scale = sxx > 1e-9 ? sxy / sxx : 0;
            return new[] { (float)(my - scale * mx), (float)scale };
        }

        // A flat quad in the plane of the two largest extents, UVs as the best linear map of position.
        static FxPrimitiveShape Plane(Vector3[] p, Vector2[] uv, FxPrimitiveShape s)
        {
            s.Kind = "plane";
            s.UMap = LinearMap(p, uv.Select(x => x.X).ToArray());
            s.VMap = LinearMap(p, uv.Select(x => x.Y).ToArray());
            return s;
        }

        // y = a.x * X + a.y * Y + a.z * Z + w by least squares (normal equations, 4x4).
        static float[] LinearMap(Vector3[] p, float[] y)
        {
            var m = new double[4, 5];
            for (int i = 0; i < p.Length; i++)
            {
                var row = new double[] { p[i].X, p[i].Y, p[i].Z, 1 };
                for (int a = 0; a < 4; a++) { for (int b = 0; b < 4; b++) m[a, b] += row[a] * row[b]; m[a, 4] += row[a] * y[i]; }
            }
            for (int a = 0; a < 4; a++) m[a, a] += 1e-6; // flat meshes: keep the system solvable
            for (int c = 0; c < 4; c++)
            {
                int pivot = c;
                for (int rr = c + 1; rr < 4; rr++) if (Math.Abs(m[rr, c]) > Math.Abs(m[pivot, c])) pivot = rr;
                for (int k = 0; k < 5; k++) { var tmp = m[c, k]; m[c, k] = m[pivot, k]; m[pivot, k] = tmp; }
                for (int rr = 0; rr < 4; rr++)
                {
                    if (rr == c || Math.Abs(m[c, c]) < 1e-12) continue;
                    double f = m[rr, c] / m[c, c];
                    for (int k = 0; k < 5; k++) m[rr, k] -= f * m[c, k];
                }
            }
            var x = new float[4];
            for (int a = 0; a < 4; a++) x[a] = Math.Abs(m[a, a]) < 1e-12 ? 0 : (float)(m[a, 4] / m[a, a]);
            return x;
        }

        public void Write(JsonWriter w)
        {
            string N(float f) => Math.Round(f, 4).ToString("0.####", CultureInfo.InvariantCulture);
            w.BeginObject().Name("kind").Value(Kind);
            if (Source != null) w.Name("source").Value(Source);
            w.Name("min").BeginArray().Raw(N(Min.X)).Raw(N(Min.Y)).Raw(N(Min.Z)).EndArray();
            w.Name("max").BeginArray().Raw(N(Max.X)).Raw(N(Max.Y)).Raw(N(Max.Z)).EndArray();
            if (Kind == "revolution")
            {
                w.Name("axis").Value(Axis).Name("score").Raw(N(Score)).Name("sweepStart").Raw(N(SweepStart)).Name("sweep").Raw(N(Sweep));
                w.Name("profile").BeginArray();
                foreach (var pt in Profile) w.BeginArray().Raw(N(pt[0])).Raw(N(pt[1])).EndArray();
                w.EndArray();
                w.Name("uAround").Value(UAround).Name("seam").Raw(N(SeamStart)).Name("seamDirection").Value(SeamDirection);
                w.Name("aroundMap").BeginArray().Raw(N(AroundMap[0])).Raw(N(AroundMap[1])).EndArray();
                w.Name("alongMap").BeginArray().Raw(N(AlongMap[0])).Raw(N(AlongMap[1])).EndArray();
            }
            else
            {
                w.Name("uMap").BeginArray(); foreach (var f in UMap) w.Raw(N(f)); w.EndArray();
                w.Name("vMap").BeginArray(); foreach (var f in VMap) w.Raw(N(f)); w.EndArray();
            }
            w.EndObject();
        }
    }
}
