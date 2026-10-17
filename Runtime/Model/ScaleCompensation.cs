using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace PokeModelExporter.Model
{
    // Segment scale compensation (Maya's, which the games' skeletons use: SV sets it on 58307 of 58314 joints) in a
    // format without it. With it, a joint's world transform is
    //     S_child R_child S_parent^-1 T_child W_parent
    // the parent's scale moves the child (its offset is scaled) but doesn't scale or skew it. glTF, Blender and
    // Unity always hand the whole parent transform down, so the clips' squash and stretch (Pikachu's spine at 0.8,
    // shoulders 0.9, arms 0.95, each keyed on its own joint) multiplied down the chains and skewed rotated children.
    //
    // Exact rebuild: between a scaled parent P and its compensating children goes a helper node "<P>_ssc" whose scale
    // is 1/S_P (so its world is P without the scale), and each child's translation becomes T_child x S_P (the offset
    // the parent's scale would have moved it by). Both follow P's scale per frame where a clip animates it.
    // Rest scales are kept, so inverse bind matrices computed from the node list stay right.
    public static class ScaleCompensation
    {
        const float Epsilon = 1e-4f;

        // Returns the number of helper nodes added. Call after the clips are loaded and the node list is final.
        public static int Apply(ModelData model)
        {
            var nodes = model.Nodes;
            var tracks = model.Animations.Select(a => a.Tracks.GroupBy(t => t.Bone).ToDictionary(g => g.Key, g => g.First())).ToList();

            bool Scaled(NodeData n) =>
                !IsOne(n.Scale) || tracks.Any(byBone => byBone.TryGetValue(n.Name ?? "", out var t) && t.Scale != null && t.Scale.Values.Any(v => !IsOne(v)));

            int added = 0, count = nodes.Count;
            for (int p = 0; p < count; p++)
            {
                var parent = nodes[p];
                var children = Enumerable.Range(0, count).Where(c => nodes[c].Parent == p && nodes[c].ScaleCompensate && !nodes[c].IsLocator).ToList();
                if (children.Count == 0 || !Scaled(parent)) continue;

                var helper = new NodeData { Name = parent.Name + "_ssc", Parent = p, Scale = Inverse(parent.Scale) };
                int h = nodes.Count;
                nodes.Add(helper);
                added++;
                foreach (int c in children)
                {
                    nodes[c].Parent = h;
                    nodes[c].Translation *= parent.Scale;
                }

                for (int a = 0; a < model.Animations.Count; a++)
                {
                    var anim = model.Animations[a];
                    var byBone = tracks[a];
                    byBone.TryGetValue(parent.Name ?? "", out var parentTrack);
                    var scale = parentTrack?.Scale;
                    if (scale == null || scale.Values.Length == 0)
                    {
                        // P keeps its rest scale in this clip: the children's keyed offsets get the rest scale.
                        foreach (int c in children)
                            if (byBone.TryGetValue(nodes[c].Name ?? "", out var ct) && ct.Translation != null)
                                for (int k = 0; k < ct.Translation.Values.Length; k++) ct.Translation.Values[k] *= parent.Scale;
                        continue;
                    }

                    // Every frame the parent's scale or a child's offset is keyed over, so the product and the
                    // inverse are right at each frame, not only at the parent's keys.
                    var frames = Frames(scale, anim);
                    var helperTrack = new BoneTrackData { Bone = helper.Name, Scale = new Keys<Vector3> { Times = frames, Values = frames.Select(f => Inverse(Sample(scale, f))).ToArray() } };
                    anim.Tracks.Add(helperTrack);
                    byBone[helper.Name] = helperTrack;
                    foreach (int c in children)
                    {
                        var child = nodes[c];
                        if (!byBone.TryGetValue(child.Name ?? "", out var ct))
                        {
                            ct = new BoneTrackData { Bone = child.Name };
                            anim.Tracks.Add(ct);
                            byBone[child.Name] = ct;
                        }
                        var own = ct.Translation;
                        var rest = child.Translation / parent.Scale; // unscaled rest offset (scaled above)
                        var times = own == null ? frames : frames.Concat(own.Times).Distinct().OrderBy(f => f).ToArray();
                        ct.Translation = new Keys<Vector3>
                        {
                            Times = times,
                            Values = times.Select(f => (own == null ? rest : Sample(own, f)) * Sample(scale, f)).ToArray(),
                        };
                    }
                }
            }
            return added;
        }

        static bool IsOne(Vector3 v) => Math.Abs(v.X - 1) < Epsilon && Math.Abs(v.Y - 1) < Epsilon && Math.Abs(v.Z - 1) < Epsilon;

        static Vector3 Inverse(Vector3 s) => new Vector3(Safe(s.X), Safe(s.Y), Safe(s.Z));
        static float Safe(float x) => Math.Abs(x) < 1e-6f ? 1e6f * Math.Sign(x == 0 ? 1 : x) : 1 / x;

        // The parent's key times plus every whole frame between its first and last key when it changes.
        static float[] Frames(Keys<Vector3> scale, AnimationData anim)
        {
            var times = new SortedSet<float>(scale.Times);
            bool constant = scale.Values.All(v => Vector3.DistanceSquared(v, scale.Values[0]) < Epsilon * Epsilon);
            if (!constant)
            {
                float first = scale.Times[0], last = Math.Max(scale.Times[scale.Times.Length - 1], anim.FrameCount - 1);
                for (float f = (float)Math.Ceiling(first); f <= last; f++) times.Add(f);
            }
            return times.ToArray();
        }

        // Linear between keys, held before the first and after the last (glTF's LINEAR sampling).
        static Vector3 Sample(Keys<Vector3> keys, float frame)
        {
            var t = keys.Times;
            if (frame <= t[0]) return keys.Values[0];
            if (frame >= t[t.Length - 1]) return keys.Values[t.Length - 1];
            int i = Array.BinarySearch(t, frame);
            if (i >= 0) return keys.Values[i];
            i = ~i;
            float u = (frame - t[i - 1]) / (t[i] - t[i - 1]);
            return Vector3.Lerp(keys.Values[i - 1], keys.Values[i], u);
        }
    }
}
