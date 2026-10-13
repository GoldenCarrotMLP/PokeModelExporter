// PokeModelExporter command-line tool. Reads a player's own extracted game files (Scarlet/Violet
// RomFS for now) and exports Pokemon models as GLB (+ JSON sidecar), plus inspection commands.
//   pokemodelexporter glb <national dex> [form] [gender] [--shiny] [--lods] --out <file.glb>
//   pokemodelexporter info | catalog [dex...] | model <dex> [--params] | events <dex> | audit <dex> | ...
// Game files: --game <folder with arc/data.trpfd> (default Documents/PokeSim/Games/scarlet-violet).
// Oodle: --oodle <oo2core_*_win64.dll> (default: the newest in the game folder's parent).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PokeModelExporter.Formats;
using PokeModelExporter.Export;
using PokeModelExporter.Model;
using PokeModelExporter.Scarlet;

string games = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PokeSim", "Games");
string gameFolder = Path.Combine(games, "scarlet-violet");
string oodlePath = Oodle.FindDll(games);
var rest = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--game") gameFolder = args[++i];
    else if (args[i] == "--oodle") oodlePath = args[++i];
    else rest.Add(args[i]);
}
if (rest.Count == 0)
{
    Console.WriteLine("commands: info | packs <text> | pack <pack name> | has <path>...");
    return 1;
}

if (rest[0] == "texture") // texture <file.bntx> <out.png> [--normal]: decode a local BNTX file (no game folder needed)
{
    var tex = Bntx.Read(File.ReadAllBytes(rest[1]), normalMap: rest.Contains("--normal"));
    File.WriteAllBytes(rest[2], Png.Encode(tex.Rgba, tex.Width, tex.Height));
    Console.WriteLine($"{tex.Width}x{tex.Height} format 0x{tex.Format:X4} -> {rest[2]}");
    return 0;
}

if (rest[0] == "audio") // audio <bank> <EVENT_NAME>... --out <dir> [--switch INDEX,FORM00] [--vgmstream <vgmstream-cli.exe>]: an event's sounds as .wem (+ .wav)
{
    string folder = PokeModelExporter.Wwise.WwiseSounds.FindFolder(gameFolder);
    if (folder == null) { Console.Error.WriteLine($"No audio/wwise/bin/Switch/Init.bnk under {gameFolder}"); return 1; }
    int o = rest.IndexOf("--out"), v = rest.IndexOf("--vgmstream"), sw = rest.IndexOf("--switch");
    var switches = sw >= 0 ? rest[sw + 1].Split(',') : new string[0];
    string outDir = Path.GetFullPath(o >= 0 ? rest[o + 1] : "audio");
    Directory.CreateDirectory(outDir);
    var sounds = new PokeModelExporter.Wwise.WwiseSounds(folder);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 2; i < rest.Count && !rest[i].StartsWith("--"); i++)
    {
        var media = sounds.Resolve(rest[1], rest[i], switches);
        Console.WriteLine($"{rest[i]} (id {PokeModelExporter.Wwise.WwiseSounds.Hash(rest[i])}): {media.Count} sound(s)");
        foreach (var m in media)
        {
            string wem = Path.Combine(outDir, $"{rest[i]}_{m.Id}.wem");
            File.WriteAllBytes(wem, sounds.Read(m));
            Console.WriteLine($"  media {m.Id} in {m.Source} @ {m.Offset}, {m.Size} bytes -> {Path.GetFileName(wem)}");
            if (v < 0) continue;
            var run = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(rest[v + 1], $"-o \"{Path.ChangeExtension(wem, ".wav")}\" \"{wem}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            run.StandardOutput.ReadToEnd();
            run.WaitForExit();
            Console.WriteLine($"  vgmstream exit {run.ExitCode} -> {Path.GetFileName(Path.ChangeExtension(wem, ".wav"))}");
        }
    }
    Console.WriteLine($"{watch.ElapsedMilliseconds} ms");
    return 0;
}

string arc = SvExporter.FindArc(gameFolder);
if (arc == null) { Console.Error.WriteLine($"No arc/data.trpfd under {gameFolder}"); return 1; }
if (oodlePath == null) { Console.Error.WriteLine($"No oo2core_*_win64.dll in {games} (README step 5b) and no --oodle given"); return 1; }

using var oodle = new Oodle(oodlePath);
using var archive = new TrinityArchive(arc, oodle);

switch (rest[0])
{
    case "actor": // actor <dex> [form] [gender] [--shiny] --out <dir>: model.glb + actor.json as a game imports them
    {
        int o = rest.IndexOf("--out");
        string outDir = Path.GetFullPath(o >= 0 ? rest[o + 1] : "actor");
        using var exporter = new SvExporter(arc, oodlePath);
        int species = int.Parse(rest[1]);
        int form = rest.Count > 2 && int.TryParse(rest[2], out int f) ? f : 0;
        int gender = rest.Count > 3 && int.TryParse(rest[3], out int g) ? g : 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = exporter.Export(species, form, gender, new SvExporter.Options { Shiny = rest.Contains("--shiny"), ExtraImages = false });
        if (result == null) { Console.Error.WriteLine($"#{species} is not in this game's catalog"); return 1; }
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, "model.glb"), result.Glb);
        File.WriteAllText(Path.Combine(outDir, "actor.json"), result.ActorManifest);
        Console.WriteLine($"{result.Folder}: {result.Glb.Length} bytes, {result.Model.Animations.Count} clips, size {result.Config?.SizeName}, {watch.ElapsedMilliseconds} ms -> {outDir}");
        break;
    }

    case "info":
        Console.WriteLine($"arc:   {arc}");
        Console.WriteLine($"oodle: {oodlePath}");
        Console.WriteLine($"files: {archive.FileCount}, packs: {archive.PackNames.Count}, packs found in data.trpfs by FNV-1a 64: {archive.ResolvablePacks()}");
        foreach (var p in archive.PackNames.Take(8)) Console.WriteLine("  " + p);
        break;

    case "packs":
        foreach (var p in archive.PackNames.Where(p => p.Contains(rest[1], StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine(p);
        break;

    case "has": // which of these paths exist (hashed with FNV-1a 64)
        foreach (var path in rest.Skip(1))
            Console.WriteLine($"{(archive.Contains(path) ? "yes" : " - ")}  {path}  {(archive.Contains(path) ? "(pack " + archive.PackOf(TrinityArchive.Hash(path)) + ")" : "")}");
        break;

    case "scan": // every pokemon/data/pmNNNN/pmNNNN_FF_VV/pmNNNN_FF_VV.trmdl that exists
    {
        int found = 0;
        for (int n = 0; n <= 1200; n++)
            for (int f = 0; f < 64; f++)
                for (int v = 0; v < 4; v++)
                {
                    string id = $"pm{n:0000}_{f:00}_{v:00}";
                    string path = $"pokemon/data/pm{n:0000}/{id}/{id}.trmdl";
                    if (!archive.Contains(path)) continue;
                    Console.WriteLine(id);
                    found++;
                }
        Console.Error.WriteLine($"{found} models");
        break;
    }

    case "unnamed": // files in the archive that a hash list doesn't name
    {
        var known = new HashSet<ulong>();
        foreach (var line in File.ReadLines(rest[1]))
            if (line.Length > 18 && ulong.TryParse(line.AsSpan(2, 16), System.Globalization.NumberStyles.HexNumber, null, out ulong h)) known.Add(h);
        var packs = new Dictionary<string, int>();
        int unnamed = 0;
        foreach (ulong h in archive.AllFileHashes())
        {
            if (known.Contains(h)) continue;
            unnamed++;
            string pack = archive.PackOf(h);
            packs[pack] = packs.TryGetValue(pack, out int c) ? c + 1 : 1;
        }
        Console.WriteLine($"{known.Count} names in list, {archive.FileCount} files in archive, {unnamed} unnamed");
        foreach (var kv in packs.OrderByDescending(kv => kv.Value).Take(15)) Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
        break;
    }

    case "extract": // extract <path> <output file>
    {
        var data = archive.Read(rest[1]);
        if (data == null) { Console.Error.WriteLine("not found: " + rest[1]); return 1; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(rest[2])));
        File.WriteAllBytes(rest[2], data);
        Console.WriteLine($"{data.Length} bytes -> {rest[2]}");
        break;
    }

    case "catalog": // catalog [species...]: the game's species -> model table (pokemon/catalog/.../poke_resource_table.trpmcatalog)
    {
        var cat = FbTable.Root(archive.Read("pokemon/catalog/catalog/poke_resource_table.trpmcatalog"));
        var entries = cat.Vector(1);
        var wanted = new HashSet<int>(rest.Skip(1).Select(int.Parse));
        Console.Error.WriteLine($"{entries.Length} catalog entries");
        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries.Table(i);
            var sp = e.Table(0);
            int species = sp.U16(0), form = sp.U16(1), gender = sp.U8(2);
            if (wanted.Count > 0 && !wanted.Contains(species)) continue;
            Console.WriteLine($"#{species:0000} form {form} gender {gender}: {e.String(1)}  ({e.Vector(4).Length} anim sets)");
            if (wanted.Count > 0)
            {
                var anims = e.Vector(4); for (int k = 0; k < anims.Length; k++) Console.WriteLine($"     anim [{anims.Table(k).I16(0)}] {anims.Table(k).String(1)}");
                var locs = e.Vector(5); for (int k = 0; k < locs.Length; k++) Console.WriteLine($"     locator [{locs.Table(k).I16(0)}/{locs.Table(k).U8(1)}] {locs.Table(k).String(2)}");
                Console.WriteLine($"     material table {e.String(2)}, config {e.String(3)}, icon {e.String(6)}");
            }
        }
        break;
    }

    case "model": // model <national dex> [form] [gender] [--shiny]: load and summarize a Pokemon model
    {
        var model = LoadModel(archive, rest);
        Console.WriteLine($"model {model.Name}: {model.Nodes.Count} nodes ({model.Nodes.Count(n => n.SkinIndex >= 0)} skinned), {model.Meshes.Count} meshes, {model.Materials.Count} materials");
        foreach (var m in model.Meshes)
            Console.WriteLine($"  mesh {m.Name}: {m.Positions?.Length} verts, normals {m.Normals != null}, tangents {m.Tangents != null}, uv layers {m.TexCoords.Count}, colors {m.Colors != null}, skin {m.Joints != null}, " +
                string.Join(", ", m.SubMeshes.Select(sm => $"{sm.Material}:{sm.Indices.Length / 3} tris")));
        foreach (var mat in model.Materials)
        {
            Console.WriteLine($"  material {mat.Name} [{mat.Shader}] " + string.Join(" ", mat.Textures.Select(t => $"{t.Key}={t.Value}")));
            if (rest.Contains("--params"))
            {
                foreach (var o2 in mat.ShaderOptions) Console.WriteLine($"      option {o2.Key} = {o2.Value}");
                foreach (var c in mat.Colors) Console.WriteLine($"      color  {c.Key} = ({c.Value.X:0.###}, {c.Value.Y:0.###}, {c.Value.Z:0.###}, {c.Value.W:0.###})");
                foreach (var f2 in mat.Floats) Console.WriteLine($"      float  {f2.Key} = {f2.Value:0.###}");
            }
        }
        var root = model.Nodes.Count > 0 ? model.Nodes[0] : null;
        var allPos = model.Meshes.Where(m => m.Positions != null).SelectMany(m => m.Positions).ToList();
        if (allPos.Count > 0)
            Console.WriteLine($"  bounds: min ({allPos.Min(p => p.X):0.###}, {allPos.Min(p => p.Y):0.###}, {allPos.Min(p => p.Z):0.###}) max ({allPos.Max(p => p.X):0.###}, {allPos.Max(p => p.Y):0.###}, {allPos.Max(p => p.Z):0.###})");
        break;
    }

    case "glb": // glb <dex> [form] [gender] [--shiny] [--lods] [--no-anims] [--no-sidecar] [--hashes <list>] [--schemas <dir>] --out <file.glb>
    {
        int o = rest.IndexOf("--out");
        string outFile = Path.GetFullPath(o >= 0 ? rest[o + 1] : "model.glb");
        var catalog = new SvCatalog(archive.Read(SvCatalog.Path));
        var entry = FindEntry(catalog, archive, rest);
        var model = LoadModelFrom(archive, rest, out string folder);
        string dir = folder;
        Func<string, byte[]> read = file => archive.Read(dir + file);
        if (!rest.Contains("--no-anims")) LoadClips(archive, model, dir, rest);
        var folderFiles = FolderFiles(rest, folder);

        // Textures: plain maps decoded, layered materials (eyes, flames...) baked to color/emission;
        // everything else the materials, icons or folder hold kept as unreferenced images.
        var baked = SvMaterialBaker.Bake(model, read);
        var extraNames = new List<string>();
        foreach (var mtr in new[] { "", "_rare", "_edit" }.Select(x => model.Name + x + ".trmtr"))
        {
            var data = read(mtr);
            if (data == null) continue;
            var mats = FbTable.Root(data).Vector(1);
            for (int i = 0; i < mats.Length; i++) { var tx = mats.Table(i).Vector(2); for (int k = 0; k < tx.Length; k++) extraNames.Add(tx.Table(k).String(1)); }
        }
        extraNames.Add(Path.GetFileName(entry.IconPath));
        extraNames.Add(Path.GetFileNameWithoutExtension(entry.IconPath) + "_big.bntx");
        if (folderFiles != null) extraNames.AddRange(folderFiles.Where(f => f.EndsWith(".bntx")));
        var extra = SvMaterialBaker.ExtraTextures(extraNames, read, baked);

        int dump = rest.IndexOf("--dump-textures");
        if (dump >= 0)
        {
            Directory.CreateDirectory(rest[dump + 1]);
            foreach (var kv in baked.Textures.Concat(extra)) File.WriteAllBytes(Path.Combine(rest[dump + 1], kv.Key.Replace(".bntx", ".png").Replace('/', '_')), kv.Value);
            foreach (var kv in baked.Mappings) Console.WriteLine($"  {kv.Key}: color={kv.Value.BaseColorTexture} emission={kv.Value.EmissiveTexture} unlit={kv.Value.Unlit}");
        }
        var glb = GlbWriter.Write(model, baked.Textures, m => baked.Mappings[m.Name], extra);
        Directory.CreateDirectory(Path.GetDirectoryName(outFile));
        File.WriteAllBytes(outFile, glb);
        Console.WriteLine($"{glb.Length} bytes -> {outFile}  ({extra.Count} extra images)");

        // Lower-detail meshes, only on request: same skeleton and materials, no clips.
        if (rest.Contains("--lods"))
        {
            int lods = TrinityModelReader.LodCount(archive.Read(SvCatalog.DataRoot + entry.ModelPath));
            for (int lod = 1; lod < lods; lod++)
            {
                var lodModel = TrinityModelReader.Read(Path.GetFileName(entry.ModelPath), read, shiny: rest.Contains("--shiny"), lod: lod, locatorFiles: entry.LocatorFiles());
                var lodBaked = SvMaterialBaker.Bake(lodModel, read);
                string lodFile = Path.Combine(Path.GetDirectoryName(outFile), Path.GetFileNameWithoutExtension(outFile) + $"_lod{lod}.glb");
                var lodGlb = GlbWriter.Write(lodModel, lodBaked.Textures, m => lodBaked.Mappings[m.Name]);
                File.WriteAllBytes(lodFile, lodGlb);
                Console.WriteLine($"{lodGlb.Length} bytes -> {lodFile}");
            }
        }

        // Sidecar: what glTF can't hold.
        if (!rest.Contains("--no-sidecar"))
        {
            var schemas = new SchemaCache(rest);
            string json = SvSidecar.Build(entry, "scarlet-violet", read, schemas.Get, folderFiles);
            string sidecar = Path.ChangeExtension(outFile, ".json");
            File.WriteAllText(sidecar, json);
            Console.WriteLine($"{json.Length} chars -> {sidecar}");
        }
        break;
    }

    case "bones": // bones <national dex> [names...]: stored bone matrices vs. our computed bind pose
    {
        var catalog = new SvCatalog(archive.Read(SvCatalog.Path));
        var entry = FindEntry(catalog, archive, rest);
        string folder = SvCatalog.DataRoot + entry.Folder;
        var mdl = FbTable.Root(archive.Read(SvCatalog.DataRoot + entry.ModelPath));
        var skl = FbTable.Root(archive.Read(folder + mdl.Table(2).String(0)));
        var nodes = skl.Vector(1);
        var bones = skl.Vector(2);
        Console.WriteLine($"{nodes.Length} nodes, {bones.Length} bones, rig_offset {skl.U32(4)}");
        // Check: our bind pose (rest transforms, Euler XYZ) inverted vs. the stored inverse bind matrices.
        var model = LoadModel(archive, rest.Take(2).ToList());
        var world = new System.Numerics.Matrix4x4[model.Nodes.Count];
        for (int i = 0; i < model.Nodes.Count; i++)
        {
            var nd = model.Nodes[i];
            var local = System.Numerics.Matrix4x4.CreateScale(nd.Scale) * System.Numerics.Matrix4x4.CreateFromQuaternion(nd.Rotation) * System.Numerics.Matrix4x4.CreateTranslation(nd.Translation);
            world[i] = nd.Parent >= 0 ? local * world[nd.Parent] : local; // parents come first in the node list
        }
        float worst = 0; string worstName = null;
        for (int i = 0; i < model.Nodes.Count; i++)
        {
            int rig = model.Nodes[i].SkinIndex;
            if (rig < 0 || rig >= bones.Length) continue;
            System.Numerics.Matrix4x4.Invert(world[i], out var ours);
            var mt = bones.Table(rig).Table(2);
            var b = mt.Buffer;
            float[] stored = new float[12];
            for (int c = 0; c < 4; c++) { int p = mt.InlineStruct(c); for (int k = 0; k < 3; k++) stored[c * 3 + k] = p == 0 ? 0 : BitConverter.ToSingle(b, p + 4 * k); }
            // stored columns x,y,z,w = rows 1..4 of a row-vector matrix (rotation rows + translation row)
            float[] mine = { ours.M11, ours.M12, ours.M13, ours.M21, ours.M22, ours.M23, ours.M31, ours.M32, ours.M33, ours.M41, ours.M42, ours.M43 };
            float diff = 0;
            for (int k = 0; k < 12; k++) diff = Math.Max(diff, Math.Abs(mine[k] - stored[k]));
            if (diff > worst) { worst = diff; worstName = model.Nodes[i].Name; }
        }
        Console.WriteLine($"bind pose check: largest difference {worst:0.######} ({worstName})");
        var filter = rest.Skip(2).ToList();
        for (int i = 0; i < nodes.Length; i++)
        {
            var n = nodes.Table(i);
            string name = n.String(0);
            int rig = n.I32(5, -1), parent = n.I32(4, -1);
            if (filter.Count > 0 && !filter.Any(f => name.Contains(f))) continue;
            string m = "";
            if (rig >= 0 && rig < bones.Length)
            {
                var mt = bones.Table(rig).Table(2);
                var b = mt.Buffer;
                for (int c = 0; c < 4; c++)
                {
                    int p = mt.InlineStruct(c);
                    m += p == 0 ? " [-]" : $" [{BitConverter.ToSingle(b, p):0.###} {BitConverter.ToSingle(b, p + 4):0.###} {BitConverter.ToSingle(b, p + 8):0.###}]";
                }
            }
            Console.WriteLine($"{i,3} {name,-28} parent {parent,3} rig {rig,3}{m}");
        }
        break;
    }

    case "materials": // materials <file.trmtr path in the archive>: dump every material in one material file
    {
        var tmp = new ModelData();
        var data = archive.Read(rest[1]) ?? throw new FileNotFoundException(rest[1]);
        var mtr = FbTable.Root(data);
        var mats = mtr.Vector(1);
        for (int i = 0; i < mats.Length; i++)
        {
            var m = mats.Table(i);
            Console.WriteLine($"material {m.String(0)}");
            var sh = m.Vector(1);
            for (int k = 0; k < sh.Length; k++)
            {
                Console.WriteLine($"  shader {sh.Table(k).String(0)}");
                var vals = sh.Table(k).Vector(1);
                for (int v = 0; v < vals.Length; v++) Console.WriteLine($"    {vals.Table(v).String(0)} = {vals.Table(v).String(1)}");
            }
            var tx = m.Vector(2);
            for (int k = 0; k < tx.Length; k++) Console.WriteLine($"  texture {tx.Table(k).String(0)} = {tx.Table(k).String(1)}");
            var c4 = m.Vector(7);
            for (int k = 0; k < c4.Length; k++)
            {
                var e = c4.Table(k); int p = e.InlineStruct(1); var b = e.Buffer;
                var col = p == 0 ? "-" : $"({BitConverter.ToSingle(b, p):0.###}, {BitConverter.ToSingle(b, p + 4):0.###}, {BitConverter.ToSingle(b, p + 8):0.###}, {BitConverter.ToSingle(b, p + 12):0.###})";
                if (col != "(0, 0, 0, 0)") Console.WriteLine($"  color {e.String(0)} = {col}");
            }
        }
        break;
    }

    case "events": // events <national dex> [--clip part]: every clip's notifies (cries, sounds, VFX triggers)
    {
        var model = LoadModelFrom(archive, rest, out string folder);
        LoadClips(archive, model, folder, rest);
        foreach (var anim in model.Animations)
        {
            Console.WriteLine($"{anim.Name}  ({anim.FrameCount} frames @ {anim.FramesPerSecond} fps{(anim.Loop ? ", loop" : "")})");
            foreach (var e in anim.Events.OrderBy(e => e.StartFrame))
                Console.WriteLine($"   frame {e.StartFrame,4}{(e.DurationFrames > 0 ? $"-{e.StartFrame + e.DurationFrames}" : ""),-6} {e.Type,-32} " +
                    string.Join("  ", e.Parameters.Where(p => p.Value.Length > 0).Select(p => $"{p.Key}={p.Value}")));
        }
        break;
    }

    case "audit": // audit <national dex> --hashes <hashes_inside_fd.txt>: what the converter uses vs. leaves out
    {
        var model = LoadModelFrom(archive, rest, out string folder);
        LoadClips(archive, model, folder, rest);
        int h = rest.IndexOf("--hashes");
        string hashes = h >= 0 ? rest[h + 1] : Path.Combine("External", "hashlists", "scarlet-violet", "hashes_inside_fd.txt");
        var files = File.ReadLines(hashes).Select(l => l.Length > 19 ? l.Substring(19) : "").Where(f => f.StartsWith(folder)).Select(f => f.Substring(folder.Length)).OrderBy(f => f).ToList();
        Console.WriteLine($"{files.Count} files in {folder}");
        var used = new Dictionary<string, string>
        {
            [".trmdl"] = "converted", [".trmsh"] = "converted (LOD0 only)", [".trmbf"] = "converted (LOD0 only)",
            [".trskl"] = "converted (main skeleton + effect locators)", [".trmtr"] = "converted (normal / --shiny; _edit not)",
            [".bntx"] = "partly (see textures)", [".tranm"] = "converted", [".traef"] = "converted (notifies)",
            [".tracr"] = "clip names only", [".tracm"] = "converted (material / visibility / blend-shape tracks)",
        };
        foreach (var g in files.GroupBy(f => Path.GetExtension(f)).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key,-10} {g.Count(),3}  {(used.TryGetValue(g.Key, out var u) ? u : "NOT converted")}");

        // Animation tracks whose bone isn't in the skeleton.
        var nodeNames = new HashSet<string>(model.Nodes.Select(n => n.Name));
        var missing = model.Animations.SelectMany(a => a.Tracks).Select(t => t.Bone).Where(b => b != null && !nodeNames.Contains(b)).Distinct().ToList();
        Console.WriteLine($"animation tracks: {model.Animations.Sum(a => a.Tracks.Count)} in {model.Animations.Count} clips; bones not in skeleton: {(missing.Count == 0 ? "none" : string.Join(", ", missing))}");

        // Textures referenced by materials vs. used in the GLB.
        var mapped = new HashSet<string>();
        foreach (var m in model.Materials) { var mm = MaterialMapping.Default(m); foreach (var t in new[] { mm.BaseColorTexture, mm.NormalTexture }) if (t != null) mapped.Add(t); }
        foreach (var m in model.Materials)
            foreach (var kv in m.Textures)
            {
                string use = kv.Key switch
                {
                    "BaseColorMap" or "NormalMap" or "RoughnessMap" or "AOMap" or "MetallicMap" or "LayerMaskMap" => "used",
                    _ => "NOT used (kept in extras)",
                };
                Console.WriteLine($"  {m.Name,-10} {kv.Key,-16} {kv.Value,-40} {use}");
            }

        // Mesh-level data the reader skips.
        var mdl = FbTable.Root(archive.Read(SvCatalog.DataRoot + new SvCatalog(archive.Read(SvCatalog.Path)).Find(int.Parse(rest[1])).ModelPath));
        Console.WriteLine($"LOD mesh files: {mdl.Vector(1).Length} (exported: 1)");
        var msh = FbTable.Root(archive.Read(folder + mdl.Vector(1).Table(0).String(0)));
        var shapes = msh.Vector(1);
        for (int i = 0; i < shapes.Length; i++)
        {
            var sh = shapes.Table(i);
            int morphs = sh.Vector(14).IsNull ? 0 : sh.Vector(14).Length, vis = sh.Vector(11).IsNull ? 0 : sh.Vector(11).Length;
            if (morphs > 0 || vis > 0) Console.WriteLine($"  mesh {sh.String(0)}: {morphs} morph shapes, {vis} visibility shapes (NOT exported)");
        }
        break;
    }

    case "pack":
        foreach (var h in archive.FilesInPack(rest[1]))
            Console.WriteLine($"{h:x16}");
        break;

    default:
        Console.Error.WriteLine("unknown command " + rest[0]);
        return 1;
}
return 0;

static ModelData LoadModel(TrinityArchive archive, List<string> rest) => LoadModelFrom(archive, rest, out _);

static ModelData LoadModelFrom(TrinityArchive archive, List<string> rest, out string folder)
{
    var catalog = new SvCatalog(archive.Read(SvCatalog.Path));
    var entry = FindEntry(catalog, archive, rest);
    folder = SvCatalog.DataRoot + entry.Folder;
    Console.Error.WriteLine($"#{rest[1]} -> {folder}");
    string dir = folder;
    return TrinityModelReader.Read(entry.ModelPath.Substring(entry.Folder.Length), name => archive.Read(dir + name),
        shiny: rest.Contains("--shiny"), locatorFiles: entry.LocatorFiles());
}

// All skeletal clips listed in the model folder's animation resource file (*.tracr).
static void LoadClips(TrinityArchive archive, ModelData model, string folder, List<string> rest)
{
    int only = rest.IndexOf("--clip");
    SvExporter.LoadClips(model, file => archive.Read(folder + file), only >= 0 ? clip => clip.Contains(rest[only + 1]) : (Func<string, bool>)null);
    Console.Error.WriteLine($"{model.Animations.Count} clips");
}

// National Dex number -> the game's model number (they differ for most of Gen 9) -> catalog entry.
static SvCatalog.Entry FindEntry(SvCatalog catalog, TrinityArchive archive, List<string> rest)
{
    int species = int.Parse(rest[1]);
    int form = rest.Count > 2 && int.TryParse(rest[2], out int f) ? f : 0;
    int gender = rest.Count > 3 && int.TryParse(rest[3], out int g) ? g : 0;
    var personal = new SvPersonal(archive.Read(SvPersonal.Path));
    return catalog.Find(personal.CatalogSpecies(species, form), form, gender) ?? throw new InvalidOperationException($"#{species} is not in this game's catalog");
}

// Every file of the model folder, from a hash list (--hashes <hashes_inside_fd.txt>), relative to the folder.
static List<string> FolderFiles(List<string> rest, string folder)
{
    int h = rest.IndexOf("--hashes");
    if (h < 0) return null;
    return File.ReadLines(rest[h + 1]).Select(l => l.Length > 19 ? l.Substring(19) : "")
        .Where(f => f.StartsWith(folder)).Select(f => f.Substring(folder.Length)).OrderBy(f => f).ToList();
}

// PokeDocs schemas (no license; not bundled): read from --schemas <dir> (a PokeDocs SV/Flatbuffers folder),
// else downloaded once from GitHub into %LOCALAPPDATA%/PokeModelExporter/schemas/SV.
sealed class SchemaCache
{
    const string Base = "https://raw.githubusercontent.com/pkZukan/PokeDocs/HEAD/SV/Flatbuffers/";
    readonly string _dir;
    readonly bool _download;
    readonly Dictionary<string, FbsSchema> _parsed = new Dictionary<string, FbsSchema>();

    public SchemaCache(List<string> rest)
    {
        int s = rest.IndexOf("--schemas");
        _download = s < 0;
        _dir = s >= 0 ? rest[s + 1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PokeModelExporter", "schemas", "SV");
    }

    public FbsSchema Get(string extension)
    {
        if (_parsed.TryGetValue(extension, out var cached)) return cached;
        FbsSchema schema = null;
        if (SvSidecar.SchemaPaths.TryGetValue(extension, out var rel))
        {
            string file = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file) && _download)
            {
                try
                {
                    using var http = new System.Net.Http.HttpClient();
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("PokeModelExporter");
                    var text = http.GetStringAsync(Base + rel).GetAwaiter().GetResult();
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllText(file, text);
                }
                catch (Exception e) { Console.Error.WriteLine($"schema {rel}: {e.Message}"); }
            }
            if (File.Exists(file))
            {
                try { schema = FbsSchema.Parse(File.ReadAllText(file)); }
                catch (Exception e) { Console.Error.WriteLine($"schema {rel}: {e.Message}"); }
            }
        }
        return _parsed[extension] = schema;
    }
}
