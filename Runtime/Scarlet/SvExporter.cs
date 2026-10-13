using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PokeModelExporter.Export;
using PokeModelExporter.Formats;
using PokeModelExporter.Model;

namespace PokeModelExporter.Scarlet
{
    // One-call export of a Scarlet/Violet Pokemon: model, textures, clips with their notifies and
    // tracks, baked materials, GLB, plus its config and a compact actor manifest for game engines.
    // Shared by the command-line tool and in-game importers.
    public sealed class SvExporter : IDisposable
    {
        readonly Oodle _oodle;
        public TrinityArchive Archive { get; }
        public SvCatalog Catalog { get; }
        public SvPersonal Personal { get; }

        // Identifies the game data version (index file size + date): caches keyed on it go stale on updates.
        public string Fingerprint { get; }

        public SvExporter(string arcFolder, string oodleDll)
        {
            _oodle = new Oodle(oodleDll);
            Archive = new TrinityArchive(arcFolder, _oodle);
            Catalog = new SvCatalog(Archive.Read(SvCatalog.Path));
            Personal = new SvPersonal(Archive.Read(SvPersonal.Path));
            var fd = new FileInfo(Path.Combine(arcFolder, "data.trpfd"));
            Fingerprint = $"{fd.Length}-{fd.LastWriteTimeUtc.Ticks}";
        }

        // The arc folder (holding data.trpfd/trpfs) of an extracted game: <game>/arc, <game>/update/arc or <game>/base/arc.
        public static string FindArc(string gameFolder) =>
            new[] { "", "update", "base" }.Select(sub => Path.Combine(gameFolder, sub, "arc"))
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "data.trpfd")));

        public sealed class Options
        {
            public bool Shiny;
            public bool Animations = true;
            public bool ExtraImages = true;
            public Func<string, bool> Clips; // which clips to keep (name without the model prefix, e.g. 00001_battlewait01_loop); null = all
            public bool MainClipSetOnly;     // only the main clip set (see MainClipSet)
            public IEnumerable<string> FolderFiles; // every file of the model folder, if known (hash list)
        }

        public sealed class Result
        {
            public SvCatalog.Entry Entry;
            public string Folder;           // archive path of the model folder
            public ModelData Model;
            public SvConfig Config;
            public byte[] Glb;
            public string ActorManifest;    // JSON, see ActorManifest
            public Func<string, byte[]> Read; // read a file of the model folder
        }

        // The catalog entry for a National Dex number (mapped to the game's model number), or null when
        // the game has no model for it.
        public SvCatalog.Entry Find(int species, int form = 0, int gender = 0)
        {
            if (!Personal.IsPresent(species, form) && !Personal.IsPresent(species)) return null;
            var entry = Catalog.Find(Personal.CatalogSpecies(species, form), form, gender);
            return entry != null && entry.Form == form ? entry : form == 0 ? entry : null;
        }

        // Null when the species/form/gender isn't in this game. species = National Dex number.
        public Result Export(int species, int form, int gender, Options options)
        {
            var entry = Find(species, form, gender);
            if (entry == null) return null;
            string folder = SvCatalog.DataRoot + entry.Folder;
            Func<string, byte[]> read = file => Archive.Read(folder + file);

            var model = TrinityModelReader.Read(entry.ModelPath.Substring(entry.Folder.Length), read, shiny: options.Shiny, locatorFiles: entry.LocatorFiles());
            if (options.Animations) LoadClips(model, read, options.Clips, options.MainClipSetOnly);

            var baked = SvMaterialBaker.Bake(model, read);
            Dictionary<string, byte[]> extra = null;
            if (options.ExtraImages)
            {
                var names = new List<string>();
                foreach (var suffix in new[] { "", "_rare", "_edit" })
                {
                    var data = read(model.Name + suffix + ".trmtr");
                    if (data == null) continue;
                    var mats = FbTable.Root(data).Vector(1);
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var tx = mats.Table(i).Vector(2);
                        for (int k = 0; k < tx.Length; k++) names.Add(tx.Table(k).String(1));
                    }
                }
                names.Add(Path.GetFileName(entry.IconPath));
                names.Add(Path.GetFileNameWithoutExtension(entry.IconPath) + "_big.bntx");
                if (options.FolderFiles != null) names.AddRange(options.FolderFiles.Where(f => f.EndsWith(".bntx")));
                extra = SvMaterialBaker.ExtraTextures(names, read, baked);
            }

            var config = SvConfig.Read(read(Path.GetFileName(entry.ConfigPath)));
            return new Result
            {
                Entry = entry,
                Folder = folder,
                Model = model,
                Config = config,
                Glb = GlbWriter.Write(model, baked.Textures, m => baked.Mappings[m.Name], extra),
                ActorManifest = ActorManifest.Write(model, entry, config, options.Shiny),
                Read = read,
            };
        }

        // Every skeletal clip listed in the model's animation resource file, with its notifies (.traef) and
        // material/visibility/blend-shape tracks (.tracm). Meshes the battle idle hides are hidden by default.
        public static void LoadClips(ModelData model, Func<string, byte[]> read, Func<string, bool> filter = null, bool mainSetOnly = false)
        {
            var tracr = read(model.Name + "_base.tracr");
            if (tracr == null) return;
            var files = TrinityAnimationReader.ClipNamesFromResource(tracr);
            string ClipName(string file) => Path.GetFileNameWithoutExtension(file).Substring(model.Name.Length + 1); // 00001_battlewait01_loop
            char? set = mainSetOnly ? MainClipSet(files.Select(ClipName)) : null;
            foreach (var file in files)
            {
                string clip = ClipName(file);
                if (set != null && clip[0] != set) continue;
                if (filter != null && !filter(clip)) continue;
                var data = read(file);
                if (data == null) continue;
                var anim = TrinityAnimationReader.Read(data, clip);
                var traef = read(Path.ChangeExtension(file, ".traef"));
                if (traef != null) anim.Events = TrinityAnimationReader.ReadEvents(traef);
                var tracm = read(Path.ChangeExtension(file, ".tracm"));
                if (tracm != null) TrinityCurveReader.Read(tracm, anim);
                model.Animations.Add(anim);
            }
            var idle = model.Animations.FirstOrDefault(a => a.Name.Contains("battlewait")) ?? model.Animations.FirstOrDefault(a => a.Name.Contains("defaultwait"));
            if (idle != null)
                foreach (var v in idle.VisibilityTracks.Where(v => v.Fixed == false))
                    foreach (var mesh in model.Meshes.Where(m => m.Name == v.Mesh)) mesh.Hidden = true;
        }

        // Clips come in sets by their number's first digit: 0xxxx standing, 1xxxx swimming, 2xxxx floating
        // or flying. Many Pokemon have only one (Gyarados 1, Venomoth and Magnemite 2), some several:
        // Charizard has full sets 0 and 2, while Articuno or Dragonite stand idle in set 0 but battle
        // in set 2. The main set is the one with the most battle clips (idle, attacks, hit, faint),
        // the lowest number on a tie.
        static readonly string[] BattleClipKinds = { "wait01_loop", "_attack01", "_rangeattack01", "_damage01", "_down01_start" };

        public static char? MainClipSet(IEnumerable<string> clipNames)
        {
            var names = clipNames.Where(n => n.Length > 0 && char.IsDigit(n[0])).ToList();
            char? best = null;
            int bestScore = -1;
            foreach (char set in names.Select(n => n[0]).Distinct().OrderBy(c => c))
            {
                int score = BattleClipKinds.Count(kind => names.Any(n => n[0] == set && n.Contains(kind)));
                if (score > bestScore) { best = set; bestScore = score; }
            }
            return best;
        }

        public void Dispose()
        {
            Archive.Dispose();
            _oodle.Dispose();
        }
    }

    // Per-Pokemon config (.trpokecfg, schema PokeDocs SV/Flatbuffers/model/trpokecfg.fbs): size class and
    // values the game uses to frame the Pokemon in cameras.
    public sealed class SvConfig
    {
        public static readonly string[] SizeNames = { "S", "M", "L", "XL" };

        public int SizeIndex;            // 0 S, 1 M, 2 L, 3 XL
        public float MinSize, MaxSize;
        public float InframeVerticalRotYOrigin, InframeBottomYOffset, InframeCenterYOffset;
        public float InframeLeftRotation, InframeRightRotation;

        public string SizeName => SizeIndex >= 0 && SizeIndex < SizeNames.Length ? SizeNames[SizeIndex] : "M";

        public static SvConfig Read(byte[] data)
        {
            if (data == null) return null;
            var t = FbTable.Root(data);
            return new SvConfig
            {
                SizeIndex = t.U8(0),
                MinSize = t.F32(1), MaxSize = t.F32(2),
                InframeVerticalRotYOrigin = t.F32(4), InframeBottomYOffset = t.F32(5), InframeCenterYOffset = t.F32(6),
                InframeLeftRotation = t.F32(7), InframeRightRotation = t.F32(8),
            };
        }
    }

    // A compact JSON description of what a game needs to drive the model beyond glTF:
    //   { species, form, gender, shiny, size, locators: [names], hiddenMeshes: [names],
    //     clips: { name: { duration, loop, events: [{type, time, duration, params}],
    //                      visibility: [{mesh, visible} | {mesh, keys: [[t, bool]]}],
    //                      material: [{mesh, material, parameter, r|g|b|a: [[t, v]]}] } } }
    // Times in seconds.
    public static class ActorManifest
    {
        public static string Write(ModelData model, SvCatalog.Entry entry, SvConfig config, bool shiny)
        {
            var w = new JsonWriter();
            w.BeginObject();
            w.Name("version").Value(1);
            w.Name("species").Value(entry.Species).Name("form").Value(entry.Form).Name("gender").Value(entry.Gender);
            w.Name("shiny").Value(shiny);
            w.Name("size").Value(config?.SizeName ?? "M");
            w.Name("locators").BeginArray();
            foreach (var n in model.Nodes) if (n.IsLocator) w.Value(n.Name);
            w.EndArray();
            w.Name("hiddenMeshes").BeginArray();
            foreach (var m in model.Meshes) if (m.Hidden) w.Value(m.Name);
            w.EndArray();
            w.Name("clips").BeginObject();
            foreach (var a in model.Animations)
            {
                float fps = a.FramesPerSecond > 0 ? a.FramesPerSecond : 30;
                w.Name(a.Name).BeginObject();
                w.Name("duration").Value(a.Duration).Name("loop").Value(a.Loop);
                w.Name("events").BeginArray();
                foreach (var e in a.Events)
                {
                    w.BeginObject().Name("type").Value(e.Type).Name("time").Value(e.StartFrame / fps).Name("duration").Value(e.DurationFrames / fps);
                    w.Name("params").BeginObject();
                    foreach (var kv in e.Parameters) w.Name(kv.Key).Value(kv.Value);
                    w.EndObject().EndObject();
                }
                w.EndArray();
                w.Name("visibility").BeginArray();
                foreach (var v in a.VisibilityTracks)
                {
                    w.BeginObject().Name("mesh").Value(v.Mesh);
                    if (v.Fixed.HasValue) w.Name("visible").Value(v.Fixed.Value);
                    else
                    {
                        w.Name("keys").BeginArray();
                        for (int k = 0; k < v.Keys.Times.Length; k++)
                            w.BeginArray().Value(v.Keys.Times[k] / fps).Value(v.Keys.Values[k]).EndArray();
                        w.EndArray();
                    }
                    w.EndObject();
                }
                w.EndArray();
                w.Name("material").BeginArray();
                foreach (var t in a.MaterialTracks)
                {
                    w.BeginObject().Name("mesh").Value(t.Mesh).Name("material").Value(t.Material).Name("parameter").Value(t.Parameter);
                    foreach (var (name, keys) in new[] { ("r", t.R), ("g", t.G), ("b", t.B), ("a", t.A) })
                    {
                        if (keys == null) continue;
                        w.Name(name).BeginArray();
                        for (int k = 0; k < keys.Times.Length; k++) w.BeginArray().Value(keys.Times[k] / fps).Value(keys.Values[k]).EndArray();
                        w.EndArray();
                    }
                    w.EndObject();
                }
                w.EndArray();
                w.EndObject();
            }
            w.EndObject();
            w.EndObject();
            return w.ToString();
        }
    }
}
