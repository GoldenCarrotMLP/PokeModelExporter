using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PokeModelExporter.Export
{
    // Small streaming JSON writer with indentation (System.Text.Json isn't available in Unity).
    public sealed class JsonWriter
    {
        readonly StringBuilder _sb = new StringBuilder();
        readonly Stack<bool> _first = new Stack<bool>(); // per open container: no element written yet
        bool _afterName;

        public override string ToString() => _sb.ToString();

        public JsonWriter BeginObject() { Element(); _sb.Append('{'); _first.Push(true); return this; }
        public JsonWriter EndObject() { Close('}'); return this; }
        public JsonWriter BeginArray() { Element(); _sb.Append('['); _first.Push(true); return this; }
        public JsonWriter EndArray() { Close(']'); return this; }

        public JsonWriter Name(string name)
        {
            Separator();
            Quote(name);
            _sb.Append(": ");
            _afterName = true;
            return this;
        }

        public JsonWriter Value(string s) { Element(); if (s == null) _sb.Append("null"); else Quote(s); return this; }
        public JsonWriter Value(long v) { Element(); _sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }
        public JsonWriter Value(double v) { Element(); _sb.Append(double.IsNaN(v) || double.IsInfinity(v) ? "null" : v.ToString("R", CultureInfo.InvariantCulture)); return this; }
        public JsonWriter Value(bool v) { Element(); _sb.Append(v ? "true" : "false"); return this; }
        public JsonWriter Null() { Element(); _sb.Append("null"); return this; }

        // Pre-formatted JSON (a number, true/false, or a whole value).
        public JsonWriter Raw(string json) { Element(); _sb.Append(json); return this; }

        void Element()
        {
            if (_afterName) { _afterName = false; return; }
            Separator();
        }

        void Separator()
        {
            if (_first.Count == 0) return;
            if (!_first.Peek()) _sb.Append(',');
            _first.Pop();
            _first.Push(false);
            _sb.Append('\n').Append(' ', _first.Count * 2);
        }

        void Close(char c)
        {
            bool empty = _first.Pop();
            if (!empty) _sb.Append('\n').Append(' ', _first.Count * 2);
            _sb.Append(c);
        }

        void Quote(string s)
        {
            _sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) _sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else _sb.Append(c);
                        break;
                }
            }
            _sb.Append('"');
        }
    }
}
