using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using PokeModelExporter.Formats;

namespace PokeModelExporter.Scarlet
{
    // A move's effect timeline (effect/battle_ew/ew<move>/ew<move>.trtml). No published schema; layout found by
    // walking the FlatBuffer (2026-10-13, Tackle and Thunderbolt). Command types and parameter names are strings,
    // values are the same typed union as animation notifies (.traef):
    //   TRTML { groups: [Group], frames (length), _, ? }
    //   Group { object (ATK, DEF, a particle's short name, Camera, gauge...), _, tracks: [Track], conditions: [Condition] }
    //   Track { name, _, commands: [Command] }
    //   Command { object, type (e.g. Particle/Emit), start, end (frames, 30 fps), ranged, _, _, _, _, params: [Param] }
    //   Param { name, type (1 int, 2 float, 3 bool, 4 string, 5 vec3), value table }   (absent value = zero / empty)
    //   Condition { name, value } (e.g. TargetMulti = None)
    // Several groups may drive the same object under different conditions (single vs. spread target, raids...).
    public sealed class SvMoveTimeline
    {
        public int Frames;
        public List<TimelineGroup> Groups = new List<TimelineGroup>();

        public static SvMoveTimeline Read(byte[] data)
        {
            var root = FbTable.Root(data);
            var timeline = new SvMoveTimeline { Frames = root.I32(1) };
            var groups = root.Vector(0);
            for (int g = 0; g < groups.Length; g++)
            {
                var gt = groups.Table(g);
                var group = new TimelineGroup { Object = gt.String(0) };
                var conditions = gt.Vector(3);
                for (int c = 0; c < conditions.Length; c++)
                {
                    var ct = conditions.Table(c);
                    group.Conditions.Add(new KeyValuePair<string, string>(ct.String(0), ct.String(1)));
                }
                var tracks = gt.Vector(2);
                for (int t = 0; t < tracks.Length; t++)
                {
                    var tt = tracks.Table(t);
                    var track = new TimelineTrack { Name = tt.String(0) };
                    var commands = tt.Vector(2);
                    for (int k = 0; k < commands.Length; k++)
                        track.Commands.Add(ReadCommand(commands.Table(k)));
                    group.Tracks.Add(track);
                }
                timeline.Groups.Add(group);
            }
            return timeline;
        }

        static TimelineCommand ReadCommand(FbTable t)
        {
            var command = new TimelineCommand
            {
                Object = t.String(0), Type = t.String(1), Start = t.I32(2), End = t.I32(3), Ranged = t.Bool(4),
            };
            var parameters = t.Vector(9);
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters.Table(i);
                string name = p.String(0);
                if (name == null) continue;
                int type = p.U8(1);
                var v = p.Table(2);
                command.Parameters[name] = type switch
                {
                    1 => v.IsNull ? 0 : v.I32(0),
                    2 => v.IsNull ? 0f : v.F32(0),
                    3 => !v.IsNull && v.Bool(0),
                    4 => v.IsNull ? "" : v.String(0) ?? "",
                    5 => v.IsNull || !v.Has(0) ? Vector3.Zero : ReadVector3(v),
                    _ => $"<type {type}>",
                };
            }
            return command;
        }

        static Vector3 ReadVector3(FbTable v)
        {
            int p = v.InlineStruct(0);
            var b = v.Buffer;
            return new Vector3(System.BitConverter.ToSingle(b, p), System.BitConverter.ToSingle(b, p + 4), System.BitConverter.ToSingle(b, p + 8));
        }

        public static string Format(object value) => value switch
        {
            float f => f.ToString("0.###", CultureInfo.InvariantCulture),
            Vector3 v => string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", v.X, v.Y, v.Z),
            bool b => b ? "true" : "false",
            _ => System.Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    public sealed class TimelineGroup
    {
        public string Object;
        public List<KeyValuePair<string, string>> Conditions = new List<KeyValuePair<string, string>>();
        public List<TimelineTrack> Tracks = new List<TimelineTrack>();
    }

    public sealed class TimelineTrack
    {
        public string Name;
        public List<TimelineCommand> Commands = new List<TimelineCommand>();
    }

    public sealed class TimelineCommand
    {
        public string Object, Type;
        public int Start, End;
        // Set on commands that act over start..end (blends, shakes); one-shot commands have end = start + 1.
        public bool Ranged;
        // int, float, bool, string or Vector3.
        public Dictionary<string, object> Parameters = new Dictionary<string, object>();
    }
}
