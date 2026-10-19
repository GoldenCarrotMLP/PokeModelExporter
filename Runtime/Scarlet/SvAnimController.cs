using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PokeModelExporter.Formats;

namespace PokeModelExporter.Scarlet
{
    // A Pokemon's animation controller beyond the clips themselves: `<model>_base.tracs` (the state machines, PokeDocs
    // animation/tracs.fbs) and `<model>_base.tralk` (the look-at, tralk.fbs). Read by field index, no schema at runtime.
    //   Face layers: every state machine but the body's (eye01, mouth01) runs over one facial clip, chosen by an int
    //   parameter (eye_int / mouth_int): 0 = the body clip's own face ("default", no clip), then named poses (open,
    //   halfclose, close) that each play a segment of the clip and hold at its end ("<pose>_stop", speed 0). The clip
    //   goes open -> closed -> open, so a pose is one normalized time of it (open 0, half 0.2, closed 0.5 on Pikachu). A
    //   blink (eye_blink01_trigger) runs its segments faster (2.5x shut, 1.5x open) and returns to the pose. Body clips
    //   switch a layer off with their SetBoolParameterRange <layer>_enabled events while they pose the face themselves.
    //   Body state flags: blink_disable and lookat_disable on the states that own the head (attacks, notices...).
    //   Look-at: the bone it turns (head), the angle it allows and whether it also slides the eye UV (pupils).
    public sealed class SvAnimController
    {
        public sealed class FacePose { public string Name; public int Value; public float Time; } // Time: normalized clip time held
        public sealed class FaceSegment { public float From, To, Speed; }
        public sealed class FaceLayer
        {
            public string Name, Clip, Parameter, Trigger;
            public readonly List<FacePose> Poses = new List<FacePose>();
            public readonly List<FaceSegment> Blink = new List<FaceSegment>();
        }
        // A bone of the look-at chain (root first: Charizard neck_01, neck_02, neck_03, head) with its own limits, degrees:
        // yaw toward the model's left (YawLeft) and right, pitch up and down, and the smoothing time (0.3 s).
        public sealed class LookAtBone { public string Name; public float YawLeft, YawRight, Up, Down, Smooth = 0.3f; }
        // An eye: the game slides its material's UVScaleOffset (the pupil) by the look the head didn't make. Limits per eye
        // (the inward one smaller), the eye's position on the bone, and Map: three UV displacements (u, v, 0) reached at
        // the left limit, the up limit and the right limit.
        public sealed class LookAtEye
        {
            public string Bone, Mesh, Material;
            public float YawLeft, YawRight, Up, Down, Smooth = 0.05f;
            public float[] Offset = new float[3], Map = new float[9];
            public List<string> Tracks = new List<string>();
        }
        public sealed class LookAtInfo
        {
            public string Bone;                  // the chain's root bone (Bones[0])
            public float MaxAngle, Speed, Weight; // the file's fade angle (45; 0 in some files), the first bone's smoothing
            public bool EyeUv;
            public readonly List<LookAtBone> Bones = new List<LookAtBone>();
            public readonly List<LookAtEye> Eyes = new List<LookAtEye>();
        }

        public readonly List<FaceLayer> Faces = new List<FaceLayer>();
        // Body clip (00550_glad01) -> the flags of the states that play it (blink_disable, lookat_disable, priority_pass...).
        public readonly Dictionary<string, List<string>> ClipFlags = new Dictionary<string, List<string>>();
        public LookAtInfo LookAt;

        public static SvAnimController Read(string modelName, Func<string, byte[]> read)
        {
            var c = new SvAnimController();
            // Each file on its own: an unexpected layout (another game's version) loses that part, not the rest.
            var tracs = read(modelName + "_base.tracs");
            if (tracs != null) try { c.ReadStates(tracs); } catch (Exception) { c.Faces.Clear(); c.ClipFlags.Clear(); }
            var tralk = read(modelName + "_base.tralk");
            if (tralk != null) try { c.ReadLookAt(tralk); } catch (Exception) { c.LookAt = null; }
            return c;
        }

        // ---- tracs ----

        sealed class Transition
        {
            public string Path;
            public float ExitTime, Duration, Offset;
            public readonly List<(string name, uint func, double value)> Conditions = new List<(string, uint, double)>();
        }

        sealed class State
        {
            public string Path, Clip;
            public uint Type; // 0 normal, 1 entry, 2 exit, 3 any, 4 top
            public float Speed;
            public readonly List<string> Flags = new List<string>();
            public readonly List<Transition> Transitions = new List<Transition>();
            public readonly List<State> Children = new List<State>();
            public string Leaf => Path.Substring(Path.LastIndexOf('/') + 1);
            public IEnumerable<State> All() { yield return this; foreach (var c in Children) foreach (var s in c.All()) yield return s; }
        }

        static State ReadState(FbTable t)
        {
            var s = new State { Path = t.String(0) ?? "", Type = t.U32(1) };
            var source = t.Table(3);
            if (!source.IsNull)
            {
                var label = source.Table(0);
                s.Clip = label.IsNull ? null : label.String(0);
                s.Speed = source.F32(1, 0); // absent = 0 (FlatBuffers drops default scalars): the "_stop" holds
            }
            var transitions = t.Vector(4);
            for (int i = 0; !transitions.IsNull && i < transitions.Length; i++)
            {
                var tt = transitions.Table(i);
                var tr = new Transition { Path = tt.String(0) ?? "", ExitTime = tt.F32(2, 1), Duration = tt.F32(3), Offset = tt.F32(4) };
                var conditions = tt.Vector(6);
                for (int k = 0; !conditions.IsNull && k < conditions.Length; k++)
                {
                    var ct = conditions.Table(k);
                    byte kind = ct.U8(2); // union ConditionValue: 1 int, 2 float, 3 bool, 4 trigger
                    var vt = ct.Table(3);
                    double v = vt.IsNull ? 0 : kind == 1 ? vt.U32(0) : kind == 2 ? vt.F32(0) : kind == 3 ? vt.I8(0) : vt.U8(0);
                    tr.Conditions.Add((ct.String(0) ?? "", ct.U32(1), v));
                }
                s.Transitions.Add(tr);
            }
            var children = t.Vector(5);
            for (int i = 0; !children.IsNull && i < children.Length; i++) s.Children.Add(ReadState(children.Table(i)));
            var flags = t.Vector(6);
            for (int i = 0; !flags.IsNull && i < flags.Length; i++) if (flags.String(i) is string f && f.Length > 0) s.Flags.Add(f);
            return s;
        }

        void ReadStates(byte[] data)
        {
            var machines = FbTable.Root(data).Table(0).Vector(0);
            for (int i = 0; !machines.IsNull && i < machines.Length; i++)
            {
                var top = machines.Table(i);
                string name = top.String(0) ?? "";
                var root = ReadState(top.Table(1));
                if (i == 0 || name == "Base")
                {
                    foreach (var s in root.All())
                    {
                        if (string.IsNullOrEmpty(s.Clip)) continue;
                        if (!ClipFlags.TryGetValue(s.Clip, out var list)) ClipFlags[s.Clip] = list = new List<string>();
                        foreach (var f in s.Flags) if (!list.Contains(f)) list.Add(f);
                    }
                }
                else if (BuildFace(name, root) is FaceLayer face) Faces.Add(face);
            }
        }

        static readonly Regex Numbered = new Regex(@"^\d+_");

        // "0_eye01_close_stop" -> "close_stop" for layer eye01 (Legends Z-A writes "#0_eye01#_close_stop").
        static string Short(State s, string layer)
        {
            string leaf = s.Leaf;
            int at = leaf.IndexOf(layer, StringComparison.Ordinal);
            if (at >= 0) return leaf.Substring(at + layer.Length).TrimStart('#').TrimStart('_');
            return Numbered.Replace(leaf, "");
        }

        FaceLayer BuildFace(string layer, State root)
        {
            var all = root.All().ToList();
            var byPath = all.ToDictionary(s => s.Path, s => s);
            var withClip = all.Where(s => !string.IsNullOrEmpty(s.Clip)).ToList();
            if (withClip.Count == 0) return null;
            var face = new FaceLayer { Name = layer, Clip = withClip[0].Clip };
            // The parameter and trigger: the names the layer's conditions use most.
            var conditions = all.SelectMany(s => s.Transitions).SelectMany(t => t.Conditions).ToList();
            face.Parameter = conditions.Where(c => c.name.EndsWith("_int")).GroupBy(c => c.name).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            face.Trigger = conditions.Where(c => c.name.EndsWith("_trigger")).GroupBy(c => c.name).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

            // Poses: the first category's states (the others repeat them). A pose state plays the clip and hands over to
            // its "_stop" hold; the parameter value comes from the transitions out of the default state into either.
            var seen = new HashSet<string>();
            foreach (var p in withClip)
            {
                if (p.Speed <= 0 || p.Path.Contains("blink")) continue;
                string name = Short(p, layer);
                if (!seen.Add(name)) continue;
                var toStop = p.Transitions.FirstOrDefault(t => Short(byPath.TryGetValue(t.Path, out var st) ? st : new State { Path = t.Path }, layer) == name + "_stop");
                float time = toStop?.Offset ?? 0;
                string stopPath = toStop?.Path;
                int? value = null;
                foreach (var d in all.Where(s => Short(s, layer) == "default"))
                    foreach (var t in d.Transitions)
                    {
                        if (t.Path != p.Path && t.Path != stopPath) continue;
                        foreach (var c in t.Conditions)
                            if (face.Parameter != null && c.name == face.Parameter && c.func == 4) { value = (int)c.value; break; }
                        if (value != null) break;
                    }
                if (value == null) continue;
                face.Poses.Add(new FacePose { Name = name, Value = value.Value, Time = time });
            }
            face.Poses.Sort((a, b) => a.Value.CompareTo(b.Value));

            // The blink: the chain of states from its sub-machine's Entry, each a segment (incoming offset .. own exit time).
            var blinkRoot = all.FirstOrDefault(s => s.Leaf.EndsWith("blink") && s.Children.Count > 0);
            if (blinkRoot != null)
            {
                var entry = blinkRoot.Children.FirstOrDefault(s => s.Type == 1);
                var next = entry?.Transitions.FirstOrDefault();
                float from = next?.Offset ?? 0;
                var visited = new HashSet<string>();
                while (next != null && byPath.TryGetValue(next.Path, out var st) && st.Type == 0 && visited.Add(st.Path))
                {
                    var outgoing = st.Transitions.FirstOrDefault();
                    face.Blink.Add(new FaceSegment { From = from, To = outgoing?.ExitTime ?? 1, Speed = st.Speed });
                    next = outgoing;
                    from = outgoing?.Offset ?? 0;
                }
            }
            return face;
        }

        // ---- tralk ----

        // Scarlet/Violet's layout (PokeDocs tralk.fbs). Legends Z-A's differs past the channel name: whatever doesn't
        // read keeps its default, so a bone name alone is still a look-at.
        // Scarlet/Violet (PokeDocs tralk.fbs, with its unk_13 / unk_14 being the eye mesh and material strings and unk_17
        // the UV table): root field 1 = channels; a channel: 0 name, 4..7 limits, 10 smoothing, 13 mesh, 14 material,
        // 15 the eye's offset (vec3), 17 the 3x3 UV table, 20 the fade angle, 21 track names. A channel with a material
        // is an eye, the others are the chain's bones in root-first order.
        // Legends Z-A: root field 4 = channels; a channel: 0 bone, 1 = 1 for an eye, 2/3 smoothing, 8..13 six limits
        // (yaw left/right, pitch up/down, and a third pair), 16 mesh, 17 material, 19 track names, 22 the UV table.
        // Mega Lucario's eye table equals SV Lucario's, so the two files describe the same thing.
        void ReadLookAt(byte[] data)
        {
            var look = new LookAtInfo { Speed = 15, Weight = 1, MaxAngle = 45 };
            bool read = false;
            try { read = ReadLookAtSv(FbTable.Root(data), look); } catch (Exception) { read = false; }
            if (!read)
            {
                look = new LookAtInfo { Speed = 15, Weight = 1, MaxAngle = 45 };
                try { read = ReadLookAtZa(FbTable.Root(data), look); } catch (Exception) { read = false; }
            }
            if (!read || look.Bones.Count == 0) return;
            look.Bone = look.Bones[0].Name;
            look.Speed = look.Bones[0].Smooth;
            look.EyeUv = look.Eyes.Count > 0;
            // Bulbasaur's file says 0.45 where every other says 45; Snorlax's and Lucario's say 0: the usual angle.
            if (look.MaxAngle <= 1 || look.MaxAngle > 180) look.MaxAngle = 45;
            LookAt = look;
        }

        static float[] Floats(FbVector v, int count)
        {
            var r = new float[count];
            for (int i = 0; !v.IsNull && i < count && i < v.Length; i++) r[i] = v.F32(i);
            return r;
        }

        static float[] Vec3At(FbTable t, int field)
        {
            int p = t.InlineStruct(field);
            return p == 0 ? new float[3] : new[] { BitConverter.ToSingle(t.Buffer, p), BitConverter.ToSingle(t.Buffer, p + 4), BitConverter.ToSingle(t.Buffer, p + 8) };
        }

        static List<string> StringsOf(FbVector v)
        {
            var list = new List<string>();
            for (int i = 0; !v.IsNull && i < v.Length; i++) if (v.String(i) is string s && s.Length > 0) list.Add(s);
            return list;
        }

        bool ReadLookAtSv(FbTable root, LookAtInfo look)
        {
            var channels = root.Vector(1);
            if (channels.IsNull || channels.Length == 0) return false;
            for (int i = 0; i < channels.Length; i++)
            {
                var c = channels.Table(i);
                string name = c.String(0);
                if (string.IsNullOrEmpty(name)) return false;
                string material = c.Has(14) ? c.String(14) : null;
                float a = c.F32(4), b = c.F32(5), up = c.F32(6), down = c.F32(7), smooth = c.F32(10, 0.3f);
                if (a < 0 || a > 180 || b < 0 || b > 180) return false;
                if (i == 0) look.MaxAngle = c.F32(20, 45);
                if (string.IsNullOrEmpty(material))
                    look.Bones.Add(new LookAtBone { Name = name, YawLeft = a, YawRight = b, Up = up, Down = down, Smooth = smooth });
                else
                    look.Eyes.Add(new LookAtEye
                    {
                        Bone = name, Mesh = c.String(13), Material = material, YawLeft = a, YawRight = b, Up = up, Down = down, Smooth = smooth,
                        Offset = Vec3At(c, 15), Map = Floats(c.Vector(17), 9), Tracks = StringsOf(c.Vector(21)),
                    });
            }
            return look.Bones.Count > 0;
        }

        bool ReadLookAtZa(FbTable root, LookAtInfo look)
        {
            var channels = root.Vector(4);
            if (channels.IsNull || channels.Length == 0) return false;
            look.MaxAngle = 45;
            for (int i = 0; i < channels.Length; i++)
            {
                var c = channels.Table(i);
                string name = c.String(0);
                if (string.IsNullOrEmpty(name)) return false;
                bool eye = c.U32(1) == 1;
                float a = c.F32(8), b = c.F32(9), up = c.F32(10), down = c.F32(11), smooth = c.F32(2, eye ? 0.05f : 0.3f);
                if (a < 0 || a > 180 || b < 0 || b > 180) return false;
                if (!eye)
                    look.Bones.Add(new LookAtBone { Name = name, YawLeft = a, YawRight = b, Up = up, Down = down, Smooth = smooth });
                else
                    look.Eyes.Add(new LookAtEye
                    {
                        Bone = name, Mesh = c.String(16), Material = c.String(17), YawLeft = a, YawRight = b, Up = up, Down = down, Smooth = smooth,
                        Map = Floats(c.Vector(22), 9), Tracks = StringsOf(c.Vector(19)),
                    });
            }
            return look.Bones.Count > 0;
        }
    }
}
