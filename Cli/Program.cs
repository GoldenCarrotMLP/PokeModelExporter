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

    case "extract-many": // extract-many <list file of archive paths> <output dir>: each file under its own name
    {
        Directory.CreateDirectory(rest[2]);
        int n = 0;
        foreach (var path in File.ReadAllLines(rest[1]).Where(l => l.Trim().Length > 0))
        {
            var data = archive.Read(path.Trim());
            if (data == null) continue;
            File.WriteAllBytes(Path.Combine(rest[2], Path.GetFileName(path.Trim())), data);
            n++;
        }
        Console.WriteLine($"{n} files -> {rest[2]}");
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

    case "personal": // personal [national]: every species/form of the game's personal table (raw fields for research), tab-separated
    {
        var entries = FbTable.Root(archive.Read(SvPersonal.Path)).Vector(0);
        var catalog = new SvCatalog(archive.Read(SvCatalog.Path));
        var models = new HashSet<(int, int)>(catalog.Entries.Select(c => (c.Species, c.Form)));
        Console.WriteLine("national	form	internal	present	model	stats	field:u8/u16 for f3..f32");
        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries.Table(i);
            int p = e.InlineStruct(0);
            if (p == 0) continue;
            var b = e.Buffer;
            int index = BitConverter.ToUInt16(b, p), form = BitConverter.ToUInt16(b, p + 2), national = BitConverter.ToUInt16(b, p + 4);
            if (rest.Count > 1 && rest[1] != national.ToString()) continue;
            if (rest.Count > 1) // personal <national>: each field's position and its first 8 bytes
                for (int f = 0; f < 40; f++)
                    if (e.FieldPosition(f) is int fp && fp != 0)
                        Console.WriteLine($"  form {form} field {f} @ {fp}: {BitConverter.ToString(b, fp, Math.Min(8, b.Length - fp))}");
            int s = e.FieldPosition(22);
            string stats = s == 0 ? "-" : string.Join("/", Enumerable.Range(0, 6).Select(k => b[s + k]));
            string raw = string.Join(" ", Enumerable.Range(3, 30).Select(f => e.FieldPosition(f) == 0 ? $"{f}:-" : $"{f}:{e.U8(f)}/{e.U16(f)}"));
            Console.WriteLine($"{national}	{form}	{index}	{(e.Bool(1) ? 1 : 0)}	{(models.Contains((index, form)) ? 1 : 0)}	{stats}	{raw}");
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

    case "glb": // glb <dex> [form] [gender] [--shiny] [--lods] [--no-anims] [--no-ssc] [--no-sidecar] [--hashes <list>] [--schemas <dir>] --out <file.glb>
    {
        int o = rest.IndexOf("--out");
        string outFile = Path.GetFullPath(o >= 0 ? rest[o + 1] : "model.glb");
        var catalog = new SvCatalog(archive.Read(SvCatalog.Path));
        var entry = FindEntry(catalog, archive, rest);
        var model = LoadModelFrom(archive, rest, out string folder);
        string dir = folder;
        Func<string, byte[]> read = file => archive.Read(dir + file);
        if (!rest.Contains("--no-anims")) LoadClips(archive, model, dir, rest);
        if (!rest.Contains("--no-ssc")) Console.Error.WriteLine($"scale compensation: {ScaleCompensation.Apply(model)} helper nodes");
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

    case "locators": // locators <national dex>: every effect-locator node (name, local parent, effect_node bone, transform)
    {
        var catalog = new SvCatalog(archive.Read(SvCatalog.Path));
        var entry = FindEntry(catalog, archive, rest);
        foreach (var file in entry.LocatorFiles())
        {
            var data = archive.Read(SvCatalog.DataRoot + entry.Folder + file);
            Console.WriteLine($"{file}: {(data == null ? "missing" : "")}");
            if (data == null) continue;
            var nodes = FbTable.Root(data).Vector(1);
            for (int i = 0; i < nodes.Length; i++)
            {
                var n = nodes.Table(i);
                var t = n.Table(1);
                int p = t.InlineStruct(2);
                string pos = p == 0 ? "-" : $"{BitConverter.ToSingle(data, p):0.###} {BitConverter.ToSingle(data, p + 4):0.###} {BitConverter.ToSingle(data, p + 8):0.###}";
                Console.WriteLine($"  {i,2} {n.String(0),-22} parent {n.I32(4, -1),2}  effect_node {n.String(6) ?? "-",-20} pos {pos}");
            }
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

    case "skeletons": // skeletons: every catalog model's segment-scale-compensate flags (trskl bones[].inherit_position)
    {
        var catalog = new SvCatalog(archive.Read(SvCatalog.Path));
        var values = new Dictionary<int, int>();
        int models = 0, mixed = 0, none = 0;
        foreach (var path in catalog.Entries.Select(e => e.ModelPath).Distinct())
        {
            var mdlData = archive.Read(SvCatalog.DataRoot + path);
            if (mdlData == null) continue;
            string dir = SvCatalog.DataRoot + path.Substring(0, path.LastIndexOf('/') + 1);
            var sklData = archive.Read(dir + FbTable.Root(mdlData).Table(2).String(0));
            if (sklData == null) continue;
            var bones = FbTable.Root(sklData).Vector(2);
            int on = 0, off = 0;
            for (int k = 0; k < bones.Length; k++)
            {
                int v = bones.Table(k).U8(0);
                values[v] = values.TryGetValue(v, out int c) ? c + 1 : 1;
                if (v != 0) on++; else off++;
            }
            models++;
            if (on > 0 && off > 0) { mixed++; if (mixed <= 20) Console.WriteLine($"mixed {path}: {on} on, {off} off"); }
            if (on == 0) { none++; if (none <= 20) Console.WriteLine($"all off {path} ({off} bones)"); }
        }
        Console.WriteLine($"{models} models: {models - mixed - none} all on, {mixed} mixed, {none} all off; values " +
            string.Join(", ", values.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}: {kv.Value} bones")));
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

    case "dump": // dump --hashes <hashes_inside_fd.txt> --out <dir> [path prefix...]: every data table with a .bfbs schema next to
                 // it, decoded to JSON (<dir>/<path>.json) beside the raw files, and <dir>/index.json (path, schema root, sizes)
    {
        int h = rest.IndexOf("--hashes"), o = rest.IndexOf("--out");
        if (h < 0 || o < 0) { Console.Error.WriteLine("dump --hashes <list> --out <dir> [prefix...]"); return 1; }
        string outDir = Path.GetFullPath(rest[o + 1]);
        var prefixes = rest.Where((a, i) => i > 0 && i != h && i != h + 1 && i != o && i != o + 1).ToList();
        var names = File.ReadLines(rest[h + 1]).Select(l => l.Split(' ')).Where(p => p.Length >= 2).Select(p => p[1].Trim())
            .Where(n => prefixes.Count == 0 || prefixes.Any(n.StartsWith)).ToList();
        // Data files: the same path as a schema but another extension (mostly .bin).
        var byStem = names.GroupBy(n => n.Substring(0, n.Length - Path.GetExtension(n).Length)).ToDictionary(g => g.Key, g => g.ToList());
        var index = new List<object>();
        int schemas = 0, decoded = 0, failed = 0;
        long bytes = 0;
        foreach (var bfbsPath in names.Where(n => n.EndsWith(".bfbs", StringComparison.Ordinal)))
        {
            var bfbs = archive.Read(bfbsPath);
            if (bfbs == null) continue;
            schemas++;
            string stem = bfbsPath.Substring(0, bfbsPath.Length - 5);
            BfbsSchema schema;
            try { schema = new BfbsSchema(bfbs); }
            catch (Exception e) { Console.Error.WriteLine($"schema {bfbsPath}: {e.Message}"); failed++; continue; }
            void Save(string path, byte[] data)
            {
                string file = Path.Combine(outDir, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllBytes(file, data);
                bytes += data.Length;
            }
            Save(bfbsPath, bfbs);
            foreach (var dataPath in byStem.TryGetValue(stem, out var siblings) ? siblings.Where(s => s != bfbsPath) : Enumerable.Empty<string>())
            {
                var data = archive.Read(dataPath);
                if (data == null) continue;
                Save(dataPath, data);
                string json;
                try { json = BfbsSchema.ToJson(schema.Decode(data)); }
                catch (Exception e) { Console.Error.WriteLine($"decode {dataPath}: {e.Message}"); failed++; continue; }
                Save(dataPath + ".json", System.Text.Encoding.UTF8.GetBytes(json));
                decoded++;
                index.Add(new Dictionary<string, object> { ["path"] = dataPath, ["schema"] = schema.RootName ?? "", ["bytes"] = (long)data.Length, ["json"] = (long)json.Length });
            }
            if (schemas % 500 == 0) Console.WriteLine($"  {schemas} schemas, {decoded} decoded...");
        }
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "index.json"), BfbsSchema.ToJson(index));
        Console.WriteLine($"{schemas} schemas, {decoded} files decoded, {failed} failed, {bytes / 1048576.0:0.0} MB -> {outDir}");
        break;
    }

    case "behaviour": // behaviour --hashes <list> --dex <dex.json> --out <dir>: the overworld AI in PokeSim's behaviour format (SvBehaviourImport)
    {
        int h = rest.IndexOf("--hashes"), dx = rest.IndexOf("--dex"), o = rest.IndexOf("--out");
        if (h < 0 || dx < 0 || o < 0) { Console.Error.WriteLine("behaviour --hashes <list> --dex <dex.json> --out <dir>"); return 1; }
        var names = File.ReadLines(rest[h + 1]).Select(l => l.Split(' ')).Where(p => p.Length >= 2).Select(p => p[1].Trim())
            .Where(n => n.StartsWith("world/data/pokemon/", StringComparison.Ordinal)).ToList();
        // Showdown ids by (national dex, forme index) from PokeSim's dex.json.
        var ids = new Dictionary<(int, int), string>();
        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(rest[dx + 1])))
            foreach (var sp in doc.RootElement.GetProperty("species").EnumerateObject())
            {
                int num = sp.Value.TryGetProperty("num", out var n) ? n.GetInt32() : 0;
                int forme = sp.Value.TryGetProperty("formeIndex", out var fi) ? fi.GetInt32() : 0;
                if (num > 0 && !ids.ContainsKey((num, forme))) ids[(num, forme)] = sp.Name;
            }
        // The game's internal species numbers -> national dex, from its own personal table.
        var personal = new SvPersonal(archive.Read(SvPersonal.Path));
        var nationalOf = new Dictionary<int, int>();
        for (int national = 1; national <= 1025; national++)
        {
            int internalNo = personal.CatalogSpecies(national, 0);
            if (internalNo > 0 && !nationalOf.ContainsKey(internalNo)) nationalOf[internalNo] = national;
        }
        var import = new SvBehaviourImport(archive.Read, names,
            (national, form) => ids.TryGetValue((national, form), out var id) ? id : form != 0 && ids.TryGetValue((national, 0), out var baseId) ? baseId + "-form" + form : null,
            internalNo => nationalOf.TryGetValue(internalNo, out int nat) ? nat : 0);
        import.Run();
        string outDir = Path.GetFullPath(rest[o + 1]);
        foreach (var kv in import.Files)
        {
            string file = Path.Combine(outDir, kv.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, BfbsSchema.ToJson(kv.Value) + "\n");
        }
        foreach (var line in import.Log) Console.WriteLine(line);
        Console.WriteLine($"{import.Files.Count} files -> {outDir}");
        break;
    }

    case "rootmotion": // rootmotion <national dex> [--clip part]: bones near the skeleton's top whose translation travels over a clip
    {
        var model = LoadModelFrom(archive, rest, out string folder);
        LoadClips(archive, model, folder, rest);
        int Depth(int i) { int d = 0; while (i >= 0 && model.Nodes[i].Parent >= 0) { i = model.Nodes[i].Parent; d++; } return d; }
        var depth = model.Nodes.Select((n, i) => (n.Name, d: Depth(i))).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().d);
        foreach (var anim in model.Animations)
        {
            Console.WriteLine($"{anim.Name}  ({anim.FrameCount} frames, {anim.Duration:0.00} s){(anim.RootSpeed > 0 ? $", root motion {anim.RootSpeed:0.00}/s pinned" : "")}");
            foreach (var t in anim.Tracks.Where(t => t.Translation != null && t.Translation.Values.Length > 1 && depth.TryGetValue(t.Bone, out int d) && d <= 4))
            {
                var v = t.Translation.Values;
                var move = v[v.Length - 1] - v[0];
                float span = v.Max(p => (p - v[0]).Length());
                if (span < 0.01f) continue;
                Console.WriteLine($"   {t.Bone,-20} depth {depth[t.Bone]}  first {v[0]:0.000}  last {v[v.Length - 1]:0.000}  travel {move.Length():0.000}  max {span:0.000}");
            }
        }
        break;
    }

    case "fxmodel": // fxmodel <model path in the archive> --out <file.glb>: a move's effect model (Earthquake's rocks...)
    {
        int o = rest.IndexOf("--out");
        var glb = SvExporter.ExportEffectModel(archive.Read, rest[1]);
        if (glb == null) { Console.Error.WriteLine("not found: " + rest[1]); return 1; }
        string outFile = Path.GetFullPath(o >= 0 ? rest[o + 1] : "fxmodel.glb");
        File.WriteAllBytes(outFile, glb);
        Console.WriteLine($"{glb.Length / 1024} KB -> {outFile}");
        break;
    }
    case "sceneobjects": // sceneobjects <move number | .trsot path> [file suffix, default 0]: the move's scene object tree (.trsot): objects and their components
    {
        string path = rest[1].Contains('/') ? rest[1] : $"effect/battle_ew/ew{int.Parse(rest[1]):D4}/ew{int.Parse(rest[1]):D4}_{(rest.Count > 2 ? rest[2] : "0")}.trsot";
        var data = archive.Read(path);
        if (data == null) { Console.Error.WriteLine("not found: " + path); return 1; }
        foreach (var line in SvSceneObjects.Describe(data)) Console.WriteLine(line);
        break;
    }
    case "timeline": // timeline <move number | timeline path> [file suffix: attack, 1...] [--all-conditions]: a move's effect timeline (effect/battle_ew/ew<move>/ew<move>[_suffix].trtml)
    {
        string suffix = rest.Count > 2 && !rest[2].StartsWith("--") ? "_" + rest[2] : "";
        // A move number, or any timeline by archive path (effect/battle_es/es001/es001.trtml: the battle's system effects).
        string path = rest[1].Contains('/') ? rest[1] : $"effect/battle_ew/ew{int.Parse(rest[1]):D4}/ew{int.Parse(rest[1]):D4}{suffix}.trtml";
        var data = archive.Read(path);
        if (data == null) { Console.Error.WriteLine("not found: " + path); return 1; }
        var timeline = SvMoveTimeline.Read(data);
        Console.WriteLine($"{path}: {timeline.Frames} frames, {timeline.Groups.Count} groups");
        foreach (var group in timeline.Groups)
        {
            var conditions = group.Conditions.Where(c => rest.Contains("--all-conditions") || c.Value != "None");
            Console.WriteLine($"\n[{group.Object}]  {string.Join("  ", conditions.Select(c => $"{c.Key}={c.Value}"))}");
            foreach (var track in group.Tracks)
                foreach (var c in track.Commands.OrderBy(c => c.Start))
                    Console.WriteLine($"   {c.Start,4}-{c.End,-4} {track.Name,-12} {c.Type,-34} " +
                        string.Join("  ", c.Parameters.Select(p => $"{p.Key}={SvMoveTimeline.Format(p.Value)}")));
        }
        break;
    }

    case "timelines": // timelines: survey every move timeline (command types, parameters, conditions)
    {
        var types = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var conditionValues = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        int found = 0, failed = 0;
        for (int move = 0; move < 1000; move++)
        {
            var data = archive.Read($"effect/battle_ew/ew{move:D4}/ew{move:D4}.trtml");
            if (data == null) continue;
            found++;
            try
            {
                foreach (var group in SvMoveTimeline.Read(data).Groups)
                {
                    foreach (var c in group.Conditions)
                    {
                        if (!conditionValues.TryGetValue(c.Key, out var set)) conditionValues[c.Key] = set = new SortedSet<string>(StringComparer.Ordinal);
                        set.Add(c.Value);
                    }
                    foreach (var c in group.Tracks.SelectMany(t => t.Commands))
                    {
                        string key = c.Type + " (" + string.Join(", ", c.Parameters.Keys.OrderBy(k => k, StringComparer.Ordinal)) + ")";
                        types[key] = types.TryGetValue(key, out int n) ? n + 1 : 1;
                    }
                }
            }
            catch (Exception e) { failed++; Console.WriteLine($"ew{move:D4}: {e.Message}"); }
        }
        Console.WriteLine($"{found} timelines, {failed} failed\n\nconditions:");
        foreach (var c in conditionValues) Console.WriteLine($"  {c.Key}: {string.Join(", ", c.Value)}");
        Console.WriteLine("\ncommands:");
        foreach (var t in types.OrderByDescending(t => t.Value)) Console.WriteLine($"  {t.Value,6}  {t.Key}");
        break;
    }

    case "particles": // particles <ptcl path in the archive>...: every emitter of particle files (VFXB)
    {
        foreach (var path in rest.Skip(1).Where(a => !a.StartsWith("--")))
        {
            var data = archive.Read(path);
            if (data == null) { Console.Error.WriteLine("not found: " + path); return 1; }
            var ptcl = VfxbFile.Read(data);
            if (rest.Count > 2) Console.WriteLine($"file {path}");
            foreach (var set in ptcl.Sets)
            {
                Console.WriteLine($"set {set.Name}");
                foreach (var e in set.Emitters) PrintEmitter(e, ptcl, "  ");
            }
        }
        break;
    }

    case "movefx": // movefx --hashes <hashes_inside_fd.txt> --out <dir> [move numbers...]: every move's effects as <dir>/<move, 4 digits>.json
    {
        int h = rest.IndexOf("--hashes"), o = rest.IndexOf("--out");
        string outDir = Path.GetFullPath(rest[o + 1]);
        Directory.CreateDirectory(outDir);
        var only = new HashSet<int>(rest.Skip(1).Where(a => int.TryParse(a, out _)).Select(int.Parse).Where(n => n > 0 && (h < 0 || n.ToString() != rest[h + 1])));
        var byMove = new SortedDictionary<int, List<string>>();
        foreach (var line in File.ReadLines(rest[h + 1]))
        {
            string path = line.Substring(line.IndexOf(' ') + 1);
            var m = System.Text.RegularExpressions.Regex.Match(path, @"^effect/battle_ew/ew(\d{4})/(ew\d{4}(?:_\d+|_[a-z][a-z0-9_]*)?)\.(trtml|trsot)$|^effect/battle_ew/ew(\d{4})/(ew\d{4}(?:_\d+)?)_0\.trsot$");
            // The battle's system effects (stat up / down, heal, statuses, Substitute...): es001.. as -1, -2... (es<n>.json).
            // The five statuses (es001-005) keep their scene objects and particles in effect/field/status/es<n>/.
            var es = System.Text.RegularExpressions.Regex.Match(path, @"^effect/(?:battle_es|field/status)/es(\d{3})/es\d{3}(?:_0)?\.(trtml|trsot)$");
            // Battle events the stage plays: terrains starting (ee050 Grassy, ee051 Misty, ee052 Electric, ee053 Psychic) and
            // entry hazards hurting a Pokemon as it comes in (ee040 Spikes, ee041 Toxic
            // Spikes, ee042 Stealth Rock), as -1040.. (ee<n>.json).
            var ee = System.Text.RegularExpressions.Regex.Match(path, @"^effect/battle_ee/ee(04[0-2]|05[0-3])/ee\d{3}(?:_0)?\.(trtml|trsot)$");
            // Weather on the field (etb001 rain, etb002 snow, etb003 sandstorm, etb004 sun), as -2001.. (etb<n>.json):
            // looping particles and an ambient sound, no timeline.
            var etb = System.Text.RegularExpressions.Regex.Match(path, @"^effect/battle_etb/etb(\d{3})/etb\d{3}_0\.trsot$");
            if (!m.Success && !es.Success && !ee.Success && !etb.Success) continue;
            int move = es.Success ? -int.Parse(es.Groups[1].Value) : ee.Success ? -1000 - int.Parse(ee.Groups[1].Value)
                : etb.Success ? -2000 - int.Parse(etb.Groups[1].Value)
                : int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[4].Value);
            if (only.Count > 0 && !only.Contains(move)) continue;
            if (!byMove.TryGetValue(move, out var list)) byMove[move] = list = new List<string>();
            list.Add(path);
        }
        long total = 0;
        int files = 0, emitters = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var textureSources = new SortedDictionary<string, (string path, int cx, int cy)>(StringComparer.Ordinal);
        var primitiveShapes = new SortedDictionary<string, FxPrimitiveShape>(StringComparer.Ordinal);
        foreach (var kv in byMove)
        {
            var timelines = new Dictionary<string, byte[]>();
            var scenes = new Dictionary<string, byte[]>();
            foreach (var path in kv.Value)
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                var data = archive.Read(path);
                if (data == null) continue;
                if (path.EndsWith(".trtml", StringComparison.Ordinal)) timelines[stem] = data;
                else if (stem.EndsWith("_0", StringComparison.Ordinal)) scenes[stem.Substring(0, stem.Length - 2)] = data;
            }
            if (timelines.Count == 0 && kv.Key > -2000) continue;
            // Later hits of multi-hit moves (ew0024_2: Double Kick's second kick) have no scene objects of their own:
            // they play the first hit's objects (73 timelines). _1.trsot files repeat the _0 ones.
            if (scenes.Count > 0)
            {
                var first = scenes.OrderBy(k => k.Key, StringComparer.Ordinal).First().Value;
                foreach (var stem in timelines.Keys) if (!scenes.ContainsKey(stem)) scenes[stem] = first;
            }
            string folder = kv.Key <= -2000 ? $"effect/battle_etb/etb{-2000 - kv.Key:D3}"
                : kv.Key <= -1000 ? $"effect/battle_ee/ee{-1000 - kv.Key:D3}" : kv.Key < 0 ? $"effect/battle_es/es{-kv.Key:D3}" : null;
            string json = SvMoveFx.Export(kv.Key, timelines, scenes, archive.Read, out int count,
                (path, name, cx, cy) => { if (!textureSources.ContainsKey(name)) textureSources[name] = (path, cx, cy); },
                (path, name, prim) => { if (!primitiveShapes.ContainsKey(name)) { var shape = FxPrimitiveShape.Analyze(prim); shape.Source = path; primitiveShapes[name] = shape; } },
                folder);
            string file = Path.Combine(outDir, kv.Key <= -2000 ? $"etb{-2000 - kv.Key:D3}.json"
                : kv.Key <= -1000 ? $"ee{-1000 - kv.Key:D3}.json" : kv.Key < 0 ? $"es{-kv.Key:D3}.json" : $"{kv.Key:D4}.json");
            File.WriteAllText(file, json);
            total += json.Length; files++; emitters += count;
        }
        Console.WriteLine($"{files} moves, {emitters} emitters, {total / 1024} KB -> {outDir} ({watch.ElapsedMilliseconds} ms)");

        // Primitive shapes (primitives.json).
        var pw = new JsonWriter(indent: false);
        pw.BeginObject();
        foreach (var kv in primitiveShapes) { pw.Name(kv.Key); kv.Value.Write(pw); }
        pw.EndObject();
        File.WriteAllText(Path.Combine(outDir, "primitives.json"), pw.ToString());
        Console.WriteLine($"{primitiveShapes.Count} primitive shapes ({primitiveShapes.Count(p => p.Value.Kind == "revolution")} round) -> primitives.json");

        // Texture shapes (textures.json): analysed from the first particle file that has each texture.
        var tw = new JsonWriter(indent: false);
        tw.BeginObject();
        var bntxCache = new Dictionary<string, (byte[] bntx, Dictionary<string, int> index)>();
        int analysed = 0;
        foreach (var t in textureSources)
        {
            if (!bntxCache.TryGetValue(t.Value.path, out var entry))
            {
                var ptcl = VfxbFile.Read(archive.Read(t.Value.path));
                var bytes = ptcl.Bntx.Count > 0 ? ptcl.Bntx.ToArray() : null;
                var index = new Dictionary<string, int>();
                if (bytes != null) for (int i = 0; i < Bntx.Count(bytes); i++) index[Bntx.Name(bytes, i)] = i;
                bntxCache[t.Value.path] = entry = (bytes, index);
            }
            if (entry.bntx == null || !entry.index.TryGetValue(t.Key, out int at)) continue;
            try
            {
                var tex = Bntx.Read(entry.bntx, at);
                var shape = FxTextureShape.Analyze(FxTextureShape.Mask(Bntx.Mapped(tex), tex.Width, tex.Height, tex.Format), tex.Width, tex.Height, t.Value.cx, t.Value.cy);
                shape.Source = t.Value.path;
                tw.Name(t.Key);
                shape.Write(tw);
                analysed++;
            }
            catch (Exception e) { Console.WriteLine($"{t.Key}: {e.Message}"); }
        }
        tw.EndObject();
        File.WriteAllText(Path.Combine(outDir, "textures.json"), tw.ToString());
        Console.WriteLine($"{analysed}/{textureSources.Count} texture shapes -> textures.json ({watch.ElapsedMilliseconds} ms)");
        break;
    }

    case "particle-textures": // particle-textures <ptcl path in the archive> <out dir>: the textures inside a particle file as PNG (reference only)
    {
        var ptcl = VfxbFile.Read(archive.Read(rest[1]));
        if (ptcl.Bntx.Count == 0) { Console.WriteLine("no textures"); break; }
        var bntx = ptcl.Bntx.ToArray();
        Directory.CreateDirectory(rest[2]);
        for (int i = 0; i < Bntx.Count(bntx); i++)
        {
            string name = Bntx.Name(bntx, i);
            try
            {
                var tex = Bntx.Read(bntx, i);
                File.WriteAllBytes(Path.Combine(rest[2], name + ".png"), Png.Encode(tex.Rgba, tex.Width, tex.Height));
                Console.WriteLine($"{name}: {tex.Width}x{tex.Height} format 0x{tex.Format:X4} channels {string.Join(",", tex.Channels)}");
            }
            catch (Exception e) { Console.WriteLine($"{name}: {e.Message}"); }
        }
        break;
    }

    case "particles-survey": // particles-survey --hashes <hashes_inside_fd.txt>: parse every move particle file, count what they use
    {
        int h = rest.IndexOf("--hashes");
        var paths = File.ReadLines(rest[h + 1]).Select(l => l.Substring(l.IndexOf(' ') + 1))
            .Where(p => p.StartsWith("effect/battle_ew/", StringComparison.Ordinal) && p.EndsWith(".ptcl", StringComparison.Ordinal)).ToList();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Count(string key) => counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
        var textures = new Dictionary<string, int>();
        var primNames = new Dictionary<string, int>();
        int files = 0, emitters = 0, bad = 0, failed = 0;
        void Visit(VfxbEmitter e, VfxbFile f, int depth)
        {
            emitters++;
            if (e.ParsedSize != e.BinarySize) { bad++; if (bad <= 5) Console.WriteLine($"size {e.ParsedSize} != {e.BinarySize}: {e.Name}"); }
            Count($"blend {e.BlendType}"); Count($"billboard {e.BillboardType}"); Count($"volume {e.VolumeType}");
            Count($"color0 type {e.Color0Type}"); Count($"alpha0 type {e.Alpha0Type}"); Count($"follow {e.FollowType}");
            Count($"colorCombiner {e.ColorCombiner}"); Count($"alphaCombiner {e.AlphaCombiner}"); Count($"shaderType {e.ShaderType}");
            Count($"customShader {e.CustomShaderIndex} type {e.ShaderRefType} user {e.UserShaderIndex1}/{e.UserShaderIndex2}");
            int used = e.Samplers.Count(s => s.Used);
            Count($"combine[{used} tex] blend c{e.Texture1ColorBlend}{e.Texture2ColorBlend} a{e.Texture1AlphaBlend}{e.Texture2AlphaBlend} in c{string.Join("", e.TextureColorInput)} a{string.Join("", e.TextureAlphaInput)}");
            Count($"textures used {e.Samplers.Count(s => s.Used)}"); Count($"child depth {depth}");
            if (e.PrimitiveId != ulong.MaxValue)
            {
                Count("uses primitive");
                if (!f.Primitives.TryGetValue(e.PrimitiveId, out var prim)) Count("primitive unresolved");
                else if (prim.Positions == null || prim.Indices == null) Count("primitive without mesh");
                else { Count("primitive decoded"); var key = System.Text.RegularExpressions.Regex.Replace(prim.Name, @"^(cpm|upm)_(ew\d+_)?", ""); primNames[key] = primNames.TryGetValue(key, out int pn) ? pn + 1 : 1; }
            }
            if (e.OneTime) Count("one time"); if (e.InfiniteLife) Count("infinite life");
            foreach (var a in e.Animations.Keys) Count("anim " + a);
            foreach (var a in e.Attributes) Count("attr " + a);
            foreach (var s in e.Samplers.Where(s => s.Used))
            {
                string name = f.TextureNames.TryGetValue(s.TextureId, out var n) ? n : "?";
                textures[name] = textures.TryGetValue(name, out int c) ? c + 1 : 1;
            }
            foreach (var c in e.Children) Visit(c, f, depth + 1);
        }
        foreach (var path in paths)
        {
            var data = archive.Read(path);
            if (data == null) continue;
            try
            {
                var f = VfxbFile.Read(data);
                files++;
                foreach (var e in f.Sets.SelectMany(s => s.Emitters)) Visit(e, f, 0);
            }
            catch (Exception ex) { failed++; if (failed <= 5) Console.WriteLine($"{path}: {ex.Message}"); }
        }
        Console.WriteLine($"{files}/{paths.Count} files, {failed} failed, {emitters} emitters, {bad} with a size mismatch, {textures.Count} distinct textures");
        foreach (var c in counts) Console.WriteLine($"  {c.Value,7}  {c.Key}");
        Console.WriteLine("\nprimitives by use:");
        foreach (var t in primNames.OrderByDescending(t => t.Value).Take(60)) Console.WriteLine($"  {t.Value,6}  {t.Key}");
        Console.WriteLine("\ntextures by use:");
        foreach (var t in textures.OrderByDescending(t => t.Value).Take(rest.Contains("--all") ? int.MaxValue : 80)) Console.WriteLine($"  {t.Value,6}  {t.Key}");
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
static void PrintEmitter(VfxbEmitter e, VfxbFile f, string indent)
{
    string F(float[] v) => "(" + string.Join(", ", v.Select(x => x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))) + ")";
    string K(VfxbKey[] keys) => keys.Length == 0 ? "-" : string.Join(" ", keys.Select(k => $"{k.Time:0.##}:{k.X:0.##},{k.Y:0.##},{k.Z:0.##}"));
    var tex = e.Samplers.Where(s => s.Used).Select(s => f.TextureNames.TryGetValue(s.TextureId, out var n) ? n : $"#{s.TextureId:X}");
    Console.WriteLine($"{indent}{e.Name}{(e.ParsedSize != e.BinarySize ? $"  !! parsed {e.ParsedSize} of {e.BinarySize} bytes" : "")}");
    Console.WriteLine($"{indent}  emit: {(e.OneTime ? "once" : "loop")} start {e.EmitStart} duration {e.EmitDuration} rate {e.Rate}±{e.RateRandom} interval {e.Interval}  life {e.Life}±{e.LifeRandom}{(e.InfiniteLife ? " infinite" : "")}  follow {e.FollowType}");
    Console.WriteLine($"{indent}  shape {e.VolumeType} radius {F(e.VolumeRadius)} sweep {e.SweepLongitude:0.##}  velocity all {e.AllDirection} dir {e.DirectionalScale}*{F(e.Direction)} diffusion {e.DiffusionAngle} random {e.VelocityRandom}  gravity {F(e.Gravity)} air {e.AirResistance}");
    Console.WriteLine($"{indent}  draw: billboard {e.BillboardType} offset {e.OffsetType} blend {e.BlendType} combiner {e.ColorCombiner}/{e.AlphaCombiner} shader {e.ShaderType} soft {e.SoftParticle} flags {e.StaticFlags[0]:X8} inherit-flags path {e.InheritDrawPath} pre {e.PreDraw} a0f {e.InheritAlpha0EachFrame} a1f {e.InheritAlpha1EachFrame} own {e.EnableEmitterParticle} alphatest {e.AlphaTest} {e.AlphaFunc} {e.AlphaThreshold}  textures {string.Join(", ", tex)}{(e.PrimitiveId != ulong.MaxValue ? $"  primitive {e.PrimitiveId:X}" : "")}");
    Console.WriteLine($"{indent}  scale {F(e.ParticleScale)}±{F(e.ParticleScaleRandom)} keys {K(e.ScaleKeys)}  rotate init {F(e.RotateInit)}±{F(e.RotateInitRandom)} add {F(e.RotateAdd)}±{F(e.RotateAddRandom)}");
    Console.WriteLine($"{indent}  color0 [{e.Color0Type}] {F(e.Color0)} keys {K(e.Color0Keys)}  alpha0 [{e.Alpha0Type}] {e.Alpha0} keys {K(e.Alpha0Keys)}");
    Console.WriteLine($"{indent}  color1 [{e.Color1Type}] {F(e.Color1)} keys {K(e.Color1Keys)}  alpha1 [{e.Alpha1Type}] {e.Alpha1}  emitter color {F(e.EmitterColor0)} scale {F(e.Scale)} translate {F(e.Translate)}");
    Console.WriteLine($"{indent}  shader {e.ShaderIndex} (type {e.ShaderRefType}, compute {e.ComputeShaderIndex}, user {e.UserShaderIndex1}/{e.UserShaderIndex2}, custom {e.CustomShaderIndex})  combine in c{string.Join("", e.TextureColorInput)} a{string.Join("", e.TextureAlphaInput)} blend c{e.Texture1ColorBlend}{e.Texture2ColorBlend} a{e.Texture1AlphaBlend}{e.Texture2AlphaBlend}");
    if (e.Patterns[0].Count > 1 || e.TextureAnims[0].Scroll) Console.WriteLine($"{indent}  tex0 pattern {e.Patterns[0].Count} every {e.Patterns[0].Frequency} type {e.TextureAnims[0].PatternType}  uv div {F(e.Scrolls[0].UVDivisions)} scroll {F(e.Scrolls[0].ScrollAdd)}");
    foreach (var a in e.Animations) Console.WriteLine($"{indent}  anim {a.Key}: {K(a.Value.Keys)}");
    Console.WriteLine($"{indent}  colorScale {e.ColorScale}");
    foreach (var a in e.AttributeData) Console.WriteLine($"{indent}  attr {a.Key} ({a.Value.Length} floats): " + string.Join(" ", a.Value.Select((f, i) => $"[{i * 4}]{f:0.###}")));
    for (int i = 0; i < 3; i++)
        if (e.Samplers[i].Used)
        {
            var sc = e.Scrolls[i];
            Console.WriteLine($"{indent}  tex{i} scroll: add {F(sc.ScrollAdd)} start {F(sc.Scroll)} rnd {F(sc.ScrollRandom)} scaleAdd {F(sc.ScaleAdd)} scale {F(sc.Scale)} scaleRnd {F(sc.ScaleRandom)} rot {sc.RotationAdd} {sc.Rotation} {sc.RotationRandom} {sc.RotationType} div {F(sc.UVDivisions)} base {F(sc.Unused)}  pattern {e.Patterns[i].Count}/{e.Patterns[i].Frequency} repeat {e.TextureAnims[i].Repeat} anim scroll/rotate/scale {(e.TextureAnims[i].Scroll ? 1 : 0)}/{(e.TextureAnims[i].Rotate ? 1 : 0)}/{(e.TextureAnims[i].Scale ? 1 : 0)} wrap {e.Samplers[i].WrapU}/{e.Samplers[i].WrapV}");
        }
    foreach (var c in e.Children) PrintEmitter(c, f, indent + "    ");
}

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

