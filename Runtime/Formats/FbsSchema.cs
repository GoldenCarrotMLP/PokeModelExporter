using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PokeModelExporter.Formats
{
    // A parsed FlatBuffers schema (.fbs): enough of the language for the game format schemas
    // (namespace, table, struct, enum, union, root_type, defaults, comments). Used to turn any
    // FlatBuffer binary into readable JSON without generated code (see FlatBufferJson).
    public sealed class FbsSchema
    {
        public sealed class Field
        {
            public string Name;
            public string Type;        // scalar name, "string", a struct/table/enum/union name
            public bool IsVector;      // [Type]
            public string Default;     // as written in the schema, or null
        }

        public sealed class Composite
        {
            public string Name;
            public bool IsStruct;
            public readonly List<Field> Fields = new List<Field>();
        }

        public sealed class EnumDef
        {
            public string Name;
            public string Underlying = "int";
            public bool IsUnion;
            public readonly Dictionary<long, string> Values = new Dictionary<long, string>();
        }

        public string RootType { get; private set; }
        public readonly Dictionary<string, Composite> Types = new Dictionary<string, Composite>();
        public readonly Dictionary<string, EnumDef> Enums = new Dictionary<string, EnumDef>();

        public static FbsSchema Parse(string text)
        {
            var schema = new FbsSchema();
            var t = new Tokens(text);
            while (!t.End)
            {
                string word = t.Next();
                switch (word)
                {
                    case "namespace": case "include": case "attribute": case "file_extension": case "file_identifier":
                        t.SkipPast(";");
                        break;
                    case "root_type":
                        schema.RootType = t.Next();
                        t.SkipPast(";");
                        break;
                    case "table":
                    case "struct":
                        schema.ParseComposite(t, word == "struct");
                        break;
                    case "enum":
                        schema.ParseEnum(t, union: false);
                        break;
                    case "union":
                        schema.ParseEnum(t, union: true);
                        break;
                    default:
                        // Unknown statement: skip to its end.
                        if (word != ";" && word != "}") t.SkipPast(";");
                        break;
                }
            }
            return schema;
        }

        void ParseComposite(Tokens t, bool isStruct)
        {
            var c = new Composite { Name = t.Next(), IsStruct = isStruct };
            SkipAttributes(t);
            t.Expect("{");
            while (t.Peek() != "}")
            {
                var f = new Field { Name = t.Next() };
                t.Expect(":");
                if (t.Peek() == "[")
                {
                    t.Next();
                    f.Type = t.Next();
                    if (t.Peek() == ":") { t.Next(); t.Next(); } // fixed-size arrays [T:n] (unused here)
                    t.Expect("]");
                    f.IsVector = true;
                }
                else f.Type = t.Next();
                if (t.Peek() == "=") { t.Next(); f.Default = t.Next(); }
                SkipAttributes(t);
                t.Expect(";");
                c.Fields.Add(f);
            }
            t.Expect("}");
            Types[c.Name] = c;
        }

        void ParseEnum(Tokens t, bool union)
        {
            var e = new EnumDef { Name = t.Next(), IsUnion = union };
            if (!union && t.Peek() == ":") { t.Next(); e.Underlying = t.Next(); }
            SkipAttributes(t);
            t.Expect("{");
            long next = union ? 1 : 0; // union value 0 is NONE
            while (t.Peek() != "}")
            {
                string name = t.Next();
                if (t.Peek() == ":") { t.Next(); name = t.Next(); } // union alias: Name: Type
                if (t.Peek() == "=") { t.Next(); next = long.Parse(t.Next(), CultureInfo.InvariantCulture); }
                e.Values[next++] = name;
                if (t.Peek() == ",") t.Next();
            }
            t.Expect("}");
            Enums[e.Name] = e;
        }

        static void SkipAttributes(Tokens t)
        {
            if (t.Peek() != "(") return;
            int depth = 0;
            do
            {
                string s = t.Next();
                if (s == "(") depth++;
                else if (s == ")") depth--;
            } while (depth > 0 && !t.End);
        }

        sealed class Tokens
        {
            readonly List<string> _tokens = new List<string>();
            int _i;

            public Tokens(string s)
            {
                int i = 0;
                while (i < s.Length)
                {
                    char c = s[i];
                    if (char.IsWhiteSpace(c)) { i++; continue; }
                    if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
                    if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') { int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? s.Length : end + 2; continue; }
                    if (c == '"')
                    {
                        int end = s.IndexOf('"', i + 1);
                        if (end < 0) end = s.Length - 1;
                        _tokens.Add(s.Substring(i, end - i + 1));
                        i = end + 1;
                        continue;
                    }
                    if ("{}[]():;=,".IndexOf(c) >= 0) { _tokens.Add(c.ToString()); i++; continue; }
                    var sb = new StringBuilder();
                    while (i < s.Length && !char.IsWhiteSpace(s[i]) && "{}[]():;=,\"".IndexOf(s[i]) < 0) sb.Append(s[i++]);
                    _tokens.Add(sb.ToString());
                }
            }

            public bool End => _i >= _tokens.Count;
            public string Peek() => _i < _tokens.Count ? _tokens[_i] : "";
            public string Next() => _i < _tokens.Count ? _tokens[_i++] : "";

            public void Expect(string s)
            {
                string got = Next();
                if (got != s) throw new FormatException($"Schema: expected '{s}', got '{got}'");
            }

            public void SkipPast(string s)
            {
                while (!End && Next() != s) { }
            }
        }
    }
}
