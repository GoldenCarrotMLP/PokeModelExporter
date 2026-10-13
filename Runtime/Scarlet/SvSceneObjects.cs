using System.Collections.Generic;
using PokeModelExporter.Formats;

namespace PokeModelExporter.Scarlet
{
    // A move's scene objects (effect/battle_ew/ew<move>/ew<move>_0.trsot): which particle file each object a
    // timeline names ("hit", "beam"...) plays. No published schema; layout found by walking the FlatBuffer:
    //   root { name, _, _, _, entries: [Entry] }   Entry { type, payload: [ubyte] (nested FlatBuffer), children: [Entry] }
    //   trinity_SceneObject payload { name, transform... } ; trinity_ParticleComponent payload { ptcl path, _, _, name, "global" }
    // The root entry is the move's own object; its children are the named objects, whose children are components.
    public static class SvSceneObjects
    {
        // Object name -> particle files (in the order listed).
        public static Dictionary<string, List<string>> Read(byte[] data)
        {
            var result = new Dictionary<string, List<string>>();
            var entries = FbTable.Root(data).Vector(4);
            for (int i = 0; i < entries.Length; i++) Visit(entries.Table(i), result);
            return result;
        }

        static void Visit(FbTable entry, Dictionary<string, List<string>> result)
        {
            var children = entry.Vector(2);
            if (entry.String(0) == "trinity_SceneObject")
            {
                var payload = Payload(entry);
                string name = payload.IsNull ? null : payload.String(0);
                for (int i = 0; i < children.Length; i++)
                {
                    var child = children.Table(i);
                    if (child.String(0) != "trinity_ParticleComponent") continue;
                    var component = Payload(child);
                    string path = component.IsNull ? null : component.String(0);
                    if (name == null || path == null) continue;
                    if (!result.TryGetValue(name, out var list)) result[name] = list = new List<string>();
                    list.Add(path);
                }
            }
            for (int i = 0; i < children.Length; i++)
            {
                var child = children.Table(i);
                if (child.String(0) == "trinity_SceneObject") Visit(child, result);
            }
        }

        // Object name -> its 3D model (trinity_ModelComponent: an effect model such as Bind's rings, .trmdl path).
        public static Dictionary<string, string> Models(byte[] data)
        {
            var result = new Dictionary<string, string>();
            void Walk(FbTable entry)
            {
                var children = entry.Vector(2);
                if (entry.String(0) == "trinity_SceneObject")
                {
                    var payload = Payload(entry);
                    string name = payload.IsNull ? null : payload.String(0);
                    for (int i = 0; i < children.Length; i++)
                    {
                        var child = children.Table(i);
                        if (child.String(0) != "trinity_ModelComponent") continue;
                        var component = Payload(child);
                        string path = component.IsNull ? null : component.String(0);
                        if (name != null && path != null && !result.ContainsKey(name)) result[name] = path;
                    }
                }
                for (int i = 0; i < children.Length; i++)
                    if (children.Table(i).String(0) == "trinity_SceneObject") Walk(children.Table(i));
            }
            var entries = FbTable.Root(data).Vector(4);
            for (int i = 0; i < entries.Length; i++) Walk(entries.Table(i));
            return result;
        }

        // Debug view: every entry as "type: strings in its payload", indented by depth.
        public static List<string> Describe(byte[] data)
        {
            var lines = new List<string>();
            void Walk(FbTable entry, int depth)
            {
                var payload = Payload(entry);
                var fields = new List<string>();
                if (!payload.IsNull)
                    for (int f = 0; f < 12; f++)
                    {
                        string v = null;
                        try { v = payload.String(f); } catch { }
                        if (!string.IsNullOrEmpty(v) && v.Length < 200 && IsText(v)) fields.Add($"{f}={v}");
                    }
                lines.Add(new string(' ', depth * 2) + entry.String(0) + "  " + string.Join("  ", fields));
                var children = entry.Vector(2);
                for (int i = 0; i < children.Length; i++) Walk(children.Table(i), depth + 1);
            }
            var entries = FbTable.Root(data).Vector(4);
            for (int i = 0; i < entries.Length; i++) Walk(entries.Table(i), 0);
            return lines;
        }

        static bool IsText(string s)
        {
            foreach (char c in s) if (c < 32 || c > 126) return false;
            return true;
        }

        static FbTable Payload(FbTable entry)
        {
            var bytes = entry.Vector(1);
            if (bytes.IsNull || bytes.Length < 8) return default;
            var segment = bytes.Bytes();
            var copy = new byte[segment.Count];
            System.Array.Copy(segment.Array, segment.Offset, copy, 0, segment.Count);
            return FbTable.Root(copy);
        }
    }
}
