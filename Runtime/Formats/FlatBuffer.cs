using System;
using System.Text;

namespace PokeModelExporter.Formats
{
    // Minimal read-only FlatBuffers access (https://flatbuffers.dev/internals/), enough to walk
    // the Trinity schemas from pkZukan/PokeDocs by field index without generated code.
    // Field N of a table is the N-th field declared in its .fbs table.
    public readonly struct FbTable
    {
        readonly byte[] _buf;
        readonly int _pos;

        public FbTable(byte[] buffer, int position)
        {
            _buf = buffer;
            _pos = position;
        }

        public bool IsNull => _buf == null;

        // The root table of a whole FlatBuffer.
        public static FbTable Root(byte[] buffer, int start = 0) =>
            new FbTable(buffer, start + BitConverter.ToInt32(buffer, start));

        // Absolute position of a field's data, or 0 if absent.
        int Field(int index)
        {
            int vtable = _pos - BitConverter.ToInt32(_buf, _pos);
            int vtableSize = BitConverter.ToUInt16(_buf, vtable);
            int entry = 4 + 2 * index;
            if (entry >= vtableSize) return 0;
            int offset = BitConverter.ToUInt16(_buf, vtable + entry);
            return offset == 0 ? 0 : _pos + offset;
        }

        public bool Has(int index) => Field(index) != 0;

        public ulong U64(int index, ulong fallback = 0) { int p = Field(index); return p == 0 ? fallback : BitConverter.ToUInt64(_buf, p); }
        public long I64(int index, long fallback = 0) { int p = Field(index); return p == 0 ? fallback : BitConverter.ToInt64(_buf, p); }
        public uint U32(int index, uint fallback = 0) { int p = Field(index); return p == 0 ? fallback : BitConverter.ToUInt32(_buf, p); }
        public int I32(int index, int fallback = 0) { int p = Field(index); return p == 0 ? fallback : BitConverter.ToInt32(_buf, p); }
        public short I16(int index, short fallback = 0) { int p = Field(index); return p == 0 ? fallback : BitConverter.ToInt16(_buf, p); }
        public ushort U16(int index, ushort fallback = 0) { int p = Field(index); return p == 0 ? fallback : BitConverter.ToUInt16(_buf, p); }
        public byte U8(int index, byte fallback = 0) { int p = Field(index); return p == 0 ? fallback : _buf[p]; }
        public sbyte I8(int index, sbyte fallback = 0) { int p = Field(index); return p == 0 ? fallback : (sbyte)_buf[p]; }
        public bool Bool(int index, bool fallback = false) { int p = Field(index); return p == 0 ? fallback : _buf[p] != 0; }
        public float F32(int index, float fallback = 0) { int p = Field(index); return p == 0 ? fallback : BitConverter.ToSingle(_buf, p); }

        public string String(int index)
        {
            int p = Field(index);
            return p == 0 ? null : ReadString(_buf, p + BitConverter.ToInt32(_buf, p));
        }

        public FbTable Table(int index)
        {
            int p = Field(index);
            return p == 0 ? default : new FbTable(_buf, p + BitConverter.ToInt32(_buf, p));
        }

        public FbVector Vector(int index)
        {
            int p = Field(index);
            return p == 0 ? default : new FbVector(_buf, p + BitConverter.ToInt32(_buf, p));
        }

        // A struct stored inline in the table (not behind an offset).
        public int InlineStruct(int index) => Field(index);

        public byte[] Buffer => _buf;

        internal static string ReadString(byte[] buf, int at)
        {
            int length = BitConverter.ToInt32(buf, at);
            return Encoding.UTF8.GetString(buf, at + 4, length);
        }
    }

    public readonly struct FbVector
    {
        readonly byte[] _buf;
        readonly int _start; // first element (after the length)

        public FbVector(byte[] buffer, int lengthPosition)
        {
            _buf = buffer;
            _start = lengthPosition + 4;
            Length = BitConverter.ToInt32(buffer, lengthPosition);
        }

        public int Length { get; }
        public bool IsNull => _buf == null;

        public ulong U64(int i) => BitConverter.ToUInt64(_buf, _start + 8 * i);
        public uint U32(int i) => BitConverter.ToUInt32(_buf, _start + 4 * i);
        public int I32(int i) => BitConverter.ToInt32(_buf, _start + 4 * i);
        public ushort U16(int i) => BitConverter.ToUInt16(_buf, _start + 2 * i);
        public byte U8(int i) => _buf[_start + i];
        public float F32(int i) => BitConverter.ToSingle(_buf, _start + 4 * i);

        public FbTable Table(int i)
        {
            int p = _start + 4 * i;
            return new FbTable(_buf, p + BitConverter.ToInt32(_buf, p));
        }

        public string String(int i)
        {
            int p = _start + 4 * i;
            return FbTable.ReadString(_buf, p + BitConverter.ToInt32(_buf, p));
        }

        // Inline structs of a fixed size.
        public int StructAt(int i, int structSize) => _start + structSize * i;

        public ArraySegment<byte> Bytes() => new ArraySegment<byte>(_buf, _start, Length);
    }
}
