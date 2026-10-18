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
