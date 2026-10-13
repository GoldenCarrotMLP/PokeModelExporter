using System;
using System.Collections.Generic;
using PokeModelExporter.Formats;
using PokeModelExporter.Model;

namespace PokeModelExporter.Scarlet
{
    // Reads a clip's non-skeletal tracks (.tracm, schema PokeDocs SV/Flatbuffers/animation/tracm.fbs):
    //   TRACM { config { _, duration, framerate }, tracks: [Track], material/visibility/blendshape frame multipliers }
    //   Track { track_path (mesh shape name), _, _, _, material_animation, visibility_animation, blendshape_animation }
    // Material: per material, per parameter, per channel lists of (time, value).
    // Visibility: a bool union (Fixed / Dynamic / Framed16 / Framed8) per mesh. The bools of keyed tracks
    // are bit-packed, 8 per byte (regardless of the header byte, which varies): key i = bit i % 8 of byte i / 8.
    // Braixen's special attack: tail hair [0, 22, 140] = 0b101 (hidden while the stick is out),
    // flame [0, 21, 140] = 0b010 (lit while it's raised).
    // Blend shapes: per shape, a float union (Fixed / Dynamic / Framed16 / Framed8).
    public static class TrinityCurveReader
    {
        public static void Read(byte[] data, AnimationData anim)
        {
            var root = FbTable.Root(data);
            int visibilityMultiplier = root.U8(3, 1);
            var tracks = root.Vector(1);
            for (int i = 0; i < tracks.Length; i++)
            {
                var track = tracks.Table(i);
                string mesh = track.String(0);
                ReadMaterial(track.Table(4), mesh, anim);
                ReadVisibility(track.Table(5), mesh, visibilityMultiplier, anim);
                ReadBlendShapes(track.Table(6), mesh, anim);
            }
        }

        static void ReadMaterial(FbTable timeline, string mesh, AnimationData anim)
        {
            if (timeline.IsNull) return;
            var materials = timeline.Vector(2);
            for (int m = 0; m < materials.Length; m++)
            {
                var mat = materials.Table(m);
                var animated = mat.Vector(2);
                for (int a = 0; a < animated.Length; a++)
                {
                    var p = animated.Table(a);
                    var ch = p.Table(1);
                    if (ch.IsNull) continue;
                    anim.MaterialTracks.Add(new MaterialTrackData
                    {
                        Mesh = mesh,
                        Material = mat.String(0),
                        Parameter = p.String(0),
                        R = Values(ch.Table(0)), G = Values(ch.Table(1)), B = Values(ch.Table(2)), A = Values(ch.Table(3)),
                    });
                }
            }
        }

        // TrackMaterialValueList { values: [TrackMaterialValue { time, value, ... }] }
        static Keys<float> Values(FbTable list)
        {
            if (list.IsNull) return null;
            var v = list.Vector(0);
            if (v.IsNull || v.Length == 0) return null;
            var keys = new Keys<float> { Times = new float[v.Length], Values = new float[v.Length] };
            for (int i = 0; i < v.Length; i++)
            {
                var e = v.Table(i);
                keys.Times[i] = e.F32(0);
                keys.Values[i] = e.F32(1);
            }
            return keys;
        }

        // VisibilityShapeTimeline { time, value, info: TrackFlagsInfo { values: union TrackFlag } }
        static void ReadVisibility(FbTable timeline, string mesh, int multiplier, AnimationData anim)
        {
            if (timeline.IsNull) return;
            var info = timeline.Table(2);
            if (info.IsNull) return;
            int type = info.U8(0);
            var t = info.Table(1);
            var track = new VisibilityTrackData { Mesh = mesh };
            if (type == 1) // Fixed { value: bool } (absent = false)
            {
                track.Fixed = !t.IsNull && t.Bool(0);
            }
            else if (type >= 2 && type <= 4) // Dynamic { value } / Framed16, Framed8 { frames, value }
            {
                // Values are always bit-packed, 8 per byte, low bit first (the header byte isn't a bit
                // count: Braixen's clips have 8 there, Decidueye's 12, both packed the same way).
                var packed = Bytes(t.Vector(type == 2 ? 0 : 1));
                var frames = type == 2 ? default : t.Vector(0);
                int count = type == 2 ? Math.Min(packed.Length * 8, Math.Max(anim.FrameCount, 1)) : (frames.IsNull ? 0 : frames.Length);
                track.Keys = new Keys<bool> { Times = new float[count], Values = new bool[count] };
                for (int i = 0; i < count; i++)
                {
                    track.Keys.Times[i] = type == 2 ? i : type == 3 ? frames.U16(i) : frames.U8(i);
                    track.Keys.Values[i] = i / 8 < packed.Length && ((packed[i / 8] >> (i % 8)) & 1) != 0;
                }
            }
            else return;
            anim.VisibilityTracks.Add(track);
        }

        // BlendShapeTimeline { config, _, res_2: [TrackBlendShape { name, init_values, track: union BlendTrack, _ }], _, blend_list }
        static void ReadBlendShapes(FbTable timeline, string mesh, AnimationData anim)
        {
            if (timeline.IsNull) return;
            var shapes = timeline.Vector(2);
            for (int s = 0; s < shapes.Length; s++)
            {
                var shape = shapes.Table(s);
                int type = shape.U8(2);
                var t = shape.Table(3);
                if (t.IsNull) continue;
                Keys<float> keys;
                if (type == 1) keys = new Keys<float> { Times = new[] { 0f }, Values = new[] { t.F32(0) } };
                else
                {
                    var values = t.Vector(type == 2 ? 0 : 1);
                    keys = new Keys<float> { Times = new float[values.Length], Values = new float[values.Length] };
                    var frames = type == 2 ? default : t.Vector(0);
                    for (int i = 0; i < values.Length; i++)
                    {
                        keys.Values[i] = values.F32(i);
                        keys.Times[i] = type == 2 ? i : type == 3 ? frames.U16(i) : frames.U8(i);
                    }
                }
                anim.MorphTracks.Add(new MorphTrackData { Mesh = mesh, Shape = shape.String(0), Weight = keys });
            }
        }

        static byte[] Bytes(FbVector v)
        {
            if (v.IsNull) return new byte[0];
            var b = new byte[v.Length];
            for (int i = 0; i < v.Length; i++) b[i] = v.U8(i);
            return b;
        }
    }
}
