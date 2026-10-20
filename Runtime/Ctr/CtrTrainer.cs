using System;
using System.Collections.Generic;
using System.Numerics;
using PokeModelExporter.Export;
using PokeModelExporter.Model;

namespace PokeModelExporter.Ctr
{
    // Ultra Sun/Moon's player in battle (PokeSim puts the trainer's Z-Move dances on players' VRM avatars): the battle
    // body's skeleton (GARC a/1/7/4 entry 0 male p1_base / 1 female p2_base: a "CM" container whose sub-file 0 is the
    // model pack, a skeleton with a dummy mesh; 79 / 80 bones, rest pose an exact T-pose, centimetres) with scripted
    // motions (a/0/8/7 entries that battle scripts play on the trainer with command 161: "PC" containers of 18 motion
    // packs, sub-file 0 the male player's, 1 the female's) as one GLB of the skeleton and its clips, every frame baked.
    // Rotation keys are read per bone by its track's flag bit 31 (GfMotion.BoneTrack.Euler): clear, the three values are
    // an axis times half the angle; set, Euler angles in radians applied X first, then Y, then Z.
    public static class CtrTrainer
    {
        // A "PC" / "CM" container's sub-files (u16 count at 2, count + 1 u32 offsets from 4), or null.
        public static List<byte[]> Container(byte[] d)
        {
            if (d == null || d.Length < 8 || !((d[0] == 'P' && d[1] == 'C') || (d[0] == 'C' && d[1] == 'M'))) return null;
            int n = BitConverter.ToUInt16(d, 2);
            var list = new List<byte[]>(n);
            for (int i = 0; i < n; i++)
            {
                int a = BitConverter.ToInt32(d, 4 + 4 * i), b = BitConverter.ToInt32(d, 8 + 4 * i);
                var sub = new byte[Math.Max(0, b - a)];
                Array.Copy(d, a, sub, 0, sub.Length);
                list.Add(Lz11.Maybe(sub));
            }
            return list;
        }

        // The skeleton (body: a/1/7/4's entry) and the motions (name, a/0/8/7 entry's bytes), sub = 0 male, 1 female.
        // Null without a skeleton; motions that aren't there are left out.
        public static byte[] Export(byte[] body, IEnumerable<(string name, byte[] data)> motions, int sub, out List<string> clips)
        {
            clips = new List<string>();
            var parts = Container(body);
            var packData = parts != null && parts.Count > 0 ? parts[0] : body;
            if (!GfModelPack.IsPack(packData)) return null;
            var pack = GfModelPack.Read(packData);
            if (pack.Models.Count == 0) return null;
            var gf = pack.Models[0];
            var model = GfModelConvert.ToModel(gf, pack, out _);
            model.Meshes.Clear(); // the dummy mesh: the skeleton is all we need
            model.Materials.Clear();
            var rest = new Dictionary<string, GfModelPack.Bone>();
            foreach (var b in gf.Bones) rest[b.Name] = b;
            foreach (var (name, data) in motions)
            {
                var subs = Container(data);
                var bytes = subs != null ? (sub < subs.Count ? subs[sub] : null) : data;
                if (bytes == null || !GfMotion.IsMotion(bytes)) continue;
                GfMotion motion;
                try { motion = GfMotion.Read(bytes); }
                catch (Exception) { continue; }
                model.Animations.Add(Bake(name, motion, rest));
                clips.Add(name);
            }
            return GlbWriter.Write(model, new Dictionary<string, byte[]>(), m => null, null);
        }

        static AnimationData Bake(string name, GfMotion motion, Dictionary<string, GfModelPack.Bone> rest)
        {
            int frames = Math.Max(1, motion.FrameCount + 1);
            var anim = new AnimationData { Name = name, FramesPerSecond = 30, FrameCount = frames, Loop = motion.Loop };
            foreach (var track in motion.Bones)
            {
                if (!rest.TryGetValue(track.Bone, out var bone)) continue;
                var times = new float[frames];
                var t = new Vector3[frames];
                var r = new Quaternion[frames];
                var s = new Vector3[frames];
                var restQ = EulerZFirst(bone.Rotation);
                var restHalf = HalfAxis(restQ);
                bool rotates = track.Channels[3]?.Animated == true || track.Channels[4]?.Animated == true || track.Channels[5]?.Animated == true;
                for (int f = 0; f < frames; f++)
                {
                    times[f] = f;
                    s[f] = new Vector3(Eval(track.Channels[0], f, bone.Scale.X), Eval(track.Channels[1], f, bone.Scale.Y), Eval(track.Channels[2], f, bone.Scale.Z));
                    t[f] = new Vector3(Eval(track.Channels[6], f, bone.Translation.X), Eval(track.Channels[7], f, bone.Translation.Y), Eval(track.Channels[8], f, bone.Translation.Z)) * GfModelConvert.Scale;
                    if (!rotates) { r[f] = restQ; continue; }
                    if (track.Euler)
                        r[f] = EulerXFirst(new Vector3(Eval(track.Channels[3], f, bone.Rotation.X), Eval(track.Channels[4], f, bone.Rotation.Y), Eval(track.Channels[5], f, bone.Rotation.Z)));
                    else
                        r[f] = FromHalfAxis(new Vector3(Eval(track.Channels[3], f, restHalf.X), Eval(track.Channels[4], f, restHalf.Y), Eval(track.Channels[5], f, restHalf.Z)));
                    if (f > 0 && Quaternion.Dot(r[f], r[f - 1]) < 0) r[f] = -r[f]; // one hemisphere: no flips between frames
                }
                anim.Tracks.Add(new BoneTrackData
                {
                    Bone = track.Bone,
                    Translation = new Keys<Vector3> { Times = times, Values = t },
                    Rotation = new Keys<Quaternion> { Times = (float[])times.Clone(), Values = r },
                    Scale = new Keys<Vector3> { Times = (float[])times.Clone(), Values = s },
                });
            }
            return anim;
        }

        static float Eval(GfMotion.Curve c, float frame, float fallback)
        {
            if (c == null || !c.Animated) return fallback;
            if (c.Frames == null) return c.Constant;
            var f = c.Frames; var v = c.Values; var sl = c.Slopes;
            int n = f.Length;
            if (n == 1 || frame <= f[0]) return v[0];
            if (frame >= f[n - 1]) return v[n - 1];
            int i = 0;
            while (i < n - 2 && frame > f[i + 1]) i++;
            float d = f[i + 1] - f[i], u = d > 0 ? (frame - f[i]) / d : 0, u2 = u * u, u3 = u2 * u;
            return (2 * u3 - 3 * u2 + 1) * v[i] + (u3 - 2 * u2 + u) * d * sl[i] + (-2 * u3 + 3 * u2) * v[i + 1] + (u3 - u2) * d * sl[i + 1];
        }

        // The bind pose's Euler angles (Z applied first, as GfModelConvert).
        static Quaternion EulerZFirst(Vector3 e) =>
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, e.X) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, e.Y) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, e.Z);

        // A flagged track's Euler angles: X applied first, then Y, then Z.
        static Quaternion EulerXFirst(Vector3 e) =>
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, e.Z) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, e.Y) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, e.X);

        // axis x half the angle <-> quaternion (whose vector part is axis x sin(half)).
        static Quaternion FromHalfAxis(Vector3 v)
        {
            float h = v.Length();
            if (h < 1e-7f) return Quaternion.Identity;
            var axis = v / h;
            return new Quaternion(axis * MathF.Sin(h), MathF.Cos(h));
        }

        static Vector3 HalfAxis(Quaternion q)
        {
            if (q.W < 0) q = -q;
            var v = new Vector3(q.X, q.Y, q.Z);
            float s = v.Length();
            if (s < 1e-7f) return Vector3.Zero;
            float half = MathF.Atan2(s, q.W);
            return v / s * half;
        }
    }
}
