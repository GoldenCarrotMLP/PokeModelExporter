using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using PokeModelExporter.Formats;
using PokeModelExporter.Model;

namespace PokeModelExporter.Scarlet
{
    // Reads Trinity skeletal clips (.tranm, schema pkZukan/PokeDocs SV/Flatbuffers/animation/tranm.fbs):
    //   TRANM { info: { does_loop, animation_count (frames), animation_rate (fps) }, track: { tracks: [BoneTrack] } }
    //   BoneTrack { bone_name, scale: VectorTrack, rotate: RotationTrack, translate: VectorTrack }
    // Each track is a union (FlatBuffers: a type byte field followed by the value field) of
    // Fixed (one value), Dynamic (one per frame), Framed16 / Framed8 (keyframes at listed frames).
    // Rotations are 48-bit "smallest three" quaternions; values are local, like the skeleton's.
    public static class TrinityAnimationReader
    {
        public static AnimationData Read(byte[] data, string name)
        {
            var root = FbTable.Root(data);
            var info = root.Table(0);
            var anim = new AnimationData
            {
                Name = name,
                Loop = info.U32(0) != 0,
                FrameCount = (int)info.U32(1),
                FramesPerSecond = info.U32(2) > 0 ? info.U32(2) : 30,
            };
            var tracks = root.Table(1).Vector(0);
            for (int i = 0; i < tracks.Length; i++)
            {
                var t = tracks.Table(i);
                anim.Tracks.Add(new BoneTrackData
                {
                    Bone = t.String(0),
                    Scale = VectorTrack(t, 1),
                    Rotation = RotationTrack(t, 3),
                    Translation = VectorTrack(t, 5),
                });
            }
            return anim;
        }

        // Union at field typeField (type) / typeField + 1 (value). Types: 1 Fixed, 2 Dynamic, 3 Framed16, 4 Framed8.
        static Keys<Vector3> VectorTrack(FbTable track, int typeField)
        {
            int type = track.U8(typeField);
            if (type == 0) return null;
            var t = track.Table(typeField + 1);
            if (t.IsNull) return null;
            if (type == 1) return Single(Vec3At(t.Buffer, t.InlineStruct(0)));
            var values = t.Vector(type == 2 ? 0 : 1);
            var result = new Keys<Vector3> { Values = new Vector3[values.Length] };
            for (int i = 0; i < values.Length; i++) result.Values[i] = Vec3At(t.Buffer, values.StructAt(i, 12));
            result.Times = Times(t, type, values.Length);
            return result;
        }

        static Keys<Quaternion> RotationTrack(FbTable track, int typeField)
        {
            int type = track.U8(typeField);
            if (type == 0) return null;
            var t = track.Table(typeField + 1);
            if (t.IsNull) return null;
            if (type == 1)
            {
                int p = t.InlineStruct(0);
                return new Keys<Quaternion> { Times = new[] { 0f }, Values = new[] { UnpackAt(t.Buffer, p) } };
            }
            var values = t.Vector(type == 2 ? 0 : 1);
            var result = new Keys<Quaternion> { Values = new Quaternion[values.Length] };
            for (int i = 0; i < values.Length; i++) result.Values[i] = UnpackAt(t.Buffer, values.StructAt(i, 6));
            result.Times = Times(t, type, values.Length);
            return result;
        }

        static float[] Times(FbTable t, int type, int count)
        {
            var times = new float[count];
            if (type == 2)
            {
                for (int i = 0; i < count; i++) times[i] = i;
                return times;
            }
            var frames = t.Vector(0);
            for (int i = 0; i < count && i < frames.Length; i++)
                times[i] = type == 3 ? frames.U16(i) : frames.U8(i);
            return times;
        }

        static Keys<Vector3> Single(Vector3 v) => new Keys<Vector3> { Times = new[] { 0f }, Values = new[] { v } };

        static Vector3 Vec3At(byte[] b, int p) =>
            p == 0 ? Vector3.Zero : new Vector3(BitConverter.ToSingle(b, p), BitConverter.ToSingle(b, p + 4), BitConverter.ToSingle(b, p + 8));

        static Quaternion UnpackAt(byte[] b, int p) =>
            p == 0 ? Quaternion.Identity : Unpack(BitConverter.ToUInt16(b, p), BitConverter.ToUInt16(b, p + 2), BitConverter.ToUInt16(b, p + 4));

        // 48 bits: [1:0] index of the omitted (largest) component, [2] negate, then three 15-bit
        // components at bits 3, 18 and 33, each mapping 0..0x7FFF to -pi/4..pi/4. The omitted
        // component is rebuilt from unit length. Component order x, y, z, w.
        public static Quaternion Unpack(ushort x, ushort y, ushort z)
        {
            ulong pack = ((ulong)z << 32) | ((ulong)y << 16) | x;
            float Expand(ulong v) => v * ((float)Math.PI / 2f / 0x7FFF) - (float)Math.PI / 4f;
            float a = Expand((pack >> 3) & 0x7FFF), b = Expand((pack >> 18) & 0x7FFF), c = Expand((pack >> 33) & 0x7FFF);
            float largest = (float)Math.Sqrt(Math.Max(0, 1 - (a * a + b * b + c * c)));
            var v = new List<float> { a, b, c };
            v.Insert((int)(pack & 3), largest);
            var q = new Quaternion(v[0], v[1], v[2], v[3]);
            return (pack & 4) != 0 ? Quaternion.Negate(q) : q;
        }

        // Animation notifies (.traef, schema PokeDocs SV/Flatbuffers/animation/traef.fbs):
        //   TRAEF { effects: [AnimationEffect { res_0, path (event type), start, duration (frames),
        //           effects: [Effect { name, value: union Int | Float | Byte | String }] }] }
        public static List<AnimationEvent> ReadEvents(byte[] data)
        {
            var events = new List<AnimationEvent>();
            var list = FbTable.Root(data).Vector(0);
            for (int i = 0; i < list.Length; i++)
            {
                var e = list.Table(i);
                var ev = new AnimationEvent { Type = e.String(1), StartFrame = (int)e.U32(2), DurationFrames = (int)e.U32(3) };
                var parameters = e.Vector(4);
                for (int k = 0; k < parameters.Length; k++)
                {
                    var p = parameters.Table(k);
                    string name = p.String(0);
                    if (name == null) continue;
                    int type = p.U8(1);
                    var v = p.Table(2);
                    ev.Parameters[name] = v.IsNull ? "" : type switch
                    {
                        1 => v.U32(0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        2 => v.F32(0).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        3 => v.U8(0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        4 => v.String(0) ?? "",
                        _ => "",
                    };
                }
                events.Add(ev);
            }
            return events;
        }

        // Clip file names listed in an animation resource file (.tracr). It has no published schema;
        // the names are stored as plain strings, so collect every "*.tranm".
        public static List<string> ClipNamesFromResource(byte[] tracr)
        {
            var names = new List<string>();
            int start = -1;
            for (int i = 0; i <= tracr.Length; i++)
            {
                bool printable = i < tracr.Length && tracr[i] >= 0x20 && tracr[i] < 0x7F;
                if (printable) { if (start < 0) start = i; continue; }
                if (start >= 0 && i - start > 6)
                {
                    string s = Encoding.ASCII.GetString(tracr, start, i - start);
                    if (s.EndsWith(".tranm", StringComparison.Ordinal) && !names.Contains(s)) names.Add(s);
                }
                start = -1;
            }
            names.Sort(StringComparer.Ordinal);
            return names;
        }
    }
}
