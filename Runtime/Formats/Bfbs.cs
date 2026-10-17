using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PokeModelExporter.Formats
{
    // FlatBuffers reflection: a binary schema (.bfbs, flatc's compiled reflection.fbs) and data decoded with it into plain
    // objects (Dictionary<string, object> tables and structs, List<object> vectors, long / double / bool / string values,
    // enum values by name). Scarlet/Violet ships a .bfbs next to most of its data tables (world/data/...), so these decode
    // completely without hand-written schemas. Unions come out as their table plus "_type"; unknown enum values as numbers.
    public sealed class BfbsSchema
    {
        enum Base : byte { None, UType, Bool, Byte, UByte, Short, UShort, Int, UInt, Long, ULong, Float, Double, String, Vector, Obj, Union, Array, Vector64 }

        sealed class FieldDef
        {
            public string Name;
            public int Id, Offset, Index = -1, FixedLength;
            public Base Type, Element;
            public bool Deprecated;
        }

        sealed class ObjectDef
        {
            public string Name;
            public bool IsStruct;
            public int ByteSize;
            public List<FieldDef> Fields = new List<FieldDef>();
        }

        sealed class EnumDef
        {
            public string Name;
            public bool IsUnion;
            public Dictionary<long, string> Names = new Dictionary<long, string>();
            public Dictionary<long, int> UnionObjects = new Dictionary<long, int>(); // union member -> object index
        }

        readonly List<ObjectDef> _objects = new List<ObjectDef>();
        readonly List<EnumDef> _enums = new List<EnumDef>();
        readonly int _root = -1;

        public string RootName => _root >= 0 ? _objects[_root].Name : null;
        public string FileIdentifier { get; }

        public BfbsSchema(byte[] bfbs)
        {
            var schema = FbTable.Root(bfbs);
            var objects = schema.Vector(0);
            for (int i = 0; i < objects.Length; i++)
            {
                var o = objects.Table(i);
                var def = new ObjectDef { Name = o.String(0), IsStruct = o.Bool(2), ByteSize = o.I32(4) };
                var fields = o.Vector(1);
                for (int j = 0; j < fields.Length; j++)
                {
                    var f = fields.Table(j);
                    var type = f.Table(1);
                    def.Fields.Add(new FieldDef
                    {
                        Name = f.String(0), Id = f.U16(2), Offset = f.U16(3), Deprecated = f.Bool(6),
                        Type = (Base)type.U8(0), Element = (Base)type.U8(1), Index = type.I32(2, -1), FixedLength = type.U16(3),
                    });
                }
                def.Fields.Sort((a, b) => a.Id.CompareTo(b.Id));
                _objects.Add(def);
            }
            var enums = schema.Vector(1);
            for (int i = 0; i < enums.Length; i++)
            {
                var e = enums.Table(i);
                var def = new EnumDef { Name = e.String(0), IsUnion = e.Bool(2) };
                var values = e.Vector(1);
                for (int j = 0; j < values.Length; j++)
                {
                    var v = values.Table(j);
                    long value = v.I64(1);
                    def.Names[value] = v.String(0);
                    if (v.Has(3)) { int obj = v.Table(3).I32(2, -1); if (obj >= 0) def.UnionObjects[value] = obj; }
                }
                _enums.Add(def);
            }
            FileIdentifier = schema.String(2);
            if (schema.Has(4))
            {
                string root = schema.Table(4).String(0);
                _root = _objects.FindIndex(o => o.Name == root);
            }
        }

        // An enum value's number by its name (e.g. a DevID "DEV_PIKATYUU" -> 25), from any enum of the schema.
        public bool TryEnumNumber(string valueName, out long number)
        {
            foreach (var e in _enums)
                foreach (var kv in e.Names)
                    if (kv.Value == valueName) { number = kv.Key; return true; }
            number = 0;
            return false;
        }

        // The data's root table as plain objects (see the class comment); null without a root type.
        public object Decode(byte[] data) => _root < 0 || data == null || data.Length < 8 ? null : Table(data, (int)ReadU32(data, 0), _root);

        object Table(byte[] b, int pos, int index)
        {
            var obj = _objects[index];
            if (obj.IsStruct) return Struct(b, pos, index);
            int vtable = pos - ReadI32(b, pos);
            int vlength = ReadU16(b, vtable);
            var result = new Dictionary<string, object>();
            foreach (var f in obj.Fields)
            {
                if (f.Deprecated || f.Type == Base.UType) continue; // a union's type goes with the union
                int slot = 4 + 2 * f.Id;
                int offset = slot < vlength ? ReadU16(b, vtable + slot) : 0;
                if (offset == 0) continue;
                int at = pos + offset;
                result[f.Name] = Value(b, at, f, obj, pos, vtable, vlength);
            }
            return result;
        }

        object Value(byte[] b, int at, FieldDef f, ObjectDef obj, int tablePos, int vtable, int vlength)
        {
            switch (f.Type)
            {
                case Base.String: return Str(b, at + (int)ReadU32(b, at));
                case Base.Obj:
                    return _objects[f.Index].IsStruct ? Struct(b, at, f.Index) : Table(b, at + (int)ReadU32(b, at), f.Index);
                case Base.Union:
                {
                    long type = UnionType(b, f, obj, tablePos, vtable, vlength, -1);
                    return UnionValue(b, at + (int)ReadU32(b, at), f.Index, type);
                }
                case Base.Vector:
                {
                    int vec = at + (int)ReadU32(b, at);
                    int n = (int)ReadU32(b, vec);
                    int start = vec + 4;
                    var list = new List<object>(Math.Min(n, 1 << 16));
                    switch (f.Element)
                    {
                        case Base.String:
                            for (int i = 0; i < n; i++) { int p = start + 4 * i; list.Add(Str(b, p + (int)ReadU32(b, p))); }
                            break;
                        case Base.Obj:
                            var od = _objects[f.Index];
                            for (int i = 0; i < n; i++)
                            {
                                if (od.IsStruct) list.Add(Struct(b, start + i * od.ByteSize, f.Index));
                                else { int p = start + 4 * i; list.Add(Table(b, p + (int)ReadU32(b, p), f.Index)); }
                            }
                            break;
                        case Base.Union:
                            for (int i = 0; i < n; i++)
                            {
                                long type = UnionType(b, f, obj, tablePos, vtable, vlength, i);
                                int p = start + 4 * i;
                                list.Add(UnionValue(b, p + (int)ReadU32(b, p), f.Index, type));
                            }
                            break;
                        default:
                            int size = Size(f.Element);
                            for (int i = 0; i < n && size > 0; i++) list.Add(Scalar(b, start + i * size, f.Element, f.Index));
                            break;
                    }
                    return list;
                }
                default: return Scalar(b, at, f.Type, f.Index);
            }
        }

        // The union's member type: the "<name>_type" field (a vector of them for a vector of unions).
        long UnionType(byte[] b, FieldDef f, ObjectDef obj, int tablePos, int vtable, int vlength, int element)
        {
            var tf = obj.Fields.Find(x => x.Name == f.Name + "_type");
            if (tf == null) return 0;
            int slot = 4 + 2 * tf.Id;
            int offset = slot < vlength ? ReadU16(b, vtable + slot) : 0;
            if (offset == 0) return 0;
            int at = tablePos + offset;
            if (element < 0) return b[at];
            int vec = at + (int)ReadU32(b, at);
            return element < ReadU32(b, vec) ? b[vec + 4 + element] : 0;
        }

        object UnionValue(byte[] b, int pos, int enumIndex, long type)
        {
            var e = _enums[enumIndex];
            string name = e.Names.TryGetValue(type, out var n) ? n : type.ToString(CultureInfo.InvariantCulture);
            var result = new Dictionary<string, object> { ["_type"] = name };
            if (e.UnionObjects.TryGetValue(type, out int objIndex) && Table(b, pos, objIndex) is Dictionary<string, object> table)
                foreach (var kv in table) result[kv.Key] = kv.Value;
            return result;
        }

        Dictionary<string, object> Struct(byte[] b, int pos, int index)
        {
            var result = new Dictionary<string, object>();
            foreach (var f in _objects[index].Fields)
            {
                int at = pos + f.Offset;
                if (f.Type == Base.Obj) result[f.Name] = Struct(b, at, f.Index);
                else if (f.Type == Base.Array)
                {
                    var list = new List<object>();
                    if (f.Element == Base.Obj)
                        for (int i = 0; i < f.FixedLength; i++) list.Add(Struct(b, at + i * _objects[f.Index].ByteSize, f.Index));
                    else
                        for (int i = 0, size = Size(f.Element); i < f.FixedLength && size > 0; i++) list.Add(Scalar(b, at + i * size, f.Element, f.Index));
                    result[f.Name] = list;
                }
                else result[f.Name] = Scalar(b, at, f.Type, f.Index);
            }
            return result;
        }

        object Scalar(byte[] b, int at, Base type, int enumIndex)
        {
            object v;
            switch (type)
            {
                case Base.Bool: return b[at] != 0;
                case Base.UType: case Base.UByte: v = (long)b[at]; break;
                case Base.Byte: v = (long)(sbyte)b[at]; break;
                case Base.Short: v = (long)BitConverter.ToInt16(b, at); break;
                case Base.UShort: v = (long)ReadU16(b, at); break;
                case Base.Int: v = (long)ReadI32(b, at); break;
                case Base.UInt: v = (long)ReadU32(b, at); break;
                case Base.Long: v = BitConverter.ToInt64(b, at); break;
                case Base.ULong: v = (long)BitConverter.ToUInt64(b, at); break;
                // A float as its shortest exact decimal (0.01, not 0.009999999776...).
                case Base.Float: return double.Parse(BitConverter.ToSingle(b, at).ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                case Base.Double: return BitConverter.ToDouble(b, at);
                default: return null;
            }
            // Integers of an enum type: the value's name.
            if (enumIndex >= 0 && enumIndex < _enums.Count && !_enums[enumIndex].IsUnion && _enums[enumIndex].Names.TryGetValue((long)v, out var name)) return name;
            return v;
        }

        static int Size(Base t) => t switch
        {
            Base.Bool => 1, Base.Byte => 1, Base.UByte => 1, Base.UType => 1, Base.Short => 2, Base.UShort => 2,
            Base.Int => 4, Base.UInt => 4, Base.Float => 4, Base.Long => 8, Base.ULong => 8, Base.Double => 8, _ => 0,
        };

        static string Str(byte[] b, int pos) => Encoding.UTF8.GetString(b, pos + 4, (int)ReadU32(b, pos));
        static int ReadU16(byte[] b, int at) => BitConverter.ToUInt16(b, at);
        static int ReadI32(byte[] b, int at) => BitConverter.ToInt32(b, at);
        static uint ReadU32(byte[] b, int at) => BitConverter.ToUInt32(b, at);

        // ---- JSON ----

        public static string ToJson(object value, bool indent = true)
        {
            var sb = new StringBuilder();
            Write(sb, value, indent ? 0 : -1);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object value, int depth)
        {
            string nl = depth >= 0 ? "\n" + new string(' ', depth + 1) : "", end = depth >= 0 ? "\n" + new string(' ', depth) : "";
            int next = depth >= 0 ? depth + 1 : -1;
            switch (value)
            {
                case null: sb.Append("null"); break;
                case bool v: sb.Append(v ? "true" : "false"); break;
                case long v: sb.Append(v.ToString(CultureInfo.InvariantCulture)); break;
                case double v: sb.Append(double.IsNaN(v) || double.IsInfinity(v) ? "null" : v.ToString("R", CultureInfo.InvariantCulture)); break;
                case string v: Quote(sb, v); break;
                case Dictionary<string, object> d:
                    if (d.Count == 0) { sb.Append("{}"); break; }
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in d)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append(nl);
                        Quote(sb, kv.Key);
                        sb.Append(depth >= 0 ? ": " : ":");
                        Write(sb, kv.Value, next);
                    }
                    sb.Append(end).Append('}');
                    break;
                case List<object> l:
                    if (l.Count == 0) { sb.Append("[]"); break; }
                    // Short lists of numbers stay on one line.
                    bool flat = l.Count <= 16 && l.TrueForAll(x => x is long || x is double || x is bool);
                    sb.Append('[');
                    for (int i = 0; i < l.Count; i++)
                    {
                        if (i > 0) sb.Append(flat && depth >= 0 ? ", " : ",");
                        if (!flat) sb.Append(nl);
                        Write(sb, l[i], next);
                    }
                    if (!flat) sb.Append(end);
                    sb.Append(']');
                    break;
                default: Quote(sb, value.ToString()); break;
            }
        }

        static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
        }
    }
}
