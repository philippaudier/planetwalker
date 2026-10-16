using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Planetwalker.Persistence
{
    public enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object,
    }

    /// <summary>
    /// Minimal JSON document node for world archives (V0.1.3A). Numbers keep their source text so that
    /// integers and floats are converted exactly once, with the target type's own rules. Object members keep
    /// their order (stable, diff-friendly files).
    /// </summary>
    public sealed class JsonValue
    {
        readonly List<KeyValuePair<string, JsonValue>> members;
        readonly List<JsonValue> items;

        JsonValue(JsonKind kind, string text = null, bool boolean = false)
        {
            Kind = kind;
            Text = text;
            Bool = boolean;
            if (kind == JsonKind.Object)
            {
                members = new List<KeyValuePair<string, JsonValue>>();
            }
            else if (kind == JsonKind.Array)
            {
                items = new List<JsonValue>();
            }
        }

        public JsonKind Kind { get; }

        /// <summary>String value, or the exact source text of a number.</summary>
        public string Text { get; }

        public bool Bool { get; }

        public static readonly JsonValue Null = new JsonValue(JsonKind.Null);

        public static JsonValue NewObject() => new JsonValue(JsonKind.Object);
        public static JsonValue NewArray() => new JsonValue(JsonKind.Array);
        public static JsonValue From(string value) => value == null ? Null : new JsonValue(JsonKind.String, value);
        public static JsonValue From(bool value) => new JsonValue(JsonKind.Bool, null, value);
        public static JsonValue From(int value) => new JsonValue(JsonKind.Number, value.ToString(CultureInfo.InvariantCulture));

        /// <summary>A finite float, written with the shortest text that parses back to the same value.</summary>
        public static JsonValue From(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentException("JSON cannot represent NaN or Infinity.");
            }
            return new JsonValue(JsonKind.Number, FormatFloat(value));
        }

        internal static JsonValue NumberFromText(string text) => new JsonValue(JsonKind.Number, text);

        public IReadOnlyList<KeyValuePair<string, JsonValue>> Members => members;
        public IReadOnlyList<JsonValue> Items => items;

        /// <summary>Adds (object) a member. Duplicate names are a programming error.</summary>
        public JsonValue Add(string name, JsonValue value)
        {
            if (Find(name) != null)
            {
                throw new ArgumentException("Duplicate JSON member '" + name + "'.");
            }
            members.Add(new KeyValuePair<string, JsonValue>(name, value ?? Null));
            return this;
        }

        public void Append(JsonValue value)
        {
            items.Add(value ?? Null);
        }

        /// <summary>Member by name, or null when absent (or when this is not an object).</summary>
        public JsonValue Find(string name)
        {
            if (members == null)
            {
                return null;
            }
            for (int i = 0; i < members.Count; i++)
            {
                if (members[i].Key == name)
                {
                    return members[i].Value;
                }
            }
            return null;
        }

        public bool Remove(string name)
        {
            if (members == null)
            {
                return false;
            }
            for (int i = 0; i < members.Count; i++)
            {
                if (members[i].Key == name)
                {
                    members.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>True when this is a JSON integer (no fraction, no exponent) that fits an int.</summary>
        public bool TryGetInt(out int value)
        {
            value = 0;
            return Kind == JsonKind.Number && Text.IndexOfAny(NonIntegerChars) < 0
                && int.TryParse(Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>True when this is a number whose float value is finite.</summary>
        public bool TryGetFloat(out float value)
        {
            value = 0f;
            if (Kind != JsonKind.Number)
            {
                return false;
            }
            try
            {
                value = float.Parse(Text, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                return false;
            }
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        static readonly char[] NonIntegerChars = { '.', 'e', 'E' };

        /// <summary>Shortest round-trip text of a finite float (invariant culture).</summary>
        public static string FormatFloat(float value)
        {
            for (int precision = 6; precision < 9; precision++)
            {
                string candidate = value.ToString("G" + precision, CultureInfo.InvariantCulture);
                if (float.Parse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture) == value)
                {
                    return candidate;
                }
            }
            return value.ToString("G9", CultureInfo.InvariantCulture);
        }

        /// <summary>Indented JSON text (2 spaces, '\n' line ends, UTF-16 string; non-ASCII kept as is).</summary>
        public string ToJson()
        {
            var builder = new StringBuilder(4096);
            Write(builder, this, 0);
            builder.Append('\n');
            return builder.ToString();
        }

        static void Write(StringBuilder b, JsonValue v, int indent)
        {
            switch (v.Kind)
            {
                case JsonKind.Null:
                    b.Append("null");
                    break;
                case JsonKind.Bool:
                    b.Append(v.Bool ? "true" : "false");
                    break;
                case JsonKind.Number:
                    b.Append(v.Text);
                    break;
                case JsonKind.String:
                    WriteString(b, v.Text);
                    break;
                case JsonKind.Array:
                    if (v.items.Count == 0)
                    {
                        b.Append("[]");
                        break;
                    }
                    b.Append("[\n");
                    for (int i = 0; i < v.items.Count; i++)
                    {
                        b.Append(' ', (indent + 1) * 2);
                        Write(b, v.items[i], indent + 1);
                        b.Append(i + 1 < v.items.Count ? ",\n" : "\n");
                    }
                    b.Append(' ', indent * 2).Append(']');
                    break;
                case JsonKind.Object:
                    if (v.members.Count == 0)
                    {
                        b.Append("{}");
                        break;
                    }
                    b.Append("{\n");
                    for (int i = 0; i < v.members.Count; i++)
                    {
                        b.Append(' ', (indent + 1) * 2);
                        WriteString(b, v.members[i].Key);
                        b.Append(": ");
                        Write(b, v.members[i].Value, indent + 1);
                        b.Append(i + 1 < v.members.Count ? ",\n" : "\n");
                    }
                    b.Append(' ', indent * 2).Append('}');
                    break;
            }
        }

        static void WriteString(StringBuilder b, string s)
        {
            b.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == 0x7F || c == (char)0x2028 || c == (char)0x2029)
                        {
                            b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            b.Append(c);
                        }
                        break;
                }
            }
            b.Append('"');
        }
    }

    /// <summary>Malformed JSON: message with the 1-based line and column of the problem.</summary>
    public sealed class JsonFormatException : FormatException
    {
        public JsonFormatException(string message) : base(message) { }
    }

    /// <summary>
    /// Strict RFC 8259 JSON reader for untrusted archive files: no comments, no trailing commas, no NaN /
    /// Infinity literals, no duplicate member names, bounded nesting depth. Throws
    /// <see cref="JsonFormatException"/> on any violation; never evaluates anything.
    /// </summary>
    public sealed class JsonReader
    {
        public const int MaxDepth = 32;

        readonly string text;
        int position;
        int depth;

        JsonReader(string text)
        {
            this.text = text;
        }

        public static JsonValue Parse(string text)
        {
            if (text == null)
            {
                throw new JsonFormatException("Empty document.");
            }
            var reader = new JsonReader(text);
            reader.SkipWhitespace();
            if (reader.position < text.Length && text[reader.position] == (char)0xFEFF)
            {
                reader.position++; // tolerate a UTF-8 byte order mark decoded as U+FEFF
                reader.SkipWhitespace();
            }
            JsonValue value = reader.ReadValue();
            reader.SkipWhitespace();
            if (reader.position != text.Length)
            {
                throw reader.Error("Unexpected content after the JSON document");
            }
            return value;
        }

        JsonValue ReadValue()
        {
            SkipWhitespace();
            if (position >= text.Length)
            {
                throw Error("Unexpected end of document");
            }
            char c = text[position];
            switch (c)
            {
                case '{': return ReadObject();
                case '[': return ReadArray();
                case '"': return JsonValue.From(ReadString());
                case 't': ExpectLiteral("true"); return JsonValue.From(true);
                case 'f': ExpectLiteral("false"); return JsonValue.From(false);
                case 'n': ExpectLiteral("null"); return JsonValue.Null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9'))
                    {
                        return ReadNumber();
                    }
                    throw Error("Unexpected character '" + c + "'");
            }
        }

        JsonValue ReadObject()
        {
            Enter();
            position++; // {
            JsonValue obj = JsonValue.NewObject();
            SkipWhitespace();
            if (Peek() == '}')
            {
                position++;
                depth--;
                return obj;
            }
            while (true)
            {
                SkipWhitespace();
                if (Peek() != '"')
                {
                    throw Error("Expected a member name");
                }
                int nameStart = position;
                string name = ReadString();
                if (obj.Find(name) != null)
                {
                    position = nameStart;
                    throw Error("Duplicate member '" + name + "'");
                }
                SkipWhitespace();
                if (Peek() != ':')
                {
                    throw Error("Expected ':'");
                }
                position++;
                obj.Add(name, ReadValue());
                SkipWhitespace();
                char c = Peek();
                position++;
                if (c == '}')
                {
                    break;
                }
                if (c != ',')
                {
                    position--;
                    throw Error("Expected ',' or '}'");
                }
            }
            depth--;
            return obj;
        }

        JsonValue ReadArray()
        {
            Enter();
            position++; // [
            JsonValue array = JsonValue.NewArray();
            SkipWhitespace();
            if (Peek() == ']')
            {
                position++;
                depth--;
                return array;
            }
            while (true)
            {
                array.Append(ReadValue());
                SkipWhitespace();
                char c = Peek();
                position++;
                if (c == ']')
                {
                    break;
                }
                if (c != ',')
                {
                    position--;
                    throw Error("Expected ',' or ']'");
                }
            }
            depth--;
            return array;
        }

        string ReadString()
        {
            position++; // opening quote
            var builder = new StringBuilder();
            while (true)
            {
                if (position >= text.Length)
                {
                    throw Error("Unterminated string");
                }
                char c = text[position++];
                if (c == '"')
                {
                    break;
                }
                if (c < 0x20)
                {
                    position--;
                    throw Error("Unescaped control character in string");
                }
                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }
                if (position >= text.Length)
                {
                    throw Error("Unterminated escape");
                }
                char e = text[position++];
                switch (e)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (position + 4 > text.Length || !int.TryParse(text.Substring(position, 4), NumberStyles.AllowHexSpecifier,
                            CultureInfo.InvariantCulture, out int code))
                        {
                            throw Error("Invalid \\u escape");
                        }
                        builder.Append((char)code);
                        position += 4;
                        break;
                    default:
                        position--;
                        throw Error("Invalid escape '\\" + e + "'");
                }
            }
            return builder.ToString();
        }

        JsonValue ReadNumber()
        {
            int start = position;
            if (Peek() == '-')
            {
                position++;
            }
            if (Peek() == '0')
            {
                position++;
            }
            else if (IsDigit(Peek()))
            {
                while (IsDigit(Peek()))
                {
                    position++;
                }
            }
            else
            {
                throw Error("Invalid number");
            }
            if (Peek() == '.')
            {
                position++;
                if (!IsDigit(Peek()))
                {
                    throw Error("Invalid number: digit expected after '.'");
                }
                while (IsDigit(Peek()))
                {
                    position++;
                }
            }
            if (Peek() == 'e' || Peek() == 'E')
            {
                position++;
                if (Peek() == '+' || Peek() == '-')
                {
                    position++;
                }
                if (!IsDigit(Peek()))
                {
                    throw Error("Invalid number: digit expected in exponent");
                }
                while (IsDigit(Peek()))
                {
                    position++;
                }
            }
            if (position - start > 64)
            {
                throw Error("Number too long");
            }
            return JsonValue.NumberFromText(text.Substring(start, position - start));
        }

        void ExpectLiteral(string literal)
        {
            if (string.CompareOrdinal(text, position, literal, 0, literal.Length) != 0)
            {
                throw Error("Invalid literal");
            }
            position += literal.Length;
        }

        void Enter()
        {
            if (++depth > MaxDepth)
            {
                throw Error("Nesting deeper than " + MaxDepth);
            }
        }

        char Peek() => position < text.Length ? text[position] : '\0';

        static bool IsDigit(char c) => c >= '0' && c <= '9';

        void SkipWhitespace()
        {
            while (position < text.Length)
            {
                char c = text[position];
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r')
                {
                    return;
                }
                position++;
            }
        }

        JsonFormatException Error(string message)
        {
            int line = 1, column = 1;
            for (int i = 0; i < position && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }
            return new JsonFormatException(message + " (line " + line + ", column " + column + ").");
        }
    }
}
