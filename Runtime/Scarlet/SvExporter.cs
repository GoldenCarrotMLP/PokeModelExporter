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
            Catalog = SvCatalog.Open(Archive.Read) ?? throw new FileNotFoundException("No Pokemon catalog (pokemon/ or ik_pokemon/) in " + arcFolder);
            Personal = new SvPersonal(Archive.Read(SvPersonal.Path), SvPersonal.FieldShift(Catalog.Game));
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
            return entry == null ? null : Export(entry, options);
        }

        // The catalog's own species numbers past the Pokedex: 9999 = the Substitute doll (pm9999, migawari).
        public const int SubstituteDoll = 9999;

        // A catalog entry directly (models that aren't Pokemon, like the Substitute doll).
        public Result Export(SvCatalog.Entry entry, Options options)
        {
            string folder = entry.DataRoot + entry.Folder;
            Func<string, byte[]> read = file => Archive.Read(folder + file);

            var model = TrinityModelReader.Read(entry.ModelPath.Substring(entry.Folder.Length), read, shiny: options.Shiny, locatorFiles: entry.LocatorFiles());
            if (options.Animations) LoadClips(model, read, options.Clips, options.MainClipSetOnly);
            if (options.Animations && options.MainClipSetOnly && model.Animations.Count > 0) KeepLocatorSet(model, model.Animations[0].Name[0]);
            ScaleCompensation.Apply(model); // after the node list is final (locators are expected last before this)

            var baked = SvMaterialBaker.Bake(model, read, engineMaps: true);
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

        // A move's effect model (a scene object's trinity_ModelComponent, e.g. Earthquake's rocks
        // effect/battle_ew/ew0089/ew0089_rock/ew0089_rock.trmdl) as a GLB: meshes, skeleton, baked textures and every
        // clip its animation resource lists (an effect model usually has one, named like the model: "default").
        // readArchive: any archive file by path. Null when the model isn't there.
        public static byte[] ExportEffectModel(Func<string, byte[]> readArchive, string modelPath)
        {
            int slash = modelPath.LastIndexOf('/');
            string folder = modelPath.Substring(0, slash + 1), file = modelPath.Substring(slash + 1);
            Func<string, byte[]> read = f => readArchive(folder + f);
            if (read(file) == null) return null;
            var model = TrinityModelReader.Read(file, read);
            var tracr = read(model.Name + ".tracr") ?? read(model.Name + "_base.tracr");
            var clips = tracr != null ? TrinityAnimationReader.ClipNamesFromResource(tracr) : new List<string>();
            if (clips.Count == 0 && read(model.Name + ".tranm") != null) clips.Add(model.Name + ".tranm");
            foreach (var clipFile in clips)
            {
                var data = read(clipFile);
                if (data == null) continue;
                string stem = Path.GetFileNameWithoutExtension(clipFile);
                string clip = stem == model.Name ? "default" : stem.StartsWith(model.Name + "_", StringComparison.Ordinal) ? stem.Substring(model.Name.Length + 1) : stem;
                var anim = TrinityAnimationReader.Read(data, clip);
                var tracm = read(Path.ChangeExtension(clipFile, ".tracm"));
                if (tracm != null) TrinityCurveReader.Read(tracm, anim);
                model.Animations.Add(anim);
            }
            BindRigidMeshes(model);
            var baked = SvMaterialBaker.Bake(model, read);
            // Effect materials without a colour texture (Guillotine's claws, Bind's rings: the game feeds them from
            // elsewhere) show their BaseColor instead of plain white.
            return GlbWriter.Write(model, baked.Textures, m =>
            {
                var map = baked.Mappings[m.Name];
                bool textured = map.BaseColorTexture != null && baked.Textures.ContainsKey(map.BaseColorTexture);
                if (!textured && m.Colors.TryGetValue("BaseColor", out var c)) map.BaseColorFactor = new[] { c.X, c.Y, c.Z, 1f };
                return map;
            }, null);
        }

        // Effect models aren't skinned: each mesh rides the joint of the same name without "_shape" (Maya's
        // "shard1_shape" under "shard1", "pl_cup0_shape1" under "pl_cup01"), its vertices in model space at the bind pose. Bound as skinned meshes with weight 1 on that joint,
        // the clips move them (Earthquake's rock rises and breaks; before, only empty joints moved).
        static void BindRigidMeshes(ModelData model)
        {
            if (model.Nodes.Count == 0 || model.Meshes.TrueForAll(m => m.Joints != null)) return;
            int next = 0;
            foreach (var n in model.Nodes) next = Math.Max(next, n.SkinIndex + 1);
            var world = GlbWriter.WorldMatrices(model);
            foreach (var mesh in model.Meshes)
            {
                if (mesh.Joints != null || mesh.Positions == null) continue;
                NodeData node = null;
                foreach (var joint in JointNames(mesh.Name))
                    if ((node = model.Nodes.Find(n => n.Name == joint && !n.IsLocator)) != null) break;
                node ??= model.Nodes.Find(n => !n.IsLocator);
                if (node == null) continue;
                // Most meshes are stored in model space at the bind pose (Earthquake's shards sit where their joints
                // are); some relative to their joint (Trick's cup sits at the origin while its joint rests 0.75 aside).
                // Those are moved into model space first, so the bind-pose skinning puts both in the right place.
                // A joint resting far from scale 1 always holds its meshes relative to it: Shell Smash's shell rests at
                // 0.01 and grows to 1, and a model-space mesh would grow 100 times with it. Otherwise the reading that
                // puts the mesh nearer its own joint wins: Celebrate's lid (0.9 m up, its joint 3 m up) sits on top of
                // the box only as joint-relative, the box's sides (3-4 m up around their joints) only as model space.
                var bind = world[model.Nodes.IndexOf(node)];
                var centre = Centre(mesh.Positions);
                float scale = System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitX, bind).Length();
                float asModel = System.Numerics.Vector3.Distance(centre, bind.Translation);
                float asJoint = System.Numerics.Vector3.Distance(System.Numerics.Vector3.Transform(centre, bind), bind.Translation);
                if (scale < 0.5f || scale > 2f
                    || bind.Translation.Length() > 0.05f && asJoint < 0.5f * asModel)
                {
                    for (int i = 0; i < mesh.Positions.Length; i++) mesh.Positions[i] = System.Numerics.Vector3.Transform(mesh.Positions[i], bind);
                    if (mesh.Normals != null)
                        for (int i = 0; i < mesh.Normals.Length; i++) mesh.Normals[i] = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.TransformNormal(mesh.Normals[i], bind));
                }
                if (node.SkinIndex < 0) node.SkinIndex = next++;
                int count = mesh.Positions.Length;
                mesh.Joints = new int[count * 4];
                mesh.Weights = new float[count * 4];
                for (int i = 0; i < count; i++) { mesh.Joints[i * 4] = node.SkinIndex; mesh.Weights[i * 4] = 1; }
            }
        }

        static System.Numerics.Vector3 Centre(System.Numerics.Vector3[] points)
        {
            var min = new System.Numerics.Vector3(float.MaxValue); var max = new System.Numerics.Vector3(float.MinValue);
            foreach (var p in points) { min = System.Numerics.Vector3.Min(min, p); max = System.Numerics.Vector3.Max(max, p); }
            return (min + max) * 0.5f;
        }

        // The joint a Maya shape node belongs to, most likely first: "shard1_shape" -> shard1, "pl_cup0_shape1" ->
        // pl_cup01, "ring1_shape12" -> ring12, "ew0919_chain_shape1" -> ew0919_chain01, "rod1_r_shape__duprename_x" -> rod1_r.
        static IEnumerable<string> JointNames(string mesh)
        {
            int at = mesh.IndexOf("_shape", StringComparison.Ordinal);
            if (at < 0) { yield return mesh; yield break; }
            string prefix = mesh.Substring(0, at), rest = mesh.Substring(at + 6);
            string digits = new string(rest.TakeWhile(char.IsDigit).ToArray());
            string bare = prefix.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            yield return prefix + rest;
            if (digits.Length > 0)
            {
                yield return bare + digits;
                yield return prefix + digits.PadLeft(2, '0');
                yield return bare + digits.PadLeft(2, '0');
            }
            yield return prefix; // last: it can be the model's own root ("ew0919_chain")
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
                PinRootMotion(anim);
                model.Animations.Add(anim);
            }
            var idle = model.Animations.FirstOrDefault(a => a.Name.Contains("battlewait")) ?? model.Animations.FirstOrDefault(a => a.Name.Contains("defaultwait"));
            if (idle != null)
                foreach (var v in idle.VisibilityTracks.Where(v => v.Fixed == false))
                    foreach (var mesh in model.Meshes.Where(m => m.Name == v.Mesh)) mesh.Hidden = true;
        }

        // Walking and running loops carry the Pokemon forward on the skeleton's "origin" bone (Pikachu's walk 0.6 per
        // second), then the loop jumps back. Whoever plays them moves the Pokemon itself, so the bone stays in place over
        // the ground and the speed is kept (RootSpeed) to match the clip's pace to the real movement. Loops only: attack
        // clips that lunge and come back keep their motion.
        public static void PinRootMotion(AnimationData anim)
        {
            if (!anim.Loop) return;
            var track = anim.Tracks.FirstOrDefault(t => t.Bone == "origin" && t.Translation != null && t.Translation.Values.Length > 1);
            if (track == null) return;
            var keys = track.Translation;
            var first = keys.Values[0];
            var last = keys.Values[keys.Values.Length - 1];
            float dx = last.X - first.X, dz = last.Z - first.Z;
            float travel = (float)Math.Sqrt(dx * dx + dz * dz);
            if (travel < 1e-3f) return;
            for (int i = 0; i < keys.Values.Length; i++) keys.Values[i] = new System.Numerics.Vector3(first.X, keys.Values[i].Y, first.Z);
            float fps = anim.FramesPerSecond > 0 ? anim.FramesPerSecond : 30;
            float seconds = (keys.Times[keys.Times.Length - 1] - keys.Times[0]) / fps;
            if (seconds > 0) anim.RootSpeed = travel / seconds;
        }

        // Locator sets match the clip sets (00000 standing, 10000 swimming, 20000 floating/flying): the same names at
        // other heights (Charizard's eff_rangeattack01 is 0.69 m up standing, 1.32 m flying). With one clip set,
        // keep only its locators so a name means one point.
        static void KeepLocatorSet(ModelData model, char clipSet)
        {
            var sets = model.Nodes.Where(n => n.IsLocator).Select(n => n.LocatorSet).Distinct().ToList();
            string keep = sets.FirstOrDefault(s => !string.IsNullOrEmpty(s) && s[0] == clipSet);
            if (keep == null || sets.Count < 2) return;
            int firstLocator = model.Nodes.FindIndex(n => n.IsLocator);
            if (model.Nodes.Skip(firstLocator).Any(n => !n.IsLocator)) return; // locators are expected last
            var map = new int[model.Nodes.Count];
            var kept = new List<NodeData>();
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                var n = model.Nodes[i];
                if (n.IsLocator && n.LocatorSet != keep) { map[i] = -1; continue; }
                map[i] = kept.Count;
                kept.Add(n);
            }
            foreach (var n in kept) if (n.Parent >= 0) n.Parent = map[n.Parent];
            model.Nodes = kept;
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
    //     clips: { name: { duration, loop, rootSpeed (walk/run loops: their pinned root motion, model units/s),
    //                      events: [{type, time, duration, params}],
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
                if (a.RootSpeed > 0) w.Name("rootSpeed").Value(a.RootSpeed);
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
