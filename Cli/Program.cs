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

if (rest[0] == "audio") // audio <bank> <EVENT_NAME>... --out <dir> [--switch INDEX,FORM00] [--variant N] [--vgmstream <vgmstream-cli.exe>]: an event's sounds as .wem (+ .wav)
{
    string folder = PokeModelExporter.Wwise.WwiseSounds.FindFolder(gameFolder);
    if (folder == null) { Console.Error.WriteLine($"No audio/wwise/bin/Switch/Init.bnk under {gameFolder}"); return 1; }
    int o = rest.IndexOf("--out"), v = rest.IndexOf("--vgmstream"), sw = rest.IndexOf("--switch"), vr = rest.IndexOf("--variant");
    int variant = vr >= 0 ? int.Parse(rest[vr + 1]) : 0;
    var switches = sw >= 0 ? rest[sw + 1].Split(',') : new string[0];
    string outDir = Path.GetFullPath(o >= 0 ? rest[o + 1] : "audio");
    Directory.CreateDirectory(outDir);
    var sounds = new PokeModelExporter.Wwise.WwiseSounds(folder);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 2; i < rest.Count && !rest[i].StartsWith("--"); i++)
    {
        var media = sounds.Resolve(rest[1], rest[i], variant, out int variants, switches);
        Console.WriteLine($"{rest[i]} (id {PokeModelExporter.Wwise.WwiseSounds.Hash(rest[i])}): {media.Count} sound(s), variant {variant} of {variants}");
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

if (rest[0] == "ctrsound") // ctrsound <bcsar> <index or script id>... --out <dir>: Ultra Sun/Moon sounds rendered to WAV
{
    var sar = new PokeModelExporter.Ctr.CtrSoundArchive(rest[1]);
    if (rest.Contains("--notes")) PokeModelExporter.Ctr.CtrSoundArchive.Debug = Console.WriteLine;
    int cb = rest.IndexOf("--code"); // --code <ExeFS/code.bin>: the game's sound id tables
    if (cb >= 0) sar.UseIdTables(File.ReadAllBytes(rest[cb + 1]));
    int o = rest.IndexOf("--out");
    string dir = o >= 0 ? rest[o + 1] : ".";
    Directory.CreateDirectory(dir);
    if (rest.Contains("--labels")) { for (int i = 0; i < sar.SoundCount; i++) Console.WriteLine($"{i} {sar.Label(i)}"); return 0; }
    int wv = rest.IndexOf("--wave"); // --wave <warc> <index>: one wave as decoded
    if (wv >= 0)
    {
        var s = sar.WaveSamples(int.Parse(rest[wv + 1], System.Globalization.NumberStyles.HexNumber), int.Parse(rest[wv + 2]), out int wr);
        Console.WriteLine($"wave: {s?.Length} samples at {wr}: " + (s == null ? "" : string.Join(" ", s.Take(10).Select(x => x.ToString("0.000")))));
        return 0;
    }
    foreach (var a in rest.Skip(2).TakeWhile(x => !x.StartsWith("--")))
    {
        int index = sar.IndexOf(int.Parse(a));
        float[] pcm = null;
        try { pcm = sar.Render(index); } catch (Exception e) { Console.WriteLine($"{a}: {e.GetType().Name} {e.Message}"); }
        float peak = pcm == null ? 0 : pcm.Select(Math.Abs).DefaultIfEmpty(0).Max();
        Console.WriteLine($"{a} -> sound {index} {sar.Label(index)}: {(pcm == null ? "not rendered" : $"{pcm.Length / 2 / 48000.0:0.00} s, peak {peak:0.00}")}");
        if (pcm != null) File.WriteAllBytes(Path.Combine(dir, $"{index}_{sar.Label(index)}.wav"), PokeModelExporter.Ctr.CtrSoundArchive.Wav(pcm));
    }
    return 0;
}

if (rest[0] == "spbd") // spbd <GARC a/0/8/7> <index> [--out <dir>]: an Ultra Sun/Moon particle resource as effect JSON + texture PNGs
{
    var garc = PokeModelExporter.Ctr.Garc.Read(rest[1]);
    int index = int.Parse(rest[2]);
    var sp = PokeModelExporter.Ctr.Spbd.Read(PokeModelExporter.Ctr.Garc.File0(garc, index), "usum" + index);
    foreach (var set in sp.Sets)
    {
        Console.WriteLine($"set {set.Name}: {set.Emitters.Count} emitters");
        foreach (var e in set.Emitters)
        {
            var j = e.Json;
            Console.WriteLine($"  {e.Name} type {e.Type}: start {j["start"]} dur {j["duration"]} rate {j["rate"]} int {j["interval"]} life {j["life"]} shape {j["shape"]} billboard {j["billboard"]} blend {j["blend"]} " +
                $"tex {string.Join("/", e.Textures.Where(x => x != null).Select(x => $"{x.Width}x{x.Height} {PokeModelExporter.Ctr.PicaTexture.Name(x.Format)}"))} | tev {e.Pica.GetValueOrDefault("pica.tev")} blend {e.Pica.GetValueOrDefault("pica.blend")} depth {e.Pica.GetValueOrDefault("pica.depth")}");
        }
    }
    int o = rest.IndexOf("--out");
    if (o >= 0)
    {
        Directory.CreateDirectory(rest[o + 1]);
        File.WriteAllText(Path.Combine(rest[o + 1], $"usum{index}.json"), sp.ToJson());
        foreach (var t in sp.Textures.Values) File.WriteAllBytes(Path.Combine(rest[o + 1], t.Name + ".png"), sp.Png(t));
    }
    return 0;
}

if (rest[0] == "spbdtex") // spbdtex <GARC a/0/8/7> <index> --out <dir>: every emitter texture decoded as each PICA format of its size (research)
{
    var garc = PokeModelExporter.Ctr.Garc.Read(rest[1]);
    var d = PokeModelExporter.Ctr.Garc.File0(garc, int.Parse(rest[2]));
    string dir = rest[rest.IndexOf("--out") + 1];
    Directory.CreateDirectory(dir);
    int texBlock = BitConverter.ToInt32(d, 0x14), strings = BitConverter.ToInt32(d, 0x10);
    int set = 0x48, n = BitConverter.ToInt32(d, set + 0x10), table = BitConverter.ToInt32(d, set + 0x14);
    var done = new HashSet<int>();
    for (int k = 0; k < n; k++)
    {
        int e = BitConverter.ToInt32(d, table + 8 * k);
        for (int slot = 0; slot < 2; slot++)
        {
            int t = e + 0x40 + 0x7C * slot;
            int w = BitConverter.ToUInt16(d, t), h = BitConverter.ToUInt16(d, t + 2), code = BitConverter.ToInt32(d, t + 0x6C), size = BitConverter.ToInt32(d, t + 0x70), off = BitConverter.ToInt32(d, t + 0x74);
            if (w == 0 || !done.Add(off)) continue;
            int bpp = size * 8 / (w * h);
            foreach (int f in new[] { 0x02, 0x03, 0x04, 0x16, 0x17, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B })
            {
                if (PokeModelExporter.Ctr.PicaTexture.BitsPerPixel(f) != bpp) continue;
                var rgba = PokeModelExporter.Ctr.PicaTexture.Decode(d, texBlock + off, w, h, f);
                File.WriteAllBytes(Path.Combine(dir, $"c{code:X2}_e{k}s{slot}_{PokeModelExporter.Ctr.PicaTexture.Name(f)}.png"), Png.Encode(rgba, w, h));
            }
            Console.WriteLine($"emitter {k} slot {slot}: {w}x{h} code 0x{code:X2} {bpp} bpp at 0x{texBlock + off:X}");
        }
    }
    return 0;
}

if (rest[0] == "sesd") // sesd <GARC a/0/8/8> <index>: an Ultra Sun/Moon battle sequence's commands with typed arguments
{
    var garc = PokeModelExporter.Ctr.Garc.Read(rest[1]);
    var seq = PokeModelExporter.Ctr.Sesd.Read(PokeModelExporter.Ctr.Garc.File0(garc, int.Parse(rest[2])));
    Console.WriteLine($"version {seq.Version}, {seq.Length} frames, {seq.Records.Count} commands");
    foreach (var r in seq.Records) Console.WriteLine(r);
    return 0;
}

if (rest[0] == "ctrfind") // ctrfind <romfs> <text>: every GARC entry (unpacked, and its container's sub-files) that contains the text (research)
{
    var needle = System.Text.Encoding.ASCII.GetBytes(rest[2]);
    bool Has(byte[] d) => d != null && d.AsSpan().IndexOf(needle) >= 0;
    foreach (var file in Directory.EnumerateFiles(rest[1], "*", SearchOption.AllDirectories))
    {
        List<List<byte[]>> garc;
        try { garc = PokeModelExporter.Ctr.Garc.Read(file); } catch (Exception) { continue; }
        for (int i = 0; i < garc.Count; i++)
        {
            byte[] d;
            try { d = PokeModelExporter.Ctr.Garc.File0(garc, i); } catch (Exception) { continue; }
            if (d == null) continue;
            var subs = PokeModelExporter.Ctr.CtrTrainer.Container(d);
            if (subs == null) { if (Has(d)) Console.WriteLine($"{Path.GetRelativePath(rest[1], file)} {i}"); continue; }
            for (int k = 0; k < subs.Count; k++) if (Has(subs[k])) Console.WriteLine($"{Path.GetRelativePath(rest[1], file)} {i} sub {k}");
        }
    }
    return 0;
}

if (rest[0] == "ctrmotion") // ctrmotion <GARC file> <index> [--out <file.json>]: an Ultra Sun/Moon (3DS) GF motion pack as JSON
{
    var garc = PokeModelExporter.Ctr.Garc.Read(rest[1]);
    var data = PokeModelExporter.Ctr.Garc.File0(garc, int.Parse(rest[2]));
    if (!PokeModelExporter.Ctr.GfMotion.IsMotion(data) && PokeModelExporter.Ctr.CtrTrainer.Container(data) is { } subs)
    {
        // A container (the trainer's motions: one per player body, sometimes more): each sub-file's tracks, or --sub n alone.
        int pick = rest.IndexOf("--sub") is int si && si >= 0 ? int.Parse(rest[si + 1]) : -1;
        Console.WriteLine($"container: {subs.Count} sub-files");
        for (int i = 0; i < subs.Count; i++)
        {
            if (pick >= 0 && i != pick) continue;
            if (!PokeModelExporter.Ctr.GfMotion.IsMotion(subs[i])) { Console.WriteLine($"sub {i}: {subs[i].Length} bytes, not a motion ({(subs[i].Length >= 4 ? System.Text.Encoding.ASCII.GetString(subs[i], 0, 4) : "")})"); continue; }
            var sm = PokeModelExporter.Ctr.GfMotion.Read(subs[i]);
            Console.WriteLine($"sub {i}: {sm.FrameCount} frames: {sm.Bones.Count} bone tracks, {sm.Uvs.Count} UV tracks, {sm.Colors.Count} colour tracks" + (sm.OtherSections.Count > 0 ? ", unread sections " + string.Join(",", sm.OtherSections) : ""));
            foreach (var u in sm.Uvs) Console.WriteLine($"  uv {u.Material} unit {u.Unit}: " + string.Join(" ", u.Channels.Select((c, k) => !c.Animated ? "-" : c.Frames == null ? c.Constant.ToString("0.###") : $"{c.Frames.Length} keys")));
            if (pick >= 0)
                foreach (var u in sm.Uvs)
                    for (int k = 0; k < u.Channels.Length; k++)
                        if (u.Channels[k]?.Frames is { } fr)
                            Console.WriteLine($"  uv {u.Material} channel {k}: " + string.Join(" ", fr.Select((f, i) => $"f{f:0.#}={u.Channels[k].Values[i]:0.####}")));
            if (pick >= 0) Console.WriteLine("  bones: " + string.Join(", ", sm.Bones.Select(b => b.Bone)));
        }
        return 0;
    }
    if (!PokeModelExporter.Ctr.GfMotion.IsMotion(data)) { Console.Error.WriteLine("not a GF motion pack"); return 1; }
    var mot = PokeModelExporter.Ctr.GfMotion.Read(data);
    Console.WriteLine($"{mot.FrameCount} frames{(mot.Loop ? ", loop" : "")}: {mot.Bones.Count} bone tracks, {mot.Uvs.Count} UV tracks, {mot.Colors.Count} colour tracks" +
        (mot.OtherSections.Count > 0 ? ", unread sections " + string.Join(",", mot.OtherSections) : ""));
    foreach (var u in mot.Uvs) Console.WriteLine($"  uv {u.Material} unit {u.Unit}: " + string.Join(" ", u.Channels.Select((c, i) => !c.Animated ? "-" : c.Frames == null ? c.Constant.ToString("0.###") : $"{c.Frames.Length} keys")));
    foreach (var c in mot.Colors) Console.WriteLine($"  colour {c.Material} constant {c.Index}: " + string.Join(" ", c.Channels.Select(x => !x.Animated ? "-" : x.Frames == null ? x.Constant.ToString("0.###") : $"{x.Frames.Length} keys")));
    foreach (var c in mot.Cameras) Console.WriteLine($"  camera {c.Name}: " + string.Join(" ", c.Channels.Select(x => !x.Animated ? "-" : x.Frames == null ? x.Constant.ToString("0.###") : $"{x.Frames.Length} keys")));
    foreach (var l in mot.Lights) Console.WriteLine($"  light {l.Name} type {l.Type}/{l.Unknown} colour {l.Color:X8}: " + string.Join(" ", l.Channels.Select(x => !x.Animated ? "-" : x.Frames == null ? x.Constant.ToString("0.###") : $"{x.Frames.Length} keys")));
    int o = rest.IndexOf("--out");
    if (o >= 0) File.WriteAllText(rest[o + 1], mot.ToJson());
    return 0;
}

if (rest[0] == "ctrmodel") // ctrmodel <GARC file> <index> --out <file.glb>: an Ultra Sun/Moon (3DS) GF model pack as GLB
{
    var garc = PokeModelExporter.Ctr.Garc.Read(rest[1]);
    var data = PokeModelExporter.Ctr.Garc.File0(garc, int.Parse(rest[2]));
    // A container (the trainers' bodies: a/1/7/4): its sub-file (--sub n, default 0).
    if (!PokeModelExporter.Ctr.GfModelPack.IsPack(data) && PokeModelExporter.Ctr.CtrTrainer.Container(data) is { } parts)
        data = parts[rest.IndexOf("--sub") is int si && si >= 0 ? int.Parse(rest[si + 1]) : 0];
    if (!PokeModelExporter.Ctr.GfModelPack.IsPack(data)) { Console.Error.WriteLine("not a GF model pack"); return 1; }
    var pack = PokeModelExporter.Ctr.GfModelPack.Read(data);
    int o = rest.IndexOf("--out");
    int tx = rest.IndexOf("--textures"); // --textures <dir>: every decoded texture as PNG (reference only)
    if (tx >= 0)
    {
        Directory.CreateDirectory(rest[tx + 1]);
        foreach (var t in pack.Textures.Where(t => t.Rgba != null))
            File.WriteAllBytes(Path.Combine(rest[tx + 1], t.Name + ".png"), Png.Encode(t.Rgba, t.Width, t.Height));
    }
    foreach (var t in pack.Textures)
        Console.WriteLine($"texture {t.Name} {t.Width}x{t.Height} {PokeModelExporter.Ctr.PicaTexture.Name(t.Format)} mips {t.Mips}{(t.Rgba == null ? " (not decoded)" : "")}");
    foreach (var m in pack.Models)
    {
        Console.WriteLine($"model {m.Name}: {m.Bones.Count} bones, {m.Materials.Count} materials, {m.Meshes.Count} meshes");
        foreach (var mat in m.Materials)
            Console.WriteLine($"  material {mat.Name} ({mat.FragmentShader}): " + string.Join(", ", mat.TexCoords.Select(t =>
                $"unit {t.Unit} {t.Texture} map {t.Mapping} scale {t.Scale} rot {t.Rotation:0.##} off {t.Translation} wrap {t.WrapU}/{t.WrapV}")));
        var md = PokeModelExporter.Ctr.GfModelConvert.ToModel(m, pack, out var warnings);
        foreach (var mm in md.Materials) { var so = mm.ShaderOptions; Console.WriteLine("  " + mm.Name + ": tev " + so.GetValueOrDefault("pica.tev") + " | buffer " + so.GetValueOrDefault("pica.tevBuffer") + " | colorOp " + so["pica.colorOp"] + " blend " + so["pica.blend"] + " alpha " + so["pica.alphaTest"] + " depth " + so["pica.depth"] + " cull " + so["pica.cull"] + " const0 " + mm.Colors["Constant0"]); }
        foreach (var w in warnings) Console.WriteLine("  warning: " + w);
        foreach (var mesh in md.Meshes) Console.WriteLine($"  mesh {mesh.Name}: {mesh.Positions.Length} vertices, {mesh.SubMeshes.Sum(s => s.Indices.Length) / 3} triangles, uv sets {mesh.TexCoords.Count}{(mesh.Colors != null ? ", colours" : "")}");
        if (rest.Contains("--bones"))
        {
            foreach (var b in m.Bones) Console.WriteLine($"  bone {b.Name} <- {b.Parent} flags {b.Flags} s {b.Scale} r {b.Rotation} t {b.Translation}");
            foreach (var gm in m.Meshes) Console.WriteLine($"  gfmesh {gm.Name}: bones/vertex {gm.BoneIndicesPerVertex}, box {gm.BoxMin} .. {gm.BoxMax}, sub-mesh bones " + string.Join(" | ", gm.SubMeshes.Select(s => string.Join(",", s.BoneIndices))));
            foreach (var mesh in md.Meshes)
            {
                var min = new System.Numerics.Vector3(float.MaxValue); var max = new System.Numerics.Vector3(float.MinValue);
                foreach (var v in mesh.Positions) { min = System.Numerics.Vector3.Min(min, v); max = System.Numerics.Vector3.Max(max, v); }
                Console.WriteLine($"  positions {mesh.Name}: {min} .. {max}, joints used " + string.Join(",", mesh.Joints.Where((j, i) => mesh.Weights[i] > 0).Distinct()));
            }
        }
    }
    if (o >= 0)
    {
        var glbs = PokeModelExporter.Ctr.CtrGlb.FromPack(pack);
        foreach (var (name, glb) in glbs)
        {
            string file = glbs.Count == 1 ? rest[o + 1] : Path.ChangeExtension(rest[o + 1], null) + "_" + name + ".glb";
            File.WriteAllBytes(file, glb);
            Console.WriteLine("  -> " + file);
        }
    }
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
                    string path = $"{(archive.Contains("ik_pokemon/catalog/catalog/poke_resource_table.trpmcatalog") ? "ik_pokemon" : "pokemon")}/data/pm{n:0000}/{id}/{id}.trmdl";
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

    case "facial": // facial <dex> [form] [gender]: the animation controller's face layers, look-at and state flags
    {
        var entry = FindEntry(SvCatalog.Open(archive.Read), archive, rest);
        if (entry == null) { Console.Error.WriteLine("no model"); return 1; }
        string folder = entry.DataRoot + entry.Folder;
        string modelName = Path.GetFileNameWithoutExtension(entry.ModelPath);
        var controller = SvAnimController.Read(modelName, file => archive.Read(folder + file));
        Console.WriteLine($"{modelName}: {controller.Faces.Count} face layers, {controller.ClipFlags.Count} body clips with states");
        foreach (var face in controller.Faces)
        {
            Console.WriteLine($"  {face.Name}: clip {face.Clip}, parameter {face.Parameter ?? "-"}, trigger {face.Trigger ?? "-"}");
            foreach (var p in face.Poses) Console.WriteLine($"    {face.Parameter} = {p.Value}: {p.Name} at t {p.Time:0.###}");
            if (face.Blink.Count > 0) Console.WriteLine("    blink: " + string.Join(" -> ", face.Blink.Select(b => $"{b.From:0.##}..{b.To:0.##} x{b.Speed:0.#}")));
        }
        if (controller.LookAt is SvAnimController.LookAtInfo look)
        {
            Console.WriteLine($"  look-at: fade angle {look.MaxAngle} deg, {look.Bones.Count} bones, {look.Eyes.Count} eyes");
            foreach (var b in look.Bones) Console.WriteLine($"    bone {b.Name,-10} yaw {b.YawLeft}/{b.YawRight} pitch {b.Up}/{b.Down} smooth {b.Smooth}");
            foreach (var e in look.Eyes)
                Console.WriteLine($"    eye {e.Material,-8} on {e.Bone} mesh {e.Mesh} yaw {e.YawLeft}/{e.YawRight} pitch {e.Up}/{e.Down} smooth {e.Smooth} offset ({string.Join(", ", e.Offset.Select(v => v.ToString("0.###")))}) uv left ({e.Map[0]:0.###}, {e.Map[1]:0.###}) up ({e.Map[3]:0.###}, {e.Map[4]:0.###}) right ({e.Map[6]:0.###}, {e.Map[7]:0.###}) tracks {string.Join(" ", e.Tracks)}");
        }
        foreach (var kv in controller.ClipFlags.OrderBy(k => k.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {kv.Key,-36} {string.Join(" ", kv.Value)}");
        break;
    }

    case "states": // states <dex> [form] [gender] | states <folder/> <model>: the body's state machine (how the game chains the clips)
    {
        string folder, modelName;
        if (rest.Count > 2 && rest[1].Contains('/'))
        {
            // A person's motion folder (chara/motion_pc/base/ p0: its p0_base.tracs).
            folder = rest[1].EndsWith("/") ? rest[1] : rest[1] + "/";
            modelName = rest[2];
        }
        else
        {
            var entry = FindEntry(SvCatalog.Open(archive.Read), archive, rest);
            if (entry == null) { Console.Error.WriteLine("no model"); return 1; }
            folder = entry.DataRoot + entry.Folder;
            modelName = Path.GetFileNameWithoutExtension(entry.ModelPath);
        }
        var controller = SvAnimController.Read(modelName, file => archive.Read(folder + file));
        if (controller.Body == null) { Console.Error.WriteLine("no body machine"); return 1; }
        static string Kind(uint type) => type switch { 1 => "entry", 2 => "exit", 3 => "any", 4 => "top", _ => "" };
        static string Func(uint f) => f switch { 0 => "", 1 => ">", 2 => "<", 3 => "!=", 4 => "==", 5 => ">=", 6 => "<=", _ => "f" + f };
        void Print(SvAnimController.State s, int depth)
        {
            string pad = new string(' ', depth * 2);
            string head = s.Clip != null ? $"{s.Leaf}  clip {s.Clip} x{s.Speed:0.##}" : $"{s.Leaf} {Kind(s.Type)}";
            if (s.Flags.Count > 0) head += "  [" + string.Join(" ", s.Flags) + "]";
            if (!string.IsNullOrEmpty(s.Tag)) head += "  tag " + s.Tag;
            Console.WriteLine(pad + head);
            foreach (var t in s.Transitions)
            {
                string conds = string.Join(" & ", t.Conditions.Select(c => c.func == 0 && c.value == 0 ? c.name : $"{c.name} {Func(c.func)} {c.value}"));
                string target = t.Path.StartsWith(s.Path.Substring(0, Math.Max(0, s.Path.LastIndexOf('/') + 1))) ? t.Path.Substring(s.Path.LastIndexOf('/') + 1) : t.Path;
                Console.WriteLine($"{pad}  -> {target}{(t.HasExitTime ? $" at {t.ExitTime:0.##}" : "")} blend {t.Duration:0.##}{(t.Offset != 0 ? $" offset {t.Offset:0.##}" : "")}{(t.CanInterrupt ? " interruptible" : "")}{(conds.Length > 0 ? " when " + conds : "")}{(t.Type != 0 ? " type " + t.Type : "")}");
            }
            foreach (var c in s.Children) Print(c, depth + 1);
        }
        Print(controller.Body, 0);
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
        var cat = FbTable.Root(archive.Read(SvCatalog.Layouts.Select(l => l.Catalog).First(archive.Contains)));
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
        var catalog = SvCatalog.Open(archive.Read);
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
        var catalog = SvCatalog.Open(archive.Read);
        var entry = FindEntry(catalog, archive, rest);
        var model = LoadModelFrom(archive, rest, out string folder);
        string dir = folder;
        Func<string, byte[]> read = file => archive.Read(dir + file);
        if (!rest.Contains("--no-anims")) LoadClips(archive, model, dir, rest);
        if (!rest.Contains("--no-ssc")) Console.Error.WriteLine($"scale compensation: {ScaleCompensation.Apply(model)} helper nodes");
        var folderFiles = FolderFiles(rest, folder);

        // Textures: plain maps decoded, layered materials (eyes, flames...) baked to color/emission;
        // everything else the materials, icons or folder hold kept as unreferenced images.
        var baked = SvMaterialBaker.Bake(model, read, unfoldAll: true);
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
            int lods = TrinityModelReader.LodCount(archive.Read(entry.DataRoot + entry.ModelPath));
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
            string json = SvSidecar.Build(entry, catalog.Game, read, schemas.Get, folderFiles);
            string sidecar = Path.ChangeExtension(outFile, ".json");
            File.WriteAllText(sidecar, json);
            Console.WriteLine($"{json.Length} chars -> {sidecar}");
        }
        break;
    }

    case "locators": // locators <national dex>: every effect-locator node (name, local parent, effect_node bone, transform)
    {
        var catalog = SvCatalog.Open(archive.Read);
        var entry = FindEntry(catalog, archive, rest);
        foreach (var file in entry.LocatorFiles())
        {
            var data = archive.Read(entry.DataRoot + entry.Folder + file);
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
        var catalog = SvCatalog.Open(archive.Read);
        var entry = FindEntry(catalog, archive, rest);
        string folder = entry.DataRoot + entry.Folder;
        var mdl = FbTable.Root(archive.Read(entry.DataRoot + entry.ModelPath));
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

    case "camera": // camera <.trcma path in the archive> [--every N]: a camera animation's tracks (Terastal cinematic cuts)
    {
        var data = archive.Read(rest[1]);
        if (data == null) { Console.Error.WriteLine("not found: " + rest[1]); return 1; }
        var cam = TrinityAnimationReader.ReadCamera(data);
        int ev = rest.IndexOf("--every");
        int every = ev >= 0 ? int.Parse(rest[ev + 1]) : 10;
        Console.WriteLine($"{rest[1]}: '{cam.Name}' {cam.FrameCount} frames at {cam.FramesPerSecond} fps");
        string K<T>(string label, Keys<T> k) => k == null ? $"  {label}: -" : $"  {label}: {k.Values.Length} keys";
        Console.WriteLine(K("translation", cam.Translation) + K("rotation", cam.Rotation) + K("fov", cam.Fov) + K("clip", cam.ClipStart) + K("unk2", cam.Unknown2) + K("unk3", cam.Unknown3) + K("unk4", cam.Unknown4));
        T At<T>(Keys<T> k, float f) { if (k == null) return default; int i = 0; while (i + 1 < k.Times.Length && k.Times[i + 1] <= f) i++; return k.Values[i]; }
        for (int f = 0; f <= cam.FrameCount; f += every)
        {
            var q = At(cam.Rotation, f);
            var e = q == default ? System.Numerics.Vector3.Zero : QuatToEuler(q);
            Console.WriteLine($"  {f,4}  pos {At(cam.Translation, f)}  rot(deg xyz) ({e.X:0.0}, {e.Y:0.0}, {e.Z:0.0})  fov {At(cam.Fov, f):0.###}  unk2 {At(cam.Unknown2, f)}  unk3 {At(cam.Unknown3, f)}  unk4 {At(cam.Unknown4, f):0.###}");
        }
        break;
    }

    case "translate": // translate <dir with NV program .txt files> [--out <dir>]: each to HLSL (NvToHlsl); counts what isn't supported
    {
        int o2 = rest.IndexOf("--out");
        string outDir = o2 >= 0 ? Path.GetFullPath(rest[o2 + 1]) : null;
        if (outDir != null) Directory.CreateDirectory(outDir);
        var problems = new Dictionary<string, int>();
        int ok = 0, total = 0;
        foreach (var f in Directory.GetFiles(rest[1], "*.txt"))
        {
            total++;
            try
            {
                var r = NvToHlsl.Translate(File.ReadAllText(f), "Sv_" + Path.GetFileNameWithoutExtension(f).Replace('.', '_'));
                ok++;
                if (outDir != null) File.WriteAllText(Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + ".hlsl"), r.Code);
            }
            catch (NotSupportedException e) { problems[e.Message] = problems.TryGetValue(e.Message, out int n) ? n + 1 : 1; }
        }
        Console.WriteLine($"{ok}/{total} translated");
        foreach (var kv in problems.OrderByDescending(x => x.Value).Take(30)) Console.WriteLine($"  {kv.Value}  {kv.Key}");
        break;
    }

    case "shaderabi": // shaderabi <list file of .ptcl archive paths>: which slot each named input / sampler / uniform block takes, over every SV-custom variation
    {
        var seen = new SortedDictionary<string, SortedDictionary<int, int>>();
        string[] kinds = { "in", "out", "sampler", "block", "storage" };
        foreach (var path in File.ReadLines(rest[1]))
        {
            var data = archive.Read(path.Trim());
            if (data == null) continue;
            VfxbFile file;
            try { file = VfxbFile.Read(data); } catch { continue; }
            var done = new HashSet<int>();
            void Walk(IEnumerable<VfxbEmitter> list)
            {
                foreach (var e in list)
                {
                    if (e.CustomShaderIndex == 1 && done.Add(e.ShaderIndex) && file.Reflection(e.ShaderIndex) is VfxbShaderReflection r)
                        foreach (var (stage, kindsOf) in new[] { ("vs", r.Vertex), ("fs", r.Fragment) })
                            for (int i = 0; i < 5; i++)
                                foreach (var (name, slot) in kindsOf[i])
                                {
                                    string key = $"{stage} {kinds[i]} {name}";
                                    if (!seen.TryGetValue(key, out var slots)) seen[key] = slots = new SortedDictionary<int, int>();
                                    slots[slot] = slots.TryGetValue(slot, out int n) ? n + 1 : 1;
                                }
                    Walk(e.Children);
                }
            }
            foreach (var set in file.Sets) Walk(set.Emitters);
        }
        foreach (var kv in seen) Console.WriteLine($"{kv.Key}: " + string.Join(" ", kv.Value.Select(x => $"{x.Key}x{x.Value}")));
        break;
    }

    case "programs": // programs <list file of .ptcl archive paths> --out <dir>: SV-custom emitters' fragment programs grouped by text
    {
        int o = rest.IndexOf("--out");
        string outDir = Path.GetFullPath(rest[o + 1]);
        Directory.CreateDirectory(outDir);
        var groups = new Dictionary<string, (string sample, List<string> users)>();
        foreach (var path in File.ReadLines(rest[1]))
        {
            var data = archive.Read(path.Trim());
            if (data == null) continue;
            VfxbFile file;
            try { file = VfxbFile.Read(data); } catch { continue; }
            void Walk(IEnumerable<VfxbEmitter> list)
            {
                foreach (var e in list)
                {
                    if (e.CustomShaderIndex == 1 && file.FragmentProgram(e.ShaderIndex) is string fp)
                    {
                        string key = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.ASCII.GetBytes(fp)))[..10];
                        if (!groups.TryGetValue(key, out var g)) groups[key] = g = (fp, new List<string>());
                        g.users.Add(Path.GetFileNameWithoutExtension(path) + "/" + e.Name);
                        // Each vertex program paired with it (they decide what its inputs hold), once per distinct text.
                        if (file.VertexProgram(e.ShaderIndex) is string vp)
                        {
                            string vk = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.ASCII.GetBytes(vp)))[..10];
                            string vpath = Path.Combine(outDir, key + ".vp." + vk + ".txt");
                            if (!File.Exists(vpath)) File.WriteAllText(vpath, vp);
                            if (file.Reflection(e.ShaderIndex) is VfxbShaderReflection refl)
                            {
                                string[] kinds = { "in", "out", "sampler", "block", "storage" };
                                var lines = new List<string>();
                                foreach (var (stage, kindList) in new[] { ("vs", refl.Vertex), ("fs", refl.Fragment) })
                                    for (int i = 0; i < 5; i++) foreach (var (n, slot) in kindList[i]) lines.Add($"{stage} {kinds[i]} {n} {slot}");
                                File.WriteAllLines(Path.ChangeExtension(vpath, ".refl"), lines);
                            }
                        }
                    }
                    Walk(e.Children);
                }
            }
            foreach (var set in file.Sets) Walk(set.Emitters);
        }
        foreach (var kv in groups)
        {
            File.WriteAllText(Path.Combine(outDir, kv.Key + ".txt"), kv.Value.sample);
            File.WriteAllLines(Path.Combine(outDir, kv.Key + ".users"), kv.Value.users);
        }
        Console.WriteLine($"{groups.Count} distinct SV-custom fragment programs, {groups.Values.Sum(g => g.users.Count)} emitters -> {outDir}");
        break;
    }

    case "skeletons": // skeletons: every catalog model's segment-scale-compensate flags (trskl bones[].inherit_position)
    {
        var catalog = SvCatalog.Open(archive.Read);
        var values = new Dictionary<int, int>();
        int models = 0, mixed = 0, none = 0;
        foreach (var path in catalog.Entries.Select(e => e.ModelPath).Distinct())
        {
            var mdlData = archive.Read(catalog.DataRoot + path);
            if (mdlData == null) continue;
            string dir = catalog.DataRoot + path.Substring(0, path.LastIndexOf('/') + 1);
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
        var personal = new SvPersonal(archive.Read(SvPersonal.Path), SvPersonal.FieldShift(SvCatalog.Open(archive.Read)?.Game));
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

    case "chara": // chara <model path> <motion folder> [--clips <regex>] [--resource <name>, e.g. p0_base for an NPC playing the player's clips] --out <file.glb>: a person's skeleton + clips (the trainer's motions)
    {
        int o = rest.IndexOf("--out"), c = rest.IndexOf("--clips"), rs = rest.IndexOf("--resource");
        var pattern = c >= 0 ? new System.Text.RegularExpressions.Regex(rest[c + 1]) : null;
        var glb = SvExporter.ExportCharacter(archive.Read, rest[1], rest[2], n => pattern == null || pattern.IsMatch(n), out var names, rs >= 0 ? rest[rs + 1] : null);
        if (glb == null) { Console.Error.WriteLine("not found: " + rest[1]); return 1; }
        string outFile = Path.GetFullPath(o >= 0 ? rest[o + 1] : "chara.glb");
        File.WriteAllBytes(outFile, glb);
        Console.WriteLine($"{names.Count} clips, {glb.Length / 1024} KB -> {outFile}");
        foreach (var n in names) Console.WriteLine("  " + n);
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
    case "tranm": // tranm <clip path in the archive>...: a skeletal clip's tracks (frames, per bone the keyed channels and their ranges)
    {
        foreach (var path in rest.Skip(1).Where(a => !a.StartsWith("--")))
        {
            var data = archive.Read(path);
            if (data == null) { Console.Error.WriteLine("not found: " + path); return 1; }
            var anim = TrinityAnimationReader.Read(data, Path.GetFileNameWithoutExtension(path));
            Console.WriteLine($"{path}: {anim.FrameCount} frames @ {anim.FramesPerSecond} fps{(anim.Loop ? ", loop" : "")}");
            string Range(float[] v) => v.Length == 0 ? "-" : $"{v.Min():0.###}..{v.Max():0.###}";
            foreach (var t in anim.Tracks)
            {
                var parts = new List<string>();
                if (t.Translation != null && t.Translation.Times.Length > 1)
                    parts.Add($"T[{t.Translation.Times.Length}] x {Range(t.Translation.Values.Select(v => v.X).ToArray())} y {Range(t.Translation.Values.Select(v => v.Y).ToArray())} z {Range(t.Translation.Values.Select(v => v.Z).ToArray())}");
                if (t.Rotation != null && t.Rotation.Times.Length > 1)
                {
                    // The turn from the first key, in degrees, to see which bone moves and how far.
                    var q0 = t.Rotation.Values[0];
                    float most = 0;
                    foreach (var q in t.Rotation.Values)
                    {
                        float dot = Math.Abs(q.X * q0.X + q.Y * q0.Y + q.Z * q0.Z + q.W * q0.W);
                        most = Math.Max(most, (float)(2 * Math.Acos(Math.Min(1, dot)) * 180 / Math.PI));
                    }
                    parts.Add($"R[{t.Rotation.Times.Length}] up to {most:0.#} deg");
                    if (rest.Contains("--keys")) // every key: frame, angle from the first key and the turn's axis
                        for (int i = 0; i < t.Rotation.Times.Length; i++)
                        {
                            var q = t.Rotation.Values[i];
                            // q0^-1 * q
                            var d = new System.Numerics.Quaternion(q0.W * q.X - q0.X * q.W - q0.Y * q.Z + q0.Z * q.Y, q0.W * q.Y + q0.X * q.Z - q0.Y * q.W - q0.Z * q.X,
                                q0.W * q.Z - q0.X * q.Y + q0.Y * q.X - q0.Z * q.W, q0.W * q.W + q0.X * q.X + q0.Y * q.Y + q0.Z * q.Z);
                            float angle = (float)(2 * Math.Acos(Math.Min(1, Math.Abs(d.W))) * 180 / Math.PI);
                            float s = (float)Math.Sqrt(Math.Max(1e-12, 1 - d.W * d.W));
                            parts.Add($"\n      f{t.Rotation.Times[i]:0.#} {angle:0.#} deg axis ({d.X / s:0.##}, {d.Y / s:0.##}, {d.Z / s:0.##})");
                        }
                }
                if (t.Scale != null && t.Scale.Times.Length > 1)
                    parts.Add($"S[{t.Scale.Times.Length}] {Range(t.Scale.Values.Select(v => v.X).ToArray())}");
                if (parts.Count > 0) Console.WriteLine($"  {t.Bone,-24} {string.Join("  ", parts)}");
            }
        }
        break;
    }

    case "tracm": // tracm <clip path in the archive>...: a clip's non-skeletal tracks (material parameters, mesh visibility, blend shapes) with their keys
    {
        foreach (var path in rest.Skip(1).Where(a => !a.StartsWith("--")))
        {
            var data = archive.Read(path);
            if (data == null) { Console.Error.WriteLine("not found: " + path); return 1; }
            var anim = new AnimationData { Name = Path.GetFileNameWithoutExtension(path) };
            TrinityCurveReader.Read(data, anim);
            Console.WriteLine($"{path}: {anim.FrameCount} frames @ {anim.FramesPerSecond} fps");
            string Keys<T>(Keys<T> k, Func<T, string> f) => k == null ? "-" : string.Join(" ", k.Times.Select((t, i) => $"f{t:0.#}={f(k.Values[i])}"));
            string F(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            foreach (var t in anim.MaterialTracks)
                Console.WriteLine($"  material {t.Mesh} / {t.Material} . {t.Parameter}\n    R {Keys(t.R, F)}\n    G {Keys(t.G, F)}\n    B {Keys(t.B, F)}\n    A {Keys(t.A, F)}");
            foreach (var t in anim.VisibilityTracks)
                Console.WriteLine($"  visible {t.Mesh}: {(t.Fixed.HasValue ? (t.Fixed.Value ? "always" : "never") : Keys(t.Keys, b => b ? "on" : "off"))}");
            foreach (var t in anim.MorphTracks)
                Console.WriteLine($"  shape {t.Mesh} / {t.Shape}: {Keys(t.Weight, F)}");
        }
        break;
    }

    case "faces": // faces <face motion folder> --hashes <hashes_inside_fd.txt> [--clips <regex>]: per clip, the blend shapes it moves (peak weight); then every shape's use count
    {
        string folder = rest.Skip(1).FirstOrDefault(a => !a.StartsWith("--"))?.TrimEnd('/') + "/";
        int h = rest.IndexOf("--hashes"), c = rest.IndexOf("--clips");
        if (h < 0) { Console.Error.WriteLine("faces needs --hashes"); return 1; }
        var pick = c >= 0 ? new System.Text.RegularExpressions.Regex(rest[c + 1]) : null;
        var files = File.ReadLines(rest[h + 1]).Select(l => l.Substring(l.IndexOf(' ') + 1).Trim())
            .Where(p => p.StartsWith(folder, StringComparison.Ordinal) && p.EndsWith(".tracm", StringComparison.Ordinal)).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        var uses = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in files)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (pick != null && !pick.IsMatch(name)) continue;
            var data = archive.Read(path);
            if (data == null) continue;
            var anim = new AnimationData { Name = name };
            try { TrinityCurveReader.Read(data, anim); } catch (Exception e) { Console.WriteLine($"{name}: unreadable ({e.Message})"); continue; }
            var moved = anim.MorphTracks.Where(t => t.Weight != null && t.Weight.Values.Length > 0 && t.Weight.Values.Max() > 0.05f)
                .Select(t => (shape: t.Shape, peak: t.Weight.Values.Max(), last: t.Weight.Times.Max())).ToList();
            foreach (var m in moved) uses[m.shape] = uses.TryGetValue(m.shape, out int n) ? n + 1 : 1;
            int frames = anim.MorphTracks.Where(t => t.Weight != null).Select(t => (int)t.Weight.Times.DefaultIfEmpty(0).Max()).DefaultIfEmpty(0).Max();
            Console.WriteLine($"{name} ({frames} f): {string.Join(", ", moved.Select(m => $"{m.shape} {m.peak:0.##}"))}");
        }
        Console.WriteLine("\nshapes by clips using them:");
        foreach (var kv in uses.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  {kv.Value,4}  {kv.Key}");
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
        // --svshaders <dir>: also translate SV-custom emitters' compiled shaders into Unity shaders there (SvShaderGen).
        int svs = rest.IndexOf("--svshaders");
        if (svs >= 0) SvShaderGen.OutDir = Path.GetFullPath(rest[svs + 1]);
        Directory.CreateDirectory(outDir);
        // Only some effects (move numbers, or the negative keys below: -2104 = the Poke Ball's eb004): textures.json and
        // primitives.json are then merged into the existing ones instead of rewritten from this subset.
        var only = new HashSet<int>(rest.Skip(1).Where(a => int.TryParse(a, out _)).Select(int.Parse).Where(n => n != 0 && (h < 0 || n.ToString() != rest[h + 1])));
        var byMove = new SortedDictionary<int, List<string>>();
        const int Picnic = -4001;
        foreach (var line in File.ReadLines(rest[h + 1]))
        {
            string path = line.Substring(line.IndexOf(' ') + 1);
            var m = System.Text.RegularExpressions.Regex.Match(path, @"^effect/battle_ew/ew(\d{4})/(ew\d{4}(?:_\d+|_[a-z][a-z0-9_]*)?)\.(trtml|trsot)$|^effect/battle_ew/ew(\d{4})/(ew\d{4}(?:_\d+)?)_0\.trsot$");
            // The battle's system effects (stat up / down, heal, statuses, Substitute...): es001.. as -1, -2... (es<n>.json).
            // The five statuses (es001-005) keep their scene objects and particles in effect/field/status/es<n>/.
            var es = System.Text.RegularExpressions.Regex.Match(path, @"^effect/(?:battle_es|field/status)/es(\d{3})/es\d{3}(?:_0)?\.(trtml|trsot)$");
            // Battle events the stage plays: items (ee004 one used by the trainer, ee005 a held one taking effect, ee006 a
            // berry eaten), terrains starting (ee050 Grassy, ee051 Misty, ee052 Electric, ee053 Psychic),
            // entry hazards hurting a Pokemon as it comes in (ee040 Spikes, ee041 Toxic
            // Spikes, ee042 Stealth Rock), as -1040.. (ee<n>.json), and a thrown ball's effects (ee101: the
            // trail behind it, the flash as it opens, the beam drawing the Pokemon in, the close; no timeline, the game's
            // code times them) as -1101.
            var ee = System.Text.RegularExpressions.Regex.Match(path, @"^effect/battle_ee/ee(00[4-6]|04[0-2]|05[0-3]|101)/ee\d{3}(?:_0)?\.(trtml|trsot)$");
            // Weather on the field (etb001 rain, etb002 snow, etb003 sandstorm, etb004 sun), as -2001.. (etb<n>.json):
            // looping particles and an ambient sound, no timeline.
            var etb = System.Text.RegularExpressions.Regex.Match(path, @"^effect/battle_etb/etb(\d{3})/etb\d{3}_0\.trsot$");
            // Each kind of ball's effects (eb001 Master Ball .. eb027 Strange Ball, numbered like world/data/battle/
            // plib_ball_conversion's plibID): "ballout" when a Pokemon comes out of it, "capture" when one is drawn in;
            // as -2101.. (eb<n>.json), no timeline either.
            var eb = System.Text.RegularExpressions.Regex.Match(path, @"^effect/battle_eb/eb(\d{3})/eb\d{3}_0\.trsot$");
            // The picnic's effects (world/obj_template/parts/picnic/effect/<name>_/<name>_0.trsot -> effect/appli/picnic*):
            // the Pokemon's emotions (picnic_heart_1..5, picnic_happy, picnic_laugh, picnic_onpu notes, picnic_question,
            // picnic_exclamation, picnic_ase sweat, picnic_moyamoya, picnic_sleep...) and the wash (picnic_awa foam,
            // picnic_awabreak, picnic_shower, picnic_waterdrop, picnic_washedkirakira), all in one file as -4001
            // (picnic.json), no timeline: the game starts them from code. Used by the petting / washing screen.
            bool picnic = System.Text.RegularExpressions.Regex.IsMatch(path, @"^world/obj_template/parts/picnic/effect/[a-z0-9_]+_/[a-z0-9_]+_0\.trsot$");
            if (!m.Success && !es.Success && !ee.Success && !etb.Success && !eb.Success && !picnic) continue;
            int move = picnic ? Picnic : es.Success ? -int.Parse(es.Groups[1].Value) : ee.Success ? -1000 - int.Parse(ee.Groups[1].Value)
                : etb.Success ? -2000 - int.Parse(etb.Groups[1].Value) : eb.Success ? -2100 - int.Parse(eb.Groups[1].Value)
                : int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[4].Value);
            if (only.Count > 0 && !only.Contains(move)) continue;
            if (!byMove.TryGetValue(move, out var list)) byMove[move] = list = new List<string>();
            list.Add(path);
        }
        // Terastallization (Docs/terastal-research.md), as -3001 (tera.json): the cinematic's timeline from demo/ with its
        // shared scene objects, every type's (ej_transform_start00..17, 99) and its camera cuts.
        const int Tera = -3001;
        const int BallThrow = -1101; // effect/battle_ee/ee101: a thrown ball's effects, a scene without a timeline
        var teraTimeline = "demo/ej/ej_transform01/ej_transform01.trtml";
        bool wantTera = only.Count == 0 || only.Contains(Tera);
        if (wantTera) byMove[Tera] = new List<string> { teraTimeline };
        // Mega Evolution (Docs/mega-research.md), as -3002 (mega.json), from Legends Z-A (--game <legends-za>): the Pokemon's
        // part of the cinematic (ik_demo/ee/m_shinka_poke) and every species' own (m_shinka_pm<dex>[x|y|z]), sharing one
        // scene file (m_shinka_cmn). The trainer's parts (m_shinka_pc, m_shinka_tr*) wait for trainer models. Only this from
        // Z-A: textures.json / primitives.json are merged into the existing ones.
        const int Mega = -3002;
        // Evolution (Scarlet/Violet's overworld scene, effect/demo/sd9110_evolution), as -3003 (evolution.json): before
        // (the glow and the charge), the change (outdoor / indoor), after_success, after_cancel; outdoor and indoor
        // variants with their own scene files. TARGET_1 is the old form (ATK), TARGET_2 the new (DEF).
        const int Evolution = -3003;
        const string EvolutionFolder = "effect/demo/sd9110_evolution/evolution";
        bool wantEvolution = only.Count == 0 || only.Contains(Evolution);
        if (wantEvolution)
            byMove[Evolution] = File.ReadLines(rest[h + 1]).Select(l => l.Substring(l.IndexOf(' ') + 1))
                .Where(p => p.StartsWith(EvolutionFolder + "/", StringComparison.Ordinal) && (p.EndsWith(".trtml", StringComparison.Ordinal) || p.EndsWith("_0.trsot", StringComparison.Ordinal))).ToList();
        bool za = SvCatalog.Open(archive.Read)?.Game == "legends-za";
        byte[] megaScene = null;
        if (za)
        {
            byMove.Clear();
            megaScene = archive.Read("world/ik_obj_template/effect/demo/em/m_shinka_cmn_/m_shinka_cmn_0.trsot");
            byMove[Mega] = File.ReadLines(rest[h + 1]).Select(l => l.Substring(l.IndexOf(' ') + 1))
                .Where(p => System.Text.RegularExpressions.Regex.IsMatch(p, @"^ik_demo/ee/m_shinka_(poke|pm[0-9a-z_]+)/m_shinka_[a-z0-9_]+\.trtml$")).ToList();
            Console.WriteLine($"Mega Evolution: {byMove[Mega].Count} timelines");
        }
        // The overworld's mood icons (effect/field/icon/ef_icon: ef_wakunasi_angry, _hunger, _exclamation, _question, _onpu,
        // _ase, _moyamoya, _silence and their _z variants, ef_symbol_*): the wild Pokemon's balloons, started from code;
        // into picnic.json beside the picnic's own.
        var icons = File.ReadLines(rest[h + 1]).Select(l => l.Substring(l.IndexOf(' ') + 1))
            .Where(p => System.Text.RegularExpressions.Regex.IsMatch(p, @"^effect/field/icon/ef_icon/ef_[a-z0-9_]+\.ptcl$")).ToList();
        long total = 0;
        int files = 0, emitters = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var textureSources = new SortedDictionary<string, (string path, int cx, int cy)>(StringComparer.Ordinal);
        var primitiveShapes = new SortedDictionary<string, FxPrimitiveShape>(StringComparer.Ordinal);
        // Terastallization last: textures and primitives keep the move files they were first analysed from.
        foreach (var kv in byMove.OrderBy(k => k.Key == Tera ? 1 : 0).ThenBy(k => k.Key))
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
            if (timelines.Count == 0 && kv.Key > -2000 && kv.Key != BallThrow) continue;
            // The picnic's scene files each hold one effect; keyed by their folder so none replaces another.
            if (kv.Key == Picnic) { scenes.Clear(); foreach (var path in kv.Value) if (archive.Read(path) is byte[] sot) scenes[path] = sot; }
            if (kv.Key == Mega && megaScene != null) foreach (var stem in timelines.Keys) scenes[stem] = megaScene;
            if (kv.Key == Evolution)
            {
                // Each timeline plays with its own place's scene: the indoor ones with sd9110_evolution_indoor.
                var outdoor = scenes.TryGetValue("sd9110_evolution", out var o0) ? o0 : null;
                var indoor = scenes.TryGetValue("sd9110_evolution_indoor", out var i0) ? i0 : outdoor;
                foreach (var stem in timelines.Keys.ToList()) scenes[stem] = stem.Contains("indoor") ? indoor : outdoor;
            }
            // Later hits of multi-hit moves (ew0024_2: Double Kick's second kick) have no scene objects of their own:
            // they play the first hit's objects (73 timelines). _1.trsot files repeat the _0 ones.
            if (scenes.Count > 0)
            {
                var first = scenes.OrderBy(k => k.Key, StringComparer.Ordinal).First().Value;
                foreach (var stem in timelines.Keys) if (!scenes.ContainsKey(stem)) scenes[stem] = first;
            }
            Dictionary<string, IReadOnlyList<byte[]>> extraScenes = null;
            Dictionary<string, TrinityAnimationReader.CameraClip> cameras = null;
            if (kv.Key == Tera)
            {
                var start = new List<byte[]>();
                foreach (var t in new[] { "" }.Concat(Enumerable.Range(0, 18).Select(i => i.ToString("D2"))).Append("99"))
                    if (archive.Read($"effect/battle_ej/ej_transform_start{t}/ej_transform_start{t}_0.trsot") is byte[] sot) start.Add(sot);
                extraScenes = new Dictionary<string, IReadOnlyList<byte[]>> { ["ej_transform01"] = start };
                cameras = new Dictionary<string, TrinityAnimationReader.CameraClip>();
                for (int c = 1; c <= 9; c++)
                    if (archive.Read($"demo/ej/ej_transform01/ej_transform01_cam_c{c:D3}.trcma") is byte[] cma)
                        cameras[$"ej_transform01_cam_c{c:D3}"] = TrinityAnimationReader.ReadCamera(cma);
            }
            string folder = kv.Key == Picnic ? "effect/appli/picnic" : kv.Key == Mega ? "ik_demo/ee/m_shinka" : kv.Key == Tera ? "demo/ej/ej_transform01"
                : kv.Key <= -2100 ? $"effect/battle_eb/eb{-2100 - kv.Key:D3}" : kv.Key <= -2000 ? $"effect/battle_etb/etb{-2000 - kv.Key:D3}"
                : kv.Key <= -1000 ? $"effect/battle_ee/ee{-1000 - kv.Key:D3}" : kv.Key < 0 ? $"effect/battle_es/es{-kv.Key:D3}" : null;
            string json = SvMoveFx.Export(kv.Key, timelines, scenes, archive.Read, out int count,
                (path, name, cx, cy) => { if (!textureSources.ContainsKey(name)) textureSources[name] = (path, cx, cy); },
                (path, name, prim) => { if (!primitiveShapes.ContainsKey(name)) { var shape = FxPrimitiveShape.Analyze(prim); shape.Source = path; primitiveShapes[name] = shape; } },
                kv.Key == Evolution ? EvolutionFolder + "/sd9110_evolution" : folder, extraScenes, cameras, kv.Key == Picnic ? icons : null,
                kv.Key == Evolution ? new Dictionary<string, string> { ["TARGET_1"] = "ATK", ["TARGET_2"] = "DEF" } : null,
                kv.Key == Evolution ? name => name.StartsWith("eff.", StringComparison.Ordinal) && archive.Read($"{EvolutionFolder}/sd9110_{name.Substring(4)}.ptcl") != null
                    ? $"{EvolutionFolder}/sd9110_{name.Substring(4)}.ptcl" : null : null);
            string file = Path.Combine(outDir, kv.Key == Picnic ? "picnic.json" : kv.Key == Mega ? "mega.json" : kv.Key == Tera ? "tera.json" : kv.Key == Evolution ? "evolution.json"
                : kv.Key <= -2100 ? $"eb{-2100 - kv.Key:D3}.json" : kv.Key <= -2000 ? $"etb{-2000 - kv.Key:D3}.json"
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
        bool merge = za || only.Count > 0; // Z-A's Mega and partial exports add to the tables instead of replacing them
        File.WriteAllText(Path.Combine(outDir, "primitives.json"), merge ? MergeJson(Path.Combine(outDir, "primitives.json"), pw.ToString()) : pw.ToString());
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
        File.WriteAllText(Path.Combine(outDir, "textures.json"), merge ? MergeJson(Path.Combine(outDir, "textures.json"), tw.ToString()) : tw.ToString());
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
        var auditEntry = SvCatalog.Open(archive.Read).Find(int.Parse(rest[1]));
        var mdl = FbTable.Root(archive.Read(auditEntry.DataRoot + auditEntry.ModelPath));
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

// Degrees about X, Y, Z (applied X first) for printing.
static System.Numerics.Vector3 QuatToEuler(System.Numerics.Quaternion q)
{
    double sinx = 2 * (q.W * q.X + q.Y * q.Z), cosx = 1 - 2 * (q.X * q.X + q.Y * q.Y);
    double siny = Math.Clamp(2 * (q.W * q.Y - q.Z * q.X), -1, 1);
    double sinz = 2 * (q.W * q.Z + q.X * q.Y), cosz = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
    const double d = 180 / Math.PI;
    return new System.Numerics.Vector3((float)(Math.Atan2(sinx, cosx) * d), (float)(Math.Asin(siny) * d), (float)(Math.Atan2(sinz, cosz) * d));
}

// An existing JSON object file with the entries of another added (existing names kept): Legends Z-A's effects join
// Scarlet/Violet's textures.json / primitives.json instead of replacing them.
static string MergeJson(string existingFile, string added)
{
    if (!File.Exists(existingFile)) return added;
    var into = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(existingFile)).AsObject();
    var more = System.Text.Json.Nodes.JsonNode.Parse(added).AsObject();
    foreach (var kv in more.ToList())
        if (!into.ContainsKey(kv.Key)) { more.Remove(kv.Key); into[kv.Key] = kv.Value; }
    return into.ToJsonString();
}

static ModelData LoadModel(TrinityArchive archive, List<string> rest) => LoadModelFrom(archive, rest, out _);

static ModelData LoadModelFrom(TrinityArchive archive, List<string> rest, out string folder)
{
    var catalog = SvCatalog.Open(archive.Read);
    var entry = FindEntry(catalog, archive, rest);
    folder = entry.DataRoot + entry.Folder;
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
    var personal = new SvPersonal(archive.Read(SvPersonal.Path), SvPersonal.FieldShift(SvCatalog.Open(archive.Read)?.Game));
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
    Console.WriteLine($"{indent}  draw: billboard {e.BillboardType} offset {e.OffsetType} blend {e.BlendType} combiner {e.ColorCombiner}/{e.AlphaCombiner} shader {e.ShaderType} soft {e.SoftParticle} flags {e.StaticFlags[0]:X8} inherit-flags path {e.InheritDrawPath} pre {e.PreDraw} a0f {e.InheritAlpha0EachFrame} a1f {e.InheritAlpha1EachFrame} own {e.EnableEmitterParticle} alphatest {e.AlphaTest} {e.AlphaFunc} {e.AlphaThreshold} @{e.AlphaThresholdOffset} fluct@{e.FluctuationOffset} pdata@{e.ParticleDataOffset} [{string.Join(" ", e.Record.Skip(e.ParticleDataOffset).Take(16).Select(b => b.ToString("X2")))}] tanim [{string.Join(" ", e.Record.Skip(e.FluctuationOffset + 12 + 3 * 32).Take(48).Select(b => b.ToString("X2")))}] tail@{e.StaticTailOffset} [{string.Join(" ", e.StaticTail.Select(x => x.ToString("0.###")))}]  textures {string.Join(", ", tex)}{(e.PrimitiveId != ulong.MaxValue ? $"  primitive {e.PrimitiveId:X}" : "")}");
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

