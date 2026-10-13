using System;
using System.Collections.Generic;
using System.Globalization;
using PokeModelExporter.Export;

namespace PokeModelExporter.Formats
{
    // Decodes a FlatBuffer binary into JSON using its parsed schema: tables become objects (absent
    // scalars get their schema default, absent references are omitted), enums their names,
    // unions {"type": name, "value": ...}, and byte vectors base64.
    public static class FlatBufferJson
    {
        public static void Write(byte[] data, FbsSchema schema, JsonWriter w, string rootType = null)
        {
            string root = rootType ?? schema.RootType ?? throw new ArgumentException("Schema has no root_type");
            WriteTable(FbTable.Root(data), root, schema, w, 0);
        }

        static void WriteTable(FbTable table, string typeName, FbsSchema schema, JsonWriter w, int depth)
        {
            if (!schema.Types.TryGetValue(typeName, out var type) || depth > 64) { w.Null(); return; }
            w.BeginObject();
            int slot = 0;
            foreach (var f in type.Fields)
            {
                bool isUnion = !f.IsVector && schema.Enums.TryGetValue(f.Type, out var u) && u.IsUnion;
                if (isUnion)
                {
                    int unionType = table.U8(slot);
                    var value = table.Table(slot + 1);
                    slot += 2;
                    if (unionType == 0 || value.IsNull) continue;
                    string member = schema.Enums[f.Type].Values.TryGetValue(unionType, out var m) ? m : null;
                    w.Name(f.Name).BeginObject().Name("type").Value(member ?? unionType.ToString(CultureInfo.InvariantCulture)).Name("value");
                    if (member != null) WriteTable(value, member, schema, w, depth + 1); else w.Null();
                    w.EndObject();
                    continue;
                }
                int index = slot++;
                if (f.IsVector)
                {
                    var v = table.Vector(index);
                    if (v.IsNull) continue;
                    w.Name(f.Name);
                    WriteVector(v, f.Type, schema, w, depth);
                }
                else if (f.Type == "string")
                {
                    string s = table.String(index);
                    if (s != null) w.Name(f.Name).Value(s);
                }
                else if (Scalar.Size(f.Type) > 0 || schema.Enums.ContainsKey(f.Type))
                {
                    w.Name(f.Name);
                    WriteScalarField(table, index, f, schema, w);
                }
                else if (schema.Types.TryGetValue(f.Type, out var sub))
                {
                    if (sub.IsStruct)
                    {
                        int p = table.InlineStruct(index);
                        if (p == 0) continue;
                        w.Name(f.Name);
                        WriteStruct(table.Buffer, p, sub, schema, w);
                    }
                    else
                    {
                        var t = table.Table(index);
                        if (t.IsNull) continue;
                        w.Name(f.Name);
                        WriteTable(t, f.Type, schema, w, depth + 1);
                    }
                }
            }
            w.EndObject();
        }

        static void WriteVector(FbVector v, string elem, FbsSchema schema, JsonWriter w, int depth)
        {
            string scalar = Scalar.Resolve(elem, schema);
            if (scalar == "ubyte" || scalar == "byte")
            {
                var bytes = v.Bytes();
                w.Value(Convert.ToBase64String(bytes.Array, bytes.Offset, bytes.Count));
                return;
            }
            w.BeginArray();
            if (elem == "string")
                for (int i = 0; i < v.Length; i++) w.Value(v.String(i));
            else if (scalar != null)
            {
                int size = Scalar.Size(scalar);
                schema.Enums.TryGetValue(elem, out var en);
                for (int i = 0; i < v.Length; i++)
                    WriteScalar(v.Buffer, v.StructAt(i, size), scalar, en, w);
            }
            else if (schema.Types.TryGetValue(elem, out var t))
            {
                if (t.IsStruct)
                {
                    int size = StructLayout(t, schema).size;
                    for (int i = 0; i < v.Length; i++) WriteStruct(v.Buffer, v.StructAt(i, size), t, schema, w);
                }
                else
                    for (int i = 0; i < v.Length; i++) WriteTable(v.Table(i), elem, schema, w, depth + 1);
            }
            w.EndArray();
        }

        static void WriteScalarField(FbTable table, int index, FbsSchema.Field f, FbsSchema schema, JsonWriter w)
        {
            string scalar = Scalar.Resolve(f.Type, schema);
            schema.Enums.TryGetValue(f.Type, out var en);
            int p = table.FieldPosition(index);
            if (p != 0) { WriteScalar(table.Buffer, p, scalar, en, w); return; }
            // Absent: the schema default (an enum default may be a name).
            string d = f.Default ?? (scalar == "bool" ? "false" : "0");
            if (en != null && !long.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) { w.Value(d); return; }
            if (en != null && long.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out long dv) && en.Values.TryGetValue(dv, out var dn)) { w.Value(dn); return; }
            if (scalar == "bool") w.Raw(d == "true" ? "true" : "false");
            else w.Raw(double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out double num) ? num.ToString("R", CultureInfo.InvariantCulture) : "0");
        }

        static void WriteScalar(byte[] b, int p, string scalar, FbsSchema.EnumDef en, JsonWriter w)
        {
            double v;
            switch (scalar)
            {
                case "bool": w.Raw(b[p] != 0 ? "true" : "false"); return;
                case "byte": v = (sbyte)b[p]; break;
                case "ubyte": v = b[p]; break;
                case "short": v = BitConverter.ToInt16(b, p); break;
                case "ushort": v = BitConverter.ToUInt16(b, p); break;
                case "int": v = BitConverter.ToInt32(b, p); break;
                case "uint": v = BitConverter.ToUInt32(b, p); break;
                case "long": w.Raw(BitConverter.ToInt64(b, p).ToString(CultureInfo.InvariantCulture)); return;
                case "ulong": w.Raw(BitConverter.ToUInt64(b, p).ToString(CultureInfo.InvariantCulture)); return;
                case "float": v = BitConverter.ToSingle(b, p); break;
                case "double": v = BitConverter.ToDouble(b, p); break;
                default: w.Null(); return;
            }
            if (en != null && en.Values.TryGetValue((long)v, out var name)) { w.Value(name); return; }
            w.Raw(double.IsNaN(v) || double.IsInfinity(v) ? "null" : v.ToString("R", CultureInfo.InvariantCulture));
        }

        static void WriteStruct(byte[] b, int p, FbsSchema.Composite s, FbsSchema schema, JsonWriter w)
        {
            w.BeginObject();
            foreach (var (field, offset) in StructLayout(s, schema).fields)
            {
                w.Name(field.Name);
                if (schema.Types.TryGetValue(field.Type, out var inner) && inner.IsStruct) WriteStruct(b, p + offset, inner, schema, w);
                else
                {
                    schema.Enums.TryGetValue(field.Type, out var en);
                    WriteScalar(b, p + offset, Scalar.Resolve(field.Type, schema), en, w);
                }
            }
            w.EndObject();
        }

        // FlatBuffers struct layout: each field aligned to its own alignment, struct padded to its largest.
        static (int size, int align, List<(FbsSchema.Field, int)> fields) StructLayout(FbsSchema.Composite s, FbsSchema schema)
        {
            int offset = 0, align = 1;
            var fields = new List<(FbsSchema.Field, int)>();
            foreach (var f in s.Fields)
            {
                int size, a;
                if (schema.Types.TryGetValue(f.Type, out var inner) && inner.IsStruct)
                {
                    var l = StructLayout(inner, schema);
                    size = l.size; a = l.align;
                }
                else { size = Scalar.Size(Scalar.Resolve(f.Type, schema)); a = size; }
                offset = (offset + a - 1) / a * a;
                fields.Add((f, offset));
                offset += size;
                align = Math.Max(align, a);
            }
            return ((offset + align - 1) / align * align, align, fields);
        }

        static class Scalar
        {
            public static string Resolve(string type, FbsSchema schema)
            {
                if (schema.Enums.TryGetValue(type, out var e) && !e.IsUnion) type = e.Underlying;
                switch (type)
                {
                    case "bool": return "bool";
                    case "byte": case "int8": return "byte";
                    case "ubyte": case "uint8": return "ubyte";
                    case "short": case "int16": return "short";
                    case "ushort": case "uint16": return "ushort";
                    case "int": case "int32": return "int";
                    case "uint": case "uint32": return "uint";
                    case "long": case "int64": return "long";
                    case "ulong": case "uint64": return "ulong";
                    case "float": case "float32": return "float";
                    case "double": case "float64": return "double";
                    default: return null;
                }
            }

            public static int Size(string type)
            {
                switch (type)
                {
                    case "bool": case "byte": case "ubyte": case "int8": case "uint8": return 1;
                    case "short": case "ushort": case "int16": case "uint16": return 2;
                    case "int": case "uint": case "int32": case "uint32": case "float": case "float32": return 4;
                    case "long": case "ulong": case "int64": case "uint64": case "double": case "float64": return 8;
                    default: return 0;
                }
            }
        }
    }
}
