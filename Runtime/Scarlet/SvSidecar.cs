using System;
using System.Collections.Generic;
using System.IO;
using PokeModelExporter.Export;
using PokeModelExporter.Formats;

namespace PokeModelExporter.Scarlet
{
    // The JSON sidecar written next to a GLB: everything about a Pokemon that glTF can't hold.
    //   catalog entry, the animation controller (state machine, layers, parameters, look-at, spine,
    //   slope, motion detector), config (size, framing), material table, every material set
    //   (normal, rare, edit) and, given a full file list, any other file of the model folder.
    // Files with a known schema are decoded to JSON; others are stored raw (base64).
    public static class SvSidecar
    {
        // File extension -> schema path in pkZukan/PokeDocs under SV/Flatbuffers/.
        public static readonly Dictionary<string, string> SchemaPaths = new Dictionary<string, string>
        {
            [".tracn"] = "animation/tracn.fbs", [".tracs"] = "animation/tracs.fbs", [".tracl"] = "animation/tracl.fbs",
            [".tracp"] = "animation/tracp.fbs", [".tracr"] = "animation/tracr.fbs", [".tralk"] = "animation/tralk.fbs",
            [".trmdt"] = "animation/trmdt.fbs", [".tracm"] = "animation/tracm.fbs", [".traef"] = "animation/traef.fbs",
            [".tranm"] = "animation/tranm.fbs",
            [".trmmt"] = "model/trmmt.fbs", [".trpokecfg"] = "model/trpokecfg.fbs", [".trmtr"] = "model/trmtr.fbs",
            [".trmdl"] = "model/trmdl.fbs", [".trskl"] = "model/trskl.fbs", [".trmsh"] = "model/trmsh.fbs",
        };

        // Already fully represented in the GLB (geometry, textures, clips): not repeated in the sidecar.
        static readonly HashSet<string> InGlb = new HashSet<string> { ".trmbf", ".trmsh", ".bntx", ".tranm", ".traef", ".tracm" };

        // read: file name relative to the model folder -> bytes or null.
        // schema: extension -> parsed schema or null. allFiles: every file of the folder, if known.
        public static string Build(SvCatalog.Entry entry, string game, Func<string, byte[]> read,
            Func<string, FbsSchema> schema, IEnumerable<string> allFiles = null)
        {
            string baseName = Path.GetFileNameWithoutExtension(entry.ModelPath);
            var files = new List<string>();
            void Add(string f) { if (f != null && !files.Contains(f) && read(f) != null) files.Add(f); }

            // The animation controller: the .tracn lists its parts.
            string tracn = baseName + "_base.tracn";
            Add(tracn);
            var tracnData = read(tracn);
            if (tracnData != null)
            {
                var list = FbTable.Root(tracnData).Vector(0);
                for (int i = 0; i < list.Length; i++) Add(list.Table(i).String(1));
            }
            Add(baseName + "_base_motion_detector.trmdd");
            Add(Path.GetFileName(entry.ConfigPath));
            Add(Path.GetFileName(entry.MaterialTablePath));
            foreach (var suffix in new[] { "", "_rare", "_edit" }) Add(baseName + suffix + ".trmtr");
            if (allFiles != null)
                foreach (var f in allFiles)
                    if (!InGlb.Contains(Path.GetExtension(f))) Add(f);

            var w = new JsonWriter();
            w.BeginObject();
            w.Name("generator").Value("PokeModelExporter");
            w.Name("game").Value(game);
            w.Name("pokemon").BeginObject()
                .Name("species").Value(entry.Species).Name("form").Value(entry.Form).Name("gender").Value(entry.Gender)
                .Name("folder").Value(SvCatalog.DataRoot + entry.Folder).EndObject();
            w.Name("catalog").BeginObject()
                .Name("model").Value(entry.ModelPath).Name("materialTable").Value(entry.MaterialTablePath)
                .Name("config").Value(entry.ConfigPath).Name("icon").Value(entry.IconPath);
            w.Name("animations").BeginArray(); foreach (var a in entry.AnimationPaths) w.Value(a); w.EndArray();
            w.Name("locators").BeginArray(); foreach (var l in entry.LocatorPaths) w.Value(l); w.EndArray();
            w.EndObject();

            w.Name("files").BeginObject();
            foreach (var f in files)
            {
                var data = read(f);
                string ext = Path.GetExtension(f);
                w.Name(f).BeginObject();
                var s = SchemaPaths.ContainsKey(ext) ? schema(ext) : null;
                bool decoded = false;
                if (s != null)
                {
                    // Decode into a separate writer so a schema mismatch falls back to raw cleanly.
                    try
                    {
                        var inner = new JsonWriter();
                        FlatBufferJson.Write(data, s, inner);
                        w.Name("schema").Value("PokeDocs SV/Flatbuffers/" + SchemaPaths[ext]);
                        w.Name("data").Raw(Indent(inner.ToString(), 3));
                        decoded = true;
                    }
                    catch (Exception e)
                    {
                        w.Name("decodeError").Value(e.Message);
                    }
                }
                if (!decoded) w.Name("raw").Value(Convert.ToBase64String(data));
                w.Name("size").Value(data.Length);
                w.EndObject();
            }
            w.EndObject();
            w.EndObject();
            return w.ToString();
        }

        // Re-indents a nested JSON document written by its own writer.
        static string Indent(string json, int level) => json.Replace("\n", "\n" + new string(' ', level * 2));
    }
}
