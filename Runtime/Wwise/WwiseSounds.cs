using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PokeModelExporter.Wwise
{
    // Finds the audio a Wwise event plays in a game's sound folder (soundbanks *.bnk + packages *.pck),
    // so it can be decoded (e.g. with vgmstream) into a normal sound file. Written from the public
    // Wwise bank layout (as documented by wwiser), tested on Scarlet/Violet (bank version 140).
    //   event (HIRC type 4) -> its actions (type 3) -> Play actions' target -> Sound (type 2) -> media ID
    //   media: embedded in a bank (DIDX/DATA) or streamed from a package's sound table.
    // Events are found by name: Wwise IDs are the FNV-1 32-bit hash of the lowercase name.
    // Play targets may be sounds or switch containers (a game variable picks the child: SV's cries
    // PLAY_PV_<internal species> switch on voice type INDEX/ATK/SPATK/SAD/GLAD1..., some first on
    // FORM00..), random/sequence containers (one child per play: footsteps, whooshes; pick it with variant) and layer
    // containers (every child at once). Sounds routed to the controller's vibration (Motion_Bus: HD rumble tracks, 4-channel
    // low tones layered under SV's Terastal sounds) are left out: through speakers they sound like a synth.
    // Music switches on game states (the ambience beds) pick the decision-tree path matching the switches asked for.
    public sealed class WwiseSounds
    {
        readonly string _folder;
        readonly Dictionary<string, Bank> _banks = new Dictionary<string, Bank>(StringComparer.OrdinalIgnoreCase);
        List<Package> _packages;

        // folder: the one holding Init.bnk (SV: <romfs>/audio/wwise/bin/Switch).
        public WwiseSounds(string folder)
        {
            _folder = folder;
        }

        // A sound folder inside an extracted game: <game>/audio/..., <game>/update/audio/..., <game>/base/audio/...
        public static string FindFolder(string gameFolder) =>
            new[] { "", "update", "base" }.Select(sub => Path.Combine(gameFolder, sub, "audio", "wwise", "bin", "Switch"))
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "Init.bnk")));

        public static uint Hash(string name)
        {
            uint h = 2166136261;
            foreach (byte b in Encoding.ASCII.GetBytes(name.ToLowerInvariant()))
            {
                h *= 16777619;
                h ^= b;
            }
            return h;
        }

        // Where the audio lives. Source = bank or package file name.
        public sealed record Media(uint Id, string Source, long Offset, int Size);

        // The media an event plays (several when it layers sounds), empty when it can't be resolved.
        // switches: preferred switch values, by name, in order (e.g. "INDEX", "FORM00"); a switch
        // container with none of them plays its first entry.
        public IReadOnlyList<Media> Resolve(string bankName, string eventName, params string[] switches) =>
            Resolve(bankName, eventName, 0, out _, switches);

        // variant: which child a random/sequence container plays (modulo its count); variants: the most children any of
        // them had (1 without one), so variant 0..variants-1 covers every take.
        public IReadOnlyList<Media> Resolve(string bankName, string eventName, int variant, out int variants, params string[] switches)
        {
            var wanted = switches.Select(Hash).ToArray();
            _variant = Math.Max(0, variant);
            _variants = 1;
            variants = 1;
            var bank = LoadBank(bankName);
            var result = new List<Media>();
            if (bank == null || !bank.Objects.TryGetValue(Hash(eventName), out var ev) || ev.Type != 4) return result;
            var r = new Reader(bank.Data, ev.Offset + 4);
            int actions = (int)r.VarUInt();
            for (int i = 0; i < actions; i++)
            {
                if (!bank.Objects.TryGetValue(r.U32(), out var action) || action.Type != 3) continue;
                var a = new Reader(bank.Data, action.Offset + 4);
                ushort type = a.U16();
                uint target = a.U32();
                if (type == 0x0403) Collect(bank, target, wanted, result, 0); // Play
            }
            variants = _variants;
            return result;
        }

        // An event's whole tree as text, for research (which switches pick what, how many takes, the media sizes): every
        // switch entry and container child, not just the ones Resolve would play. names: Wwise ID -> name (wwnames).
        public List<string> Tree(string bankName, string eventName, IReadOnlyDictionary<uint, string> names = null)
        {
            var lines = new List<string>();
            var bank = LoadBank(bankName);
            if (bank == null || !bank.Objects.TryGetValue(Hash(eventName), out var ev) || ev.Type != 4) return lines;
            string Name(uint id) => names != null && names.TryGetValue(id, out var n) ? n : id.ToString();
            var r = new Reader(bank.Data, ev.Offset + 4);
            int actions = (int)r.VarUInt();
            for (int i = 0; i < actions; i++)
            {
                uint aid = r.U32();
                if (!bank.Objects.TryGetValue(aid, out var action) || action.Type != 3) continue;
                var a = new Reader(bank.Data, action.Offset + 4);
                ushort type = a.U16();
                uint target = a.U32();
                lines.Add($"action 0x{type:X4} -> {Name(target)}");
                if (type == 0x0403) TreeOf(bank, target, 1, lines, Name);
            }
            return lines;
        }

        void TreeOf(Bank bank, uint id, int depth, List<string> lines, Func<uint, string> name)
        {
            string pad = new string(' ', depth * 2);
            if (depth > 10 || !bank.Objects.TryGetValue(id, out var obj)) { lines.Add($"{pad}{name(id)}: not in the bank"); return; }
            if (obj.Type == 2)
            {
                var s = new Reader(bank.Data, obj.Offset + 4);
                s.U32();
                byte stream = s.U8();
                uint media = s.U32();
                var found = stream == 0 ? bank.Embedded(media) : FindStreamed(media) ?? bank.Embedded(media);
                lines.Add($"{pad}sound {name(id)}: media {media} {(found == null ? "missing" : $"{found.Size} bytes in {found.Source}")}");
            }
            else if (obj.Type == 6 && SwitchList(bank, obj) is List<(uint Switch, uint[] Children)> entries)
            {
                lines.Add($"{pad}switch {name(id)} ({entries.Count} entries)");
                foreach (var e in entries)
                {
                    lines.Add($"{pad}  [{name(e.Switch)}]");
                    foreach (uint c in e.Children) TreeOf(bank, c, depth + 2, lines, name);
                }
            }
            else if (obj.Type == 5 && Playlist(bank, obj) is uint[] takes)
            {
                lines.Add($"{pad}random/sequence {name(id)} ({takes.Length} takes)");
                foreach (uint c in takes.Distinct()) TreeOf(bank, c, depth + 1, lines, name);
            }
            else if (obj.Type == 9 && LayerChildren(bank, obj, id) is uint[] layers)
            {
                lines.Add($"{pad}layers {name(id)} ({layers.Length})");
                foreach (uint c in layers) TreeOf(bank, c, depth + 1, lines, name);
            }
            else if (obj.Type == 11)
                foreach (var m in TrackSources(bank, obj)) lines.Add($"{pad}track {name(id)}: media {m.Id} {m.Size} bytes in {m.Source}");
            else if (obj.Type == 12 && Decisions(bank, obj) is (uint[] Groups, List<(uint[] Path, uint Node)> Leaves) tree)
            {
                lines.Add($"{pad}music switch {name(id)} on {string.Join(", ", tree.Groups.Select(name))} ({tree.Leaves.Count} paths)");
                foreach (var leaf in tree.Leaves)
                {
                    lines.Add($"{pad}  [{string.Join(" / ", leaf.Path.Select(k => k == 0 ? "*" : name(k)))}]");
                    if (leaf.Node != 0) TreeOf(bank, leaf.Node, depth + 2, lines, name);
                }
            }
            else if ((obj.Type == 10 || obj.Type == 12 || obj.Type == 13) && MusicChildren(bank, obj, id) is uint[] kids)
            {
                lines.Add($"{pad}{(obj.Type == 10 ? "segment" : obj.Type == 12 ? "music switch" : "music playlist")} {name(id)} ({kids.Length})");
                foreach (uint c in kids) TreeOf(bank, c, depth + 1, lines, name);
            }
            else lines.Add($"{pad}type {obj.Type} {name(id)} ({obj.Length} bytes, not parsed)");
        }

        // A music switch container's decision tree (it ends the object): the tree depth D, D state/switch group IDs, D group
        // types (bytes), the tree's size in bytes, a mode byte, then 12-byte nodes { key, children (index u16, count u16) or
        // the audio node at the last level, weight u16, probability u16 }. Found by that exact fit. Every leaf with the key
        // path to it (0 = any value).
        static (uint[] Groups, List<(uint[] Path, uint Node)> Leaves)? Decisions(Bank bank, (byte Type, int Offset, int Length) obj)
        {
            var d = bank.Data;
            int end = obj.Offset + obj.Length;
            for (int p = obj.Offset + 4; p + 5 <= end; p++)
            {
                uint size = BitConverter.ToUInt32(d, p);
                if (size == 0 || size % 12 != 0 || p + 5 + size != end) continue;
                for (int depth = 1; depth <= 6; depth++)
                {
                    int at = p - 5 * depth - 4;
                    if (at < obj.Offset + 4 || BitConverter.ToUInt32(d, at) != depth) continue;
                    var groups = Enumerable.Range(0, depth).Select(i => BitConverter.ToUInt32(d, at + 4 + 4 * i)).ToArray();
                    int nodes = p + 5, count = (int)(size / 12);
                    var leaves = new List<(uint[], uint)>();
                    void Walk(int i, int level, uint[] path)
                    {
                        if (i >= count || leaves.Count > 4096) return;
                        int q = nodes + 12 * i;
                        uint key = BitConverter.ToUInt32(d, q);
                        var here = level < 0 ? path : path.Append(key).ToArray();
                        if (level == depth - 1) { leaves.Add((here, BitConverter.ToUInt32(d, q + 4))); return; }
                        int first = BitConverter.ToUInt16(d, q + 4), n = BitConverter.ToUInt16(d, q + 6);
                        for (int c = 0; c < n; c++) Walk(first + c, level + 1, here);
                    }
                    Walk(0, -1, new uint[0]); // the root node's key is not a value
                    return (groups, leaves);
                }
            }
            return null;
        }

        int _variant, _variants; // the Resolve in progress (callers hold one lock: GameSounds)

        void Collect(Bank bank, uint id, uint[] wanted, List<Media> result, int depth)
        {
            if (depth > 8 || !bank.Objects.TryGetValue(id, out var obj) || ToMotion(bank, id, 0)) return;
            if (obj.Type == 2) // Sound
            {
                var s = new Reader(bank.Data, obj.Offset + 4);
                s.U32();                     // plugin (codec)
                byte stream = s.U8();        // 0 in the bank, 1 streamed, 2 prefetched + streamed
                uint media = s.U32();
                var found = stream == 0 ? bank.Embedded(media) : FindStreamed(media) ?? bank.Embedded(media);
                if (found != null && !result.Contains(found)) result.Add(found);
            }
            else if (obj.Type == 6 && SwitchList(bank, obj) is List<(uint Switch, uint[] Children)> entries && entries.Count > 0)
            {
                var pick = entries[0];
                foreach (uint w in wanted)
                {
                    int i = entries.FindIndex(e => e.Switch == w);
                    if (i >= 0) { pick = entries[i]; break; }
                }
                foreach (uint child in pick.Children) Collect(bank, child, wanted, result, depth + 1);
            }
            else if (obj.Type == 5 && Playlist(bank, obj) is uint[] takes && takes.Length > 0)
            {
                _variants = Math.Max(_variants, takes.Length);
                Collect(bank, takes[_variant % takes.Length], wanted, result, depth + 1);
            }
            else if (obj.Type == 9 && LayerChildren(bank, obj, id) is uint[] layers)
                foreach (uint child in layers) Collect(bank, child, wanted, result, depth + 1);
            // Interactive music (the ME bank's jingles: the catch fanfare PLAY_ME_TI_ST_POKEGET is a Music Segment
            // holding one Music Track): a segment plays all its tracks, a music switch / playlist container its first
            // child, a track its sources.
            else if (obj.Type == 11)
            {
                foreach (var media in TrackSources(bank, obj))
                    if (!result.Contains(media)) result.Add(media);
            }
            // A music switch on game states (SV's ambience beds: PLAY_ENV_BASE on SEABED_SWITCH / ENV_BASE / ENV_BASE_STAGE):
            // the path whose every set value is wanted, the most matches first; none matching plays nothing (a path of
            // only "any" plays when nothing else does).
            else if (obj.Type == 12 && wanted.Length > 0 && Decisions(bank, obj) is (uint[] _, List<(uint[] Path, uint Node)> leaves))
            {
                var pick = leaves.Where(l => l.Node != 0 && l.Path.All(k => k == 0 || wanted.Contains(k)))
                    .OrderByDescending(l => l.Path.Count(k => k != 0)).FirstOrDefault();
                if (pick.Path != null && pick.Path.Any(k => k != 0)) Collect(bank, pick.Node, wanted, result, depth + 1);
            }
            else if ((obj.Type == 10 || obj.Type == 12 || obj.Type == 13) && MusicChildren(bank, obj, id) is uint[] kids && kids.Length > 0)
            {
                if (obj.Type == 10) foreach (uint child in kids) Collect(bank, child, wanted, result, depth + 1);
                else Collect(bank, kids[0], wanted, result, depth + 1);
            }
        }

        // A music track's sources: after the ID, a flags byte, a count, then per source { plugin, stream type, media ID,
        // in-memory size, source bits } like a Sound's.
        List<Media> TrackSources(Bank bank, (byte Type, int Offset, int Length) obj)
        {
            var found = new List<Media>();
            var d = bank.Data;
            int p = obj.Offset + 5, end = obj.Offset + obj.Length;
            if (p + 4 > end) return found;
            uint n = BitConverter.ToUInt32(d, p);
            p += 4;
            for (int i = 0; i < n && n <= 16 && p + 14 <= end; i++, p += 14)
            {
                byte stream = d[p + 4];
                uint media = BitConverter.ToUInt32(d, p + 5);
                var m = stream == 0 ? bank.Embedded(media) : FindStreamed(media) ?? bank.Embedded(media);
                if (m != null && !found.Contains(m)) found.Add(m);
            }
            return found;
        }

        // A music container's children: the first list (count, IDs) of music objects of this bank other than itself.
        static uint[] MusicChildren(Bank bank, (byte Type, int Offset, int Length) obj, uint self)
        {
            var d = bank.Data;
            int end = obj.Offset + obj.Length;
            for (int k = obj.Offset + 4; k + 4 <= end; k++)
            {
                uint n = BitConverter.ToUInt32(d, k);
                if (n == 0 || n > 64 || k + 4 + 4L * n > end) continue;
                var ids = new uint[n];
                bool ok = true;
                for (int c = 0; c < n && ok; c++)
                {
                    ids[c] = BitConverter.ToUInt32(d, k + 4 + 4 * c);
                    ok = ids[c] != self && bank.Objects.TryGetValue(ids[c], out var child) && child.Type >= 10 && child.Type <= 13;
                }
                if (ok && ids.Distinct().Count() == ids.Length) return ids;
            }
            return null;
        }

        static readonly uint[] MotionBuses = { Hash("Motion_Bus"), Hash("Master Motion Bus"), Hash("Motion Factory Bus") };

        // Whether a node plays on the vibration bus: its own output bus, else its parent's (actor-mixers carry it), and that
        // bus or one above it (Init.bnk) is a motion bus.
        bool ToMotion(Bank bank, uint id, int depth)
        {
            if (depth > 16 || !bank.Objects.TryGetValue(id, out var obj)) return false;
            if (!(Routing(bank, obj) is (uint bus, uint parent))) return false;
            if (bus != 0)
            {
                var init = LoadBank("Init");
                for (int up = 0; up < 8 && bus != 0; up++)
                {
                    if (Array.IndexOf(MotionBuses, bus) >= 0) return true;
                    if (init == null || !init.Objects.TryGetValue(bus, out var b) || b.Length < 8) return false;
                    bus = BitConverter.ToUInt32(init.Data, b.Offset + 4); // a bus's parent bus
                }
                return false;
            }
            return parent != 0 && ToMotion(bank, parent, depth + 1);
        }

        // A node's output bus override and its parent (NodeBaseParams, bank version 140): after a Sound's source data,
        // the effect list, the metadata effect list and the attachment flag. Null when it doesn't parse.
        static (uint Bus, uint Parent)? Routing(Bank bank, (byte Type, int Offset, int Length) obj)
        {
            if (obj.Type != 2 && obj.Type != 5 && obj.Type != 6 && obj.Type != 7 && obj.Type != 9) return null;
            var d = bank.Data;
            int p = obj.Offset + 4, end = obj.Offset + obj.Length;
            if (obj.Type == 2)
            {
                uint plugin = BitConverter.ToUInt32(d, p);
                p += 14; // plugin, stream type, media ID, in-memory size, source bits
                if ((plugin & 0x0F) == 2) p += 4 + (int)BitConverter.ToUInt32(d, p); // a source plugin's parameters
            }
            if (p + 2 > end) return null;
            int fx = d[p + 1];
            p += 2 + (fx > 0 ? 1 + 7 * fx : 0); // override flag, count, bypass bits, {index, ID, share set, rendered}
            if (p + 2 > end) return null;
            int meta = d[p + 1];
            p += 2 + 6 * meta;                  // override flag, count, {index, ID, share set}
            p += 1;                             // override attachment params
            if (p + 8 > end) return null;
            uint parent = BitConverter.ToUInt32(d, p + 4);
            if (parent != 0 && !bank.Objects.ContainsKey(parent)) return null; // not where we thought: don't guess
            return (BitConverter.ToUInt32(d, p), parent);
        }

        // A random/sequence container's playlist: it ends the object, a 16-bit count N then N { child ID, weight }, right
        // after the child list (count, IDs). Found by that exact fit, like SwitchList.
        static uint[] Playlist(Bank bank, (byte Type, int Offset, int Length) obj)
        {
            var d = bank.Data;
            int end = obj.Offset + obj.Length;
            for (int k = obj.Offset + 4; k + 4 <= end; k++)
            {
                uint n = BitConverter.ToUInt32(d, k);
                if (n == 0 || n > 256 || k + 4 + 4L * n + 2 > end) continue;
                int q = k + 4 + 4 * (int)n;
                ushort m = BitConverter.ToUInt16(d, q);
                if (q + 2 + 8L * m != end) continue;
                bool ok = true;
                for (int c = 0; c < n && ok; c++) ok = bank.Objects.ContainsKey(BitConverter.ToUInt32(d, k + 4 + 4 * c));
                var list = new uint[m];
                for (int c = 0; c < m && ok; c++)
                {
                    list[c] = BitConverter.ToUInt32(d, q + 2 + 8 * c);
                    ok = bank.Objects.ContainsKey(list[c]);
                }
                if (ok) return m > 0 ? list : Enumerable.Range(0, (int)n).Select(c => BitConverter.ToUInt32(d, k + 4 + 4 * c)).ToArray();
            }
            return null;
        }

        // A layer container's children: the first list (count, IDs) whose IDs are all sounds or containers of this bank
        // other than itself, after the node's parent field. Each layer then plays every child at once.
        static uint[] LayerChildren(Bank bank, (byte Type, int Offset, int Length) obj, uint self)
        {
            var d = bank.Data;
            int end = obj.Offset + obj.Length;
            for (int k = obj.Offset + 13; k + 4 <= end; k++)
            {
                uint n = BitConverter.ToUInt32(d, k);
                if (n == 0 || n > 64 || k + 4 + 4L * n > end) continue;
                var ids = new uint[n];
                bool ok = true;
                for (int c = 0; c < n && ok; c++)
                {
                    ids[c] = BitConverter.ToUInt32(d, k + 4 + 4 * c);
                    ok = ids[c] != self && bank.Objects.TryGetValue(ids[c], out var child) && (child.Type == 2 || child.Type == 5 || child.Type == 6 || child.Type == 9);
                }
                if (ok && ids.Distinct().Count() == ids.Length) return ids;
            }
            return null;
        }

        // A switch container's switch -> children list. It follows variable-length node parameters, so it's
        // found by its shape: a count N, N entries { switch ID, child count, child IDs (objects of this
        // bank) }, then the per-child parameter table (count M, 14 bytes each) ending exactly at the end.
        // That exact fit parsed every one of SV's 733 cry events.
        static List<(uint, uint[])> SwitchList(Bank bank, (byte Type, int Offset, int Length) obj)
        {
            var d = bank.Data;
            int end = obj.Offset + obj.Length;
            for (int k = obj.Offset + 4; k + 4 <= end; k++)
            {
                uint n = BitConverter.ToUInt32(d, k);
                if (n == 0 || n > 256) continue;
                var entries = new List<(uint, uint[])>();
                int q = k + 4;
                bool ok = true;
                for (int e = 0; e < n && ok; e++)
                {
                    if (q + 8 > end) { ok = false; break; }
                    uint sw = BitConverter.ToUInt32(d, q);
                    uint count = BitConverter.ToUInt32(d, q + 4);
                    q += 8;
                    if (count > 64 || q + 4 * count > end) { ok = false; break; }
                    var children = new uint[count];
                    for (int c = 0; c < count; c++, q += 4)
                    {
                        children[c] = BitConverter.ToUInt32(d, q);
                        if (!bank.Objects.ContainsKey(children[c])) ok = false;
                    }
                    entries.Add((sw, children));
                }
                if (ok && q + 4 <= end && q + 4 + 14L * BitConverter.ToUInt32(d, q) == end) return entries;
            }
            return null;
        }

        // The media's bytes: a RIFF .wem file.
        public byte[] Read(Media media)
        {
            using var f = File.OpenRead(Path.Combine(_folder, media.Source));
            f.Position = media.Offset;
            var data = new byte[media.Size];
            int read = 0;
            while (read < data.Length)
            {
                int n = f.Read(data, read, data.Length - read);
                if (n <= 0) throw new EndOfStreamException(media.Source);
                read += n;
            }
            return data;
        }

        Bank LoadBank(string name)
        {
            if (_banks.TryGetValue(name, out var bank)) return bank;
            string path = Path.Combine(_folder, name + ".bnk");
            bank = File.Exists(path) ? new Bank(name + ".bnk", File.ReadAllBytes(path)) : null;
            _banks[name] = bank;
            return bank;
        }

        Media FindStreamed(uint id)
        {
            _packages ??= Directory.GetFiles(_folder, "*.pck").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => new Package(p)).ToList();
            foreach (var p in _packages)
                if (p.Sounds.TryGetValue(id, out var m)) return m;
            return null;
        }

        // A soundbank: sections BKHD, DIDX (media index), DATA (embedded media), HIRC (objects).
        sealed class Bank
        {
            public readonly string File;
            public readonly byte[] Data;
            public readonly Dictionary<uint, (byte Type, int Offset, int Length)> Objects = new Dictionary<uint, (byte, int, int)>();
            readonly Dictionary<uint, (int Offset, int Size)> _index = new Dictionary<uint, (int, int)>();
            int _dataStart = -1;

            public Bank(string file, byte[] data)
            {
                File = file;
                Data = data;
                int o = 0;
                while (o + 8 <= data.Length)
                {
                    string tag = Encoding.ASCII.GetString(data, o, 4);
                    int size = BitConverter.ToInt32(data, o + 4);
                    int body = o + 8;
                    if (tag == "DIDX")
                        for (int p = body; p + 12 <= body + size; p += 12)
                            _index[BitConverter.ToUInt32(data, p)] = (BitConverter.ToInt32(data, p + 4), BitConverter.ToInt32(data, p + 8));
                    else if (tag == "DATA") _dataStart = body;
                    else if (tag == "HIRC")
                    {
                        int count = BitConverter.ToInt32(data, body), p = body + 4;
                        for (int i = 0; i < count && p + 5 <= body + size; i++)
                        {
                            byte type = data[p];
                            int length = BitConverter.ToInt32(data, p + 1);
                            Objects[BitConverter.ToUInt32(data, p + 5)] = (type, p + 5, length); // offset of the object's ID
                            p += 5 + length;
                        }
                    }
                    o = body + size;
                }
            }

            public Media Embedded(uint id) =>
                _dataStart >= 0 && _index.TryGetValue(id, out var m) ? new Media(id, File, _dataStart + m.Offset, m.Size) : null;
        }

        // A file package (AKPK): header tables of banks and sounds (ID, block size, size, start block, language).
        sealed class Package
        {
            public readonly Dictionary<uint, Media> Sounds = new Dictionary<uint, Media>();

            public Package(string path)
            {
                using var f = System.IO.File.OpenRead(path);
                var head = new byte[8];
                if (f.Read(head, 0, 8) != 8 || Encoding.ASCII.GetString(head, 0, 4) != "AKPK") return;
                var h = new byte[BitConverter.ToInt32(head, 4)];
                if (f.Read(h, 0, h.Length) != h.Length) return;
                int languages = BitConverter.ToInt32(h, 4), banks = BitConverter.ToInt32(h, 8);
                int p = 20 + languages + banks; // version, 4 table sizes, language map, bank table
                if (p + 4 > h.Length) return;
                int count = BitConverter.ToInt32(h, p);
                p += 4;
                string name = Path.GetFileName(path);
                for (int i = 0; i < count && p + 20 <= h.Length; i++, p += 20)
                {
                    uint id = BitConverter.ToUInt32(h, p);
                    uint block = BitConverter.ToUInt32(h, p + 4);
                    int size = BitConverter.ToInt32(h, p + 8);
                    long offset = (long)BitConverter.ToUInt32(h, p + 12) * Math.Max(1u, block);
                    if (!Sounds.ContainsKey(id)) Sounds[id] = new Media(id, name, offset, size);
                }
            }
        }

        struct Reader
        {
            readonly byte[] _d;
            int _p;

            public Reader(byte[] d, int p) { _d = d; _p = p; }
            public byte U8() => _d[_p++];
            public ushort U16() { var v = BitConverter.ToUInt16(_d, _p); _p += 2; return v; }
            public uint U32() { var v = BitConverter.ToUInt32(_d, _p); _p += 4; return v; }

            // Wwise's variable-length count: 7 bits per byte, high bit = more follows.
            public uint VarUInt()
            {
                uint v = 0;
                byte b;
                do { b = _d[_p++]; v = (v << 7) | (uint)(b & 0x7F); } while ((b & 0x80) != 0);
                return v;
            }
        }
    }
}
