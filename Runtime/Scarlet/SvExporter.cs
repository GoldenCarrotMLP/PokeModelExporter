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
    public sealed partial class SvExporter : IDisposable
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
            public Donor Donor;              // another model of the species to inherit from (a Mega's base form), see BorrowClips
        }

        // A model that lends what another lacks (owner 2026-10-19: Legends Z-A's Megas have no idle / sleep / emote
        // clips and a look-at authored to barely move; their base forms in Scarlet/Violet have them all): its clips
        // are read by bone name onto the other skeleton, its clip chains and flags come along, and its look-at
        // limits replace the other's for the same bones.
        public sealed class Donor
        {
            public string ModelName;
            public Func<string, byte[]> Read;    // a file of the donor's model folder
            public SvAnimController Controller;  // its chains, flags and look-at (null when the files are missing)
        }

        public Donor MakeDonor(SvCatalog.Entry entry)
        {
            string folder = entry.DataRoot + entry.Folder;
            Func<string, byte[]> read = file => Archive.Read(folder + file);
            string name = Path.GetFileNameWithoutExtension(entry.ModelPath);
            SvAnimController controller = null;
            try { controller = SvAnimController.Read(name, read); } catch (Exception) { controller = null; }
            return new Donor { ModelName = name, Read = read, Controller = controller };
        }

        public sealed class Result
        {
            public SvCatalog.Entry Entry;
            public string Folder;           // archive path of the model folder
            public ModelData Model;
            public SvConfig Config;
            public byte[] Glb;
            public string ActorManifest;    // JSON, see ActorManifest
            public SvAnimController Controller; // the face layers, state flags and look-at (null when the files are missing)
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
            SvAnimController controller = null;
            try { controller = SvAnimController.Read(model.Name, read); }
            catch (Exception) { controller = null; } // an unexpected controller layout: the model still exports
            // The face clips (eye01, mouth01) always come along: the face layer plays them over any body clip.
            if (options.Animations) LoadClips(model, read, options.Clips, options.MainClipSetOnly, controller?.Faces.Select(f => f.Clip));
            if (options.Animations && options.Donor != null) BorrowClips(model, options.Donor, options.MainClipSetOnly);
            if (options.Donor?.Controller?.LookAt != null && controller?.LookAt != null) InheritLookAt(controller.LookAt, options.Donor.Controller.LookAt);
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
                ActorManifest = ActorManifest.Write(model, entry, config, options.Shiny, controller, options.Donor?.Controller),
                Controller = controller,
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
            // Objects (chara/model_ob/ob0204_monsterball: the Poke Balls) keep their clips in the matching
            // chara/motion_ob/ folder (ob0204_00_00930_onceaction01_c.tranm: the ball opening).
            Func<string, byte[]> readClip = read;
            if (clips.Count == 0 && folder.Contains("/model_ob/"))
            {
                string motion = folder.Replace("/model_ob/", "/motion_ob/");
                var motionTracr = readArchive(motion + model.Name + ".tracr");
                if (motionTracr != null)
                {
                    clips = TrinityAnimationReader.ClipNamesFromResource(motionTracr);
                    readClip = f => readArchive(motion + f);
                }
            }
            foreach (var clipFile in clips)
            {
                var data = readClip(clipFile);
                if (data == null) continue;
                string stem = Path.GetFileNameWithoutExtension(clipFile);
                string clip = stem == model.Name ? "default" : stem.StartsWith(model.Name + "_", StringComparison.Ordinal) ? stem.Substring(model.Name.Length + 1) : stem;
                AnimationData anim;
                // A clip that doesn't parse (the berry of effect/battle_ee/ee006, ee006_nat.tranm, has no tracks) leaves
                // the model still.
                try { anim = TrinityAnimationReader.Read(data, clip); }
                catch (Exception) { continue; }
                var tracm = readClip(Path.ChangeExtension(clipFile, ".tracm"));
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

        // A piece cut out of a room modelled in one (the player's house field_graphic/common_model/a_t01_i01: its bed,
        // kitchen, sofa... each with its own materials in one merged mesh): the triangles of the named materials whose
        // middle is inside the box (the room model's metres), moved to stand on the origin (its footprint's middle at
        // x = z = 0, its lowest point at y = 0) and turned by `turn` degrees round +y (right-handed: 90 takes +x to -z), so
        // its back can be put at -z whichever wall it stood against. Null when the model isn't there or nothing is left.
        public static byte[] ExportRoomPiece(Func<string, byte[]> readArchive, string modelPath, ICollection<string> materials,
            System.Numerics.Vector3 boxMin, System.Numerics.Vector3 boxMax, float turn = 0)
        {
            int slash = modelPath.LastIndexOf('/');
            string folder = modelPath.Substring(0, slash + 1), file = modelPath.Substring(slash + 1);
            Func<string, byte[]> read = f => readArchive(folder + f);
            if (read(file) == null) return null;
            var model = TrinityModelReader.Read(file, read);
            var meshes = new List<MeshData>();
            foreach (var mesh in model.Meshes)
            {
                var map = new Dictionary<int, int>();
                var subs = new List<SubMeshData>();
                foreach (var sub in mesh.SubMeshes)
                {
                    if (!materials.Contains(sub.Material)) continue;
                    var kept = new List<int>();
                    for (int t = 0; t + 2 < sub.Indices.Length; t += 3)
                    {
                        var mid = (mesh.Positions[sub.Indices[t]] + mesh.Positions[sub.Indices[t + 1]] + mesh.Positions[sub.Indices[t + 2]]) / 3;
                        if (mid.X < boxMin.X || mid.Y < boxMin.Y || mid.Z < boxMin.Z || mid.X > boxMax.X || mid.Y > boxMax.Y || mid.Z > boxMax.Z) continue;
                        for (int k = 0; k < 3; k++)
                        {
                            int v = sub.Indices[t + k];
                            if (!map.TryGetValue(v, out int nv)) map[v] = nv = map.Count;
                            kept.Add(nv);
                        }
                    }
                    if (kept.Count > 0) subs.Add(new SubMeshData { Material = sub.Material, Indices = kept.ToArray() });
                }
                if (subs.Count == 0) continue;
                var order = new int[map.Count];
                foreach (var kv in map) order[kv.Value] = kv.Key;
                T[] Pick<T>(T[] all, int per = 1)
                {
                    if (all == null) return null;
                    var r = new T[order.Length * per];
                    for (int i = 0; i < order.Length; i++)
                        for (int k = 0; k < per; k++) r[i * per + k] = all[order[i] * per + k];
                    return r;
                }
                var cut = new MeshData
                {
                    Name = mesh.Name, Positions = Pick(mesh.Positions), Normals = Pick(mesh.Normals), Tangents = Pick(mesh.Tangents),
                    Colors = Pick(mesh.Colors), SubMeshes = subs,
                };
                foreach (var uv in mesh.TexCoords) cut.TexCoords.Add(Pick(uv));
                meshes.Add(cut);
            }
            if (meshes.Count == 0) return null;
            var lo = new System.Numerics.Vector3(float.MaxValue);
            var hi = new System.Numerics.Vector3(float.MinValue);
            foreach (var m in meshes)
                foreach (var v in m.Positions) { lo = System.Numerics.Vector3.Min(lo, v); hi = System.Numerics.Vector3.Max(hi, v); }
            var shift = new System.Numerics.Vector3(-(lo.X + hi.X) / 2, -lo.Y, -(lo.Z + hi.Z) / 2);
            var spin = System.Numerics.Matrix4x4.CreateRotationY(turn * (float)Math.PI / 180);
            foreach (var m in meshes)
            {
                for (int i = 0; i < m.Positions.Length; i++) m.Positions[i] = System.Numerics.Vector3.Transform(m.Positions[i] + shift, spin);
                if (m.Normals != null)
                    for (int i = 0; i < m.Normals.Length; i++) m.Normals[i] = System.Numerics.Vector3.TransformNormal(m.Normals[i], spin);
                if (m.Tangents != null)
                    for (int i = 0; i < m.Tangents.Length; i++)
                    {
                        var t = System.Numerics.Vector3.TransformNormal(new System.Numerics.Vector3(m.Tangents[i].X, m.Tangents[i].Y, m.Tangents[i].Z), spin);
                        m.Tangents[i] = new System.Numerics.Vector4(t, m.Tangents[i].W);
                    }
            }
            model.Meshes = meshes;
            model.Nodes.Clear();
            model.Materials = model.Materials.FindAll(m => materials.Contains(m.Name));
            var baked = SvMaterialBaker.Bake(model, read);
            return GlbWriter.Write(model, baked.Textures, m => baked.Mappings[m.Name], null);
        }

        // A room model's parts for choosing pieces (pokemodelexporter roomparts): per material, each lump of triangles
        // joined by shared corners, with its bounds (room model metres) and triangle count.
        public static List<(string material, System.Numerics.Vector3 min, System.Numerics.Vector3 max, int triangles)> RoomParts(Func<string, byte[]> readArchive, string modelPath)
        {
            int slash = modelPath.LastIndexOf('/');
            string folder = modelPath.Substring(0, slash + 1), file = modelPath.Substring(slash + 1);
            Func<string, byte[]> read = f => readArchive(folder + f);
            var model = TrinityModelReader.Read(file, read);
            var parts = new List<(string, System.Numerics.Vector3, System.Numerics.Vector3, int)>();
            foreach (var mesh in model.Meshes)
                foreach (var sub in mesh.SubMeshes)
                {
                    // Lumps: triangles sharing a corner (positions to the millimetre), by union-find.
                    var ids = new Dictionary<(long, long, long), int>();
                    var up = new List<int>();
                    int Id(int v)
                    {
                        var p = mesh.Positions[v];
                        var k = ((long)Math.Round(p.X * 1000), (long)Math.Round(p.Y * 1000), (long)Math.Round(p.Z * 1000));
                        if (!ids.TryGetValue(k, out int id)) { ids[k] = id = up.Count; up.Add(id); }
                        return id;
                    }
                    int Find(int x) { while (up[x] != x) { up[x] = up[up[x]]; x = up[x]; } return x; }
                    var tris = new List<int>();
                    for (int t = 0; t + 2 < sub.Indices.Length; t += 3)
                    {
                        int a = Id(sub.Indices[t]), b = Id(sub.Indices[t + 1]), c = Id(sub.Indices[t + 2]);
                        int ra = Find(a), rb = Find(b);
                        if (ra != rb) up[rb] = ra;
                        ra = Find(a); int rc = Find(c);
                        if (ra != rc) up[rc] = ra;
                        tris.Add(a);
                    }
                    var lumps = new Dictionary<int, (System.Numerics.Vector3 lo, System.Numerics.Vector3 hi, int n)>();
                    for (int t = 0, i = 0; t + 2 < sub.Indices.Length; t += 3, i++)
                    {
                        int root = Find(tris[i]);
                        var e = lumps.TryGetValue(root, out var x) ? x : (lo: new System.Numerics.Vector3(float.MaxValue), hi: new System.Numerics.Vector3(float.MinValue), n: 0);
                        for (int k = 0; k < 3; k++)
                        {
                            var p = mesh.Positions[sub.Indices[t + k]];
                            e.lo = System.Numerics.Vector3.Min(e.lo, p); e.hi = System.Numerics.Vector3.Max(e.hi, p);
                        }
                        e.n++;
                        lumps[root] = e;
                    }
                    foreach (var l in lumps.Values) parts.Add((sub.Material, l.lo, l.hi, l.n));
                }
            return parts;
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
        // always: clips kept whatever the set and filter say (the controller's face clips).
        public static void LoadClips(ModelData model, Func<string, byte[]> read, Func<string, bool> filter = null, bool mainSetOnly = false, IEnumerable<string> always = null)
        {
            var tracr = read(model.Name + "_base.tracr");
            if (tracr == null) return;
            var files = TrinityAnimationReader.ClipNamesFromResource(tracr);
            string ClipName(string file) => Path.GetFileNameWithoutExtension(file).Substring(model.Name.Length + 1); // 00001_battlewait01_loop
            char? set = mainSetOnly ? MainClipSet(files.Select(ClipName)) : null;
            var forced = new HashSet<string>(always ?? Enumerable.Empty<string>());
            foreach (var file in files)
            {
                string clip = ClipName(file);
                bool force = forced.Contains(clip);
                if (!force && set != null && clip[0] != set) continue;
                if (!force && filter != null && !filter(clip)) continue;
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

        // The part of a clip name after its number: 00000_defaultwait01_loop -> defaultwait01_loop (the games number the
        // same clips alike, but the part is what the slots and chains go by).
        public static string ClipPart(string name) => name.Length > 6 && char.IsDigit(name[0]) && name[5] == '_' ? name.Substring(6) : name;

        // Clips the model lacks, from the donor (its main clip set when mainSetOnly), read onto this skeleton by bone
        // name: tracks of bones this model doesn't have are dropped, and a clip that would lose more than 40% of its
        // tracks is left out (a different rig). Face clips stay the model's own (eyelid rigs differ), and so do the
        // donor's material / visibility tracks (other meshes). Borrowed clips keep the donor's names, so the donor's
        // chains (sleep01_start -> loop -> end) and flags apply to them (ActorManifest).
        public static void BorrowClips(ModelData model, Donor donor, bool mainSetOnly)
        {
            var tracr = donor.Read(donor.ModelName + "_base.tracr");
            if (tracr == null) return;
            var files = TrinityAnimationReader.ClipNamesFromResource(tracr);
            string ClipName(string file) => Path.GetFileNameWithoutExtension(file).Substring(donor.ModelName.Length + 1);
            char? set = mainSetOnly ? MainClipSet(files.Select(ClipName)) : null;
            var have = new HashSet<string>(model.Animations.Select(a => ClipPart(a.Name)));
            var bones = new HashSet<string>(model.Nodes.Select(n => n.Name));
            // Bones of the same name that rest differently (Mega Lucario's feelers are longer and set another way than
            // Lucario's) aren't the same part: the donor's clips don't move them (owner 2026-10-19: borrowed clips swung
            // the Mega's feelers like the base form's), they keep their own rest pose, and so do the bones under them.
            var mismatched = Mismatched(model, TrinityModelReader.ReadSkeletonOnly(donor.ModelName + ".trmdl", donor.Read));
            foreach (var file in files)
            {
                string clip = ClipName(file);
                if (set != null && clip[0] != set) continue;
                if (clip.StartsWith("08")) continue; // face clips
                if (have.Contains(ClipPart(clip))) continue;
                var data = donor.Read(file);
                if (data == null) continue;
                AnimationData anim;
                try { anim = TrinityAnimationReader.Read(data, clip); }
                catch (Exception) { continue; }
                int total = anim.Tracks.Count;
                anim.Tracks.RemoveAll(t => !bones.Contains(t.Bone));
                if (total == 0 || anim.Tracks.Count < total * 0.6f) continue; // a different rig
                anim.Tracks.RemoveAll(t => mismatched.Contains(t.Bone)); // parts that differ keep their own rest pose
                var traef = donor.Read(Path.ChangeExtension(file, ".traef"));
                if (traef != null) try { anim.Events = TrinityAnimationReader.ReadEvents(traef); } catch (Exception) { }
                PinRootMotion(anim);
                anim.Borrowed = true;
                model.Animations.Add(anim);
                have.Add(ClipPart(clip));
            }
        }

        // Bones whose rest pose differs from the donor's bone of the same name (turned over 20 degrees, a length off by a
        // third, or pointing over 25 degrees elsewhere), with everything under them.
        static HashSet<string> Mismatched(ModelData model, ModelData donor)
        {
            var theirs = new Dictionary<string, NodeData>();
            foreach (var n in donor.Nodes) theirs[n.Name] = n;
            // A chain of numbered segments that is longer here (Mega Lucario's feelers run to _06, Lucario's to _03) is
            // another part: none of it is moved by the donor's clips.
            string Family(string name) { int u = name.LastIndexOf('_'); return u > 0 && name.Substring(u + 1).All(char.IsDigit) ? name.Substring(0, u) : null; }
            Dictionary<string, int> Segments(ModelData m) => m.Nodes.Where(n => !n.IsLocator).Select(n => Family(n.Name)).Where(f => f != null)
                .GroupBy(f => f).ToDictionary(g => g.Key, g => g.Count());
            var mineCount = Segments(model);
            var theirCount = Segments(donor);
            var longer = new HashSet<string>(mineCount.Where(kv => theirCount.TryGetValue(kv.Key, out int t) && kv.Value > t).Select(kv => kv.Key));
            var bad = new HashSet<string>();
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                var n = model.Nodes[i];
                if (n.IsLocator) continue;
                bool parentBad = n.Parent >= 0 && n.Parent < model.Nodes.Count && bad.Contains(model.Nodes[n.Parent].Name);
                if (parentBad || Family(n.Name) is string family && longer.Contains(family)) { bad.Add(n.Name); continue; }
                if (!theirs.TryGetValue(n.Name, out var d)) continue;
                float dot = Math.Abs(System.Numerics.Quaternion.Dot(n.Rotation, d.Rotation));
                float turn = (float)(2 * Math.Acos(Math.Min(1, dot)) * 180 / Math.PI);
                float a = n.Translation.Length(), b = d.Translation.Length();
                bool length = a > 1e-3f && b > 1e-3f && (a / b > 1.33f || b / a > 1.33f);
                bool direction = a > 1e-3f && b > 1e-3f
                    && Math.Acos(Math.Max(-1, Math.Min(1, System.Numerics.Vector3.Dot(n.Translation / a, d.Translation / b)))) * 180 / Math.PI > 25;
                if (turn > 20 || length || direction) bad.Add(n.Name);
            }
            return bad;
        }

        // The donor's look-at limits for the bones both chains have (a Z-A Mega's head is authored to turn 10 degrees
        // and look 4 up; its base form's turns 30-40): the eyes stay the model's own.
        public static void InheritLookAt(SvAnimController.LookAtInfo target, SvAnimController.LookAtInfo donor)
        {
            foreach (var bone in target.Bones)
            {
                var from = donor.Bones.FirstOrDefault(b => b.Name == bone.Name);
                if (from == null) continue;
                bone.YawLeft = from.YawLeft;
                bone.YawRight = from.YawRight;
                bone.Up = from.Up;
                bone.Down = from.Down;
            }
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
        public static string Write(ModelData model, SvCatalog.Entry entry, SvConfig config, bool shiny, SvAnimController controller = null, SvAnimController donor = null)
        {
            var w = new JsonWriter();
            // A donor's chains and flags cover the clips borrowed from it (SvExporter.BorrowClips).
            var chains = new List<SvAnimController.Chain>(controller?.Chains ?? new List<SvAnimController.Chain>());
            if (donor != null)
                foreach (var c in donor.Chains) if (chains.All(x => x.Name != c.Name)) chains.Add(c);
            List<string> FlagsOf(string clip)
            {
                if (controller != null && controller.ClipFlags.TryGetValue(clip, out var own) && own.Count > 0) return own;
                if (donor != null && donor.ClipFlags.TryGetValue(clip, out var lent) && lent.Count > 0) return lent;
                return null;
            }
            w.BeginObject();
            w.Name("version").Value(1);
            // The animation controller's face layers and look-at (SvAnimController): poses as normalized times of the
            // face clip, held; blink segments; the look-at's bone and limits.
            if (controller != null)
            {
                w.Name("face").BeginObject();
                foreach (var f in controller.Faces)
                {
                    w.Name(f.Name).BeginObject().Name("clip").Value(f.Clip);
                    if (f.Parameter != null) w.Name("parameter").Value(f.Parameter);
                    if (f.Trigger != null) w.Name("trigger").Value(f.Trigger);
                    w.Name("poses").BeginArray();
                    foreach (var p in f.Poses) w.BeginObject().Name("name").Value(p.Name).Name("value").Value(p.Value).Name("time").Value(p.Time).EndObject();
                    w.EndArray();
                    w.Name("blink").BeginArray();
                    foreach (var b in f.Blink) w.BeginObject().Name("from").Value(b.From).Name("to").Value(b.To).Name("speed").Value(b.Speed).EndObject();
                    w.EndArray().EndObject();
                }
                w.EndObject();
                if (controller.LookAt is SvAnimController.LookAtInfo look)
                {
                    w.Name("lookAt").BeginObject().Name("bone").Value(look.Bone).Name("maxAngle").Value(look.MaxAngle)
                        .Name("speed").Value(look.Speed).Name("weight").Value(look.Weight).Name("eyeUv").Value(look.EyeUv);
                    w.Name("bones").BeginArray();
                    foreach (var b in look.Bones)
                        w.BeginObject().Name("name").Value(b.Name).Name("yawLeft").Value(b.YawLeft).Name("yawRight").Value(b.YawRight)
                            .Name("up").Value(b.Up).Name("down").Value(b.Down).Name("smooth").Value(b.Smooth).EndObject();
                    w.EndArray();
                    w.Name("eyes").BeginArray();
                    foreach (var e in look.Eyes)
                    {
                        w.BeginObject().Name("bone").Value(e.Bone).Name("mesh").Value(e.Mesh).Name("material").Value(e.Material)
                            .Name("yawLeft").Value(e.YawLeft).Name("yawRight").Value(e.YawRight).Name("up").Value(e.Up).Name("down").Value(e.Down).Name("smooth").Value(e.Smooth);
                        w.Name("offset").BeginArray(); foreach (var v in e.Offset) w.Value(v); w.EndArray();
                        w.Name("map").BeginArray(); foreach (var v in e.Map) w.Value(v); w.EndArray();
                        w.Name("tracks").BeginArray(); foreach (var t in e.Tracks) w.Value(t); w.EndArray();
                        w.EndObject();
                    }
                    w.EndArray().EndObject();
                }
            }
            // The body machine's clip chains (start -> loop -> end), only those whose clips were exported.
            if (chains.Count > 0)
            {
                var exported = new HashSet<string>(model.Animations.Select(a => a.Name));
                w.Name("chains").BeginObject();
                foreach (var c in chains)
                {
                    if (!exported.Contains(c.Start) || !exported.Contains(c.Loop)) continue;
                    w.Name(c.Name).BeginObject().Name("start").Value(c.Start).Name("loop").Value(c.Loop);
                    if (c.End != null && exported.Contains(c.End)) w.Name("end").Value(c.End).Name("blendOut").Value(c.BlendOut);
                    w.EndObject();
                }
                w.EndObject();
            }
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
                if (a.Borrowed) w.Name("borrowed").Value(true);
                if (FlagsOf(a.Name) is List<string> flags)
                {
                    w.Name("flags").BeginArray();
                    foreach (var f in flags) w.Value(f);
                    w.EndArray();
                }
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
