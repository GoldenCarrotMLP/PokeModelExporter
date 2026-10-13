using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PokeModelExporter.Formats
{
    // Read access to a Trinity engine file system (Scarlet/Violet, Legends Arceus):
    //   arc/data.trpfd  FlatBuffer index: every file hash -> pack; pack names and sizes
    //   arc/data.trpfs  "ONEPACK" + offset to a FlatBuffer mapping pack-name hashes -> offsets,
    //                   followed by the packs themselves (each a TRPAK FlatBuffer)
    //   TRPAK           file hashes + entries (compression type, size, bytes)
    // Schemas: pkZukan/PokeDocs SV/Flatbuffers/filesystem. File and pack names are hashed with
    // FNV-1a 64; only pack names are stored, so file names come from a hash list.
    public sealed class TrinityArchive : IDisposable
    {
        public const ulong FnvOffset = 0xCBF29CE484222645, FnvPrime = 0x100000001B3;

        readonly FileStream _trpfs;
        readonly Oodle _oodle;
        readonly ulong[] _fileHashes;         // trpfd: every file in the game
        readonly ulong[] _filePack;           // trpfd: pack index per file
        readonly string[] _packNames;         // trpfd: pack path per pack index
        readonly ulong[] _packSizes;          // trpfd: byte size per pack index
        readonly Dictionary<ulong, int> _fileIndex = new Dictionary<ulong, int>();
        readonly Dictionary<ulong, ulong> _packOffsets = new Dictionary<ulong, ulong>(); // pack name hash -> offset

        public int FileCount => _fileHashes.Length;
        public IReadOnlyList<string> PackNames => _packNames;

        public TrinityArchive(string arcFolder, Oodle oodle)
        {
            _oodle = oodle;
            var fd = FbTable.Root(File.ReadAllBytes(Path.Combine(arcFolder, "data.trpfd")));
            var hashes = fd.Vector(0);
            var packStrings = fd.Vector(1);
            var files = fd.Vector(2);
            var packs = fd.Vector(3);

            _fileHashes = new ulong[hashes.Length];
            _filePack = new ulong[hashes.Length];
            for (int i = 0; i < hashes.Length; i++)
            {
                _fileHashes[i] = hashes.U64(i);
                _filePack[i] = files.Table(i).U64(0);
                _fileIndex[_fileHashes[i]] = i;
            }
            _packNames = new string[packStrings.Length];
            _packSizes = new ulong[packStrings.Length];
            for (int i = 0; i < packStrings.Length; i++)
            {
                _packNames[i] = packStrings.String(i);
                _packSizes[i] = packs.Table(i).U64(0);
            }

            _trpfs = new FileStream(Path.Combine(arcFolder, "data.trpfs"), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var header = ReadAt(0, 16);
            if (Encoding.ASCII.GetString(header, 0, 7) != "ONEPACK")
                throw new InvalidDataException("data.trpfs: missing ONEPACK header");
            long indexOffset = BitConverter.ToInt64(header, 8);
            var fs = FbTable.Root(ReadAt(indexOffset, (int)(_trpfs.Length - indexOffset)));
            var packHashes = fs.Vector(0);
            var packOffsets = fs.Vector(1);
            for (int i = 0; i < packHashes.Length; i++)
                _packOffsets[packHashes.U64(i)] = packOffsets.U64(i);
        }

        public static ulong Hash(string path)
        {
            ulong h = FnvOffset;
            foreach (byte b in Encoding.UTF8.GetBytes(path))
            {
                h ^= b;
                h *= FnvPrime;
            }
            return h;
        }

        public IEnumerable<ulong> AllFileHashes() => _fileHashes;

        public bool Contains(ulong fileHash) => _fileIndex.ContainsKey(fileHash);
        public bool Contains(string path) => Contains(Hash(Normalize(path)));

        public string PackOf(ulong fileHash) => _fileIndex.TryGetValue(fileHash, out int i) ? _packNames[_filePack[i]] : null;

        // How many pack names resolve in data.trpfs (a sanity check of the hash function).
        public int ResolvablePacks()
        {
            int n = 0;
            foreach (var name in _packNames) if (_packOffsets.ContainsKey(Hash(name))) n++;
            return n;
        }

        // All file hashes stored in one pack.
        public List<ulong> FilesInPack(string packName)
        {
            var result = new List<ulong>();
            int packIndex = Array.IndexOf(_packNames, packName);
            for (int i = 0; i < _fileHashes.Length; i++)
                if ((int)_filePack[i] == packIndex) result.Add(_fileHashes[i]);
            return result;
        }

        public byte[] Read(string path) => Read(Hash(Normalize(path)));

        // Files are hashed by their canonical path, but materials of alternative models point at shared
        // textures relatively ("pm0214/pm0214_01_00/../pm0214_00_00/x.bntx"): resolve "." and "..".
        public static string Normalize(string path)
        {
            if (path.IndexOf("./", System.StringComparison.Ordinal) < 0) return path;
            var parts = new System.Collections.Generic.List<string>();
            foreach (var part in path.Split('/'))
            {
                if (part == ".") continue;
                if (part == ".." && parts.Count > 0 && parts[parts.Count - 1] != "..") parts.RemoveAt(parts.Count - 1);
                else parts.Add(part);
            }
            return string.Join("/", parts);
        }

        // A file's decompressed bytes, or null if the game doesn't have it.
        public byte[] Read(ulong fileHash)
        {
            if (!_fileIndex.TryGetValue(fileHash, out int fi)) return null;
            var pack = ReadPack((int)_filePack[fi]);
            var hashes = pack.Vector(0);
            var entries = pack.Vector(1);
            for (int i = 0; i < hashes.Length; i++)
            {
                if (hashes.U64(i) != fileHash) continue;
                var entry = entries.Table(i);
                sbyte compression = entry.I8(1, -1);
                long size = (long)entry.U64(3);
                var data = entry.Vector(4).Bytes();
                switch (compression)
                {
                    case -1: return ToArray(data);
                    case 3: return _oodle.Decompress(data.Array, data.Offset, data.Count, size);
                    case 1: return Inflate(data, size);
                    default: throw new NotSupportedException($"Compression type {compression} (LZ4) not implemented");
                }
            }
            return null;
        }

        FbTable ReadPack(int packIndex)
        {
            string name = _packNames[packIndex];
            if (!_packOffsets.TryGetValue(Hash(name), out ulong offset))
                throw new InvalidDataException($"Pack {name} not found in data.trpfs");
            return FbTable.Root(ReadAt((long)offset, (int)_packSizes[packIndex]));
        }

        byte[] ReadAt(long offset, int length)
        {
            var buf = new byte[length];
            _trpfs.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < length)
            {
                int n = _trpfs.Read(buf, read, length - read);
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
            return buf;
        }

        static byte[] ToArray(ArraySegment<byte> s)
        {
            var a = new byte[s.Count];
            Buffer.BlockCopy(s.Array, s.Offset, a, 0, s.Count);
            return a;
        }

        static byte[] Inflate(ArraySegment<byte> s, long size)
        {
            // zlib: skip the 2-byte header, then raw deflate.
            using var input = new MemoryStream(s.Array, s.Offset + 2, s.Count - 2);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            var raw = new byte[size];
            int read = 0;
            while (read < size)
            {
                int n = deflate.Read(raw, read, (int)(size - read));
                if (n <= 0) break;
                read += n;
            }
            return raw;
        }

        public void Dispose() => _trpfs.Dispose();
    }
}
