using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace Planetwalker.Persistence
{
    /// <summary>
    /// Field-level access to the project's settings classes (V0.1.3A), driven by the same rule as Unity
    /// serialization: public instance fields that are not [NonSerialized]. A new field added to a settings
    /// class is therefore archived, compared and copied automatically; an unsupported field type fails the
    /// archive tests instead of being dropped silently.
    /// </summary>
    /// <remarks>
    /// Supported field types: int, float, bool, string, enums (by name), Vector3, Color. Floats are compared
    /// exactly (archives store the shortest round-trip text, so a reloaded value is bit-identical).
    /// </remarks>
    public static class SettingsFields
    {
        static readonly Dictionary<Type, FieldInfo[]> FieldCache = new Dictionary<Type, FieldInfo[]>();

        /// <summary>Archived fields of a settings type, in declaration order.</summary>
        public static FieldInfo[] Of(Type type)
        {
            lock (FieldCache)
            {
                if (!FieldCache.TryGetValue(type, out FieldInfo[] fields))
                {
                    var list = new List<FieldInfo>();
                    foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!field.IsNotSerialized && !field.IsInitOnly && !field.IsLiteral)
                        {
                            list.Add(field);
                        }
                    }
                    list.Sort((a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
                    fields = list.ToArray();
                    FieldCache[type] = fields;
                }
                return fields;
            }
        }

        public static bool IsSupported(Type type)
        {
            return type == typeof(int) || type == typeof(float) || type == typeof(bool) || type == typeof(string) || type.IsEnum
                || type == typeof(Vector3) || type == typeof(Color);
        }

        public static T Clone<T>(T source) where T : class, new()
        {
            var copy = new T();
            Copy(source, copy);
            return copy;
        }

        /// <summary>Copies every archived field (value types and strings: no shared state).</summary>
        public static void Copy(object source, object destination)
        {
            foreach (FieldInfo field in Of(source.GetType()))
            {
                field.SetValue(destination, field.GetValue(source));
            }
        }

        /// <summary>True when every archived field is equal (floats compared exactly).</summary>
        public static bool AreEqual(object a, object b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }
            if (a == null || b == null || a.GetType() != b.GetType())
            {
                return false;
            }
            foreach (FieldInfo field in Of(a.GetType()))
            {
                if (!Equals(field.GetValue(a), field.GetValue(b)))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Names of the fields that differ (diagnostics, tests).</summary>
        public static List<string> Differences(object a, object b, string prefix)
        {
            var result = new List<string>();
            foreach (FieldInfo field in Of(a.GetType()))
            {
                if (!Equals(field.GetValue(a), field.GetValue(b)))
                {
                    result.Add(prefix + "." + field.Name);
                }
            }
            return result;
        }

        /// <summary>Writes the archived fields of a settings object as a JSON object.</summary>
        public static JsonValue Write(object settings, ICollection<string> excluded = null)
        {
            JsonValue obj = JsonValue.NewObject();
            foreach (FieldInfo field in Of(settings.GetType()))
            {
                if (excluded != null && excluded.Contains(field.Name))
                {
                    continue;
                }
                obj.Add(field.Name, WriteValue(field.FieldType, field.GetValue(settings), field.Name));
            }
            return obj;
        }

        static JsonValue WriteValue(Type type, object value, string name)
        {
            if (type == typeof(int)) return JsonValue.From((int)value);
            if (type == typeof(float)) return JsonValue.From((float)value);
            if (type == typeof(bool)) return JsonValue.From((bool)value);
            if (type == typeof(string)) return JsonValue.From((string)value);
            if (type.IsEnum) return JsonValue.From(value.ToString());
            if (type == typeof(Vector3))
            {
                var v = (Vector3)value;
                return JsonValue.NewObject().Add("x", JsonValue.From(v.x)).Add("y", JsonValue.From(v.y)).Add("z", JsonValue.From(v.z));
            }
            if (type == typeof(Color))
            {
                var c = (Color)value;
                return JsonValue.NewObject().Add("r", JsonValue.From(c.r)).Add("g", JsonValue.From(c.g)).Add("b", JsonValue.From(c.b))
                    .Add("a", JsonValue.From(c.a));
            }
            throw new NotSupportedException("Settings field '" + name + "' has an unsupported type " + type.Name + ".");
        }

        /// <summary>
        /// Reads a JSON object into a settings object that already holds the documented defaults. Missing
        /// fields keep their default (reported); unknown members are reported as warnings; a member of the
        /// wrong type, a non-finite or unparsable number is an error (the field keeps its default).
        /// </summary>
        public static void Read(JsonValue obj, object target, string group, ArchiveReport report, ICollection<string> excluded = null)
        {
            if (obj.Kind != JsonKind.Object)
            {
                report.Error(group + ": expected an object.");
                return;
            }
            FieldInfo[] fields = Of(target.GetType());
            foreach (FieldInfo field in fields)
            {
                if (excluded != null && excluded.Contains(field.Name))
                {
                    continue;
                }
                JsonValue value = obj.Find(field.Name);
                if (value == null)
                {
                    report.Defaulted(group + "." + field.Name);
                    continue;
                }
                if (TryReadValue(field.FieldType, value, group + "." + field.Name, report, out object parsed))
                {
                    field.SetValue(target, parsed);
                }
            }
            foreach (KeyValuePair<string, JsonValue> member in obj.Members)
            {
                if (excluded != null && excluded.Contains(member.Key))
                {
                    report.Warning(group + "." + member.Key + ": ignored (stored once, at the top level of the archive).");
                    continue;
                }
                bool known = false;
                foreach (FieldInfo field in fields)
                {
                    known |= field.Name == member.Key;
                }
                if (!known)
                {
                    report.Warning(group + "." + member.Key + ": unknown field ignored.");
                }
            }
        }

        static bool TryReadValue(Type type, JsonValue value, string path, ArchiveReport report, out object result)
        {
            result = null;
            if (type == typeof(int))
            {
                if (value.TryGetInt(out int i))
                {
                    result = i;
                    return true;
                }
                report.Error(path + ": expected a 32-bit integer.");
                return false;
            }
            if (type == typeof(float))
            {
                if (value.TryGetFloat(out float f))
                {
                    result = f;
                    return true;
                }
                report.Error(path + (value.Kind == JsonKind.Number ? ": number is not a finite float." : ": expected a number."));
                return false;
            }
            if (type == typeof(bool))
            {
                if (value.Kind == JsonKind.Bool)
                {
                    result = value.Bool;
                    return true;
                }
                report.Error(path + ": expected true or false.");
                return false;
            }
            if (type == typeof(string))
            {
                if (value.Kind == JsonKind.String)
                {
                    result = value.Text;
                    return true;
                }
                report.Error(path + ": expected a string.");
                return false;
            }
            if (type.IsEnum)
            {
                if (value.Kind == JsonKind.String && Array.IndexOf(Enum.GetNames(type), value.Text) >= 0)
                {
                    result = Enum.Parse(type, value.Text);
                    return true;
                }
                report.Error(path + ": expected one of " + string.Join(", ", Enum.GetNames(type)) + ".");
                return false;
            }
            if (type == typeof(Vector3) || type == typeof(Color))
            {
                string[] names = type == typeof(Vector3) ? new[] { "x", "y", "z" } : new[] { "r", "g", "b", "a" };
                if (value.Kind != JsonKind.Object)
                {
                    report.Error(path + ": expected an object {" + string.Join(", ", names) + "}.");
                    return false;
                }
                var components = new float[names.Length];
                bool ok = true;
                for (int i = 0; i < names.Length; i++)
                {
                    JsonValue component = value.Find(names[i]);
                    if (component == null || !component.TryGetFloat(out components[i]))
                    {
                        report.Error(path + "." + names[i] + ": expected a finite number.");
                        ok = false;
                    }
                }
                foreach (KeyValuePair<string, JsonValue> member in value.Members)
                {
                    if (Array.IndexOf(names, member.Key) < 0)
                    {
                        report.Warning(path + "." + member.Key + ": unknown field ignored.");
                    }
                }
                if (ok)
                {
                    result = type == typeof(Vector3)
                        ? (object)new Vector3(components[0], components[1], components[2])
                        : new Color(components[0], components[1], components[2], components[3]);
                }
                return ok;
            }
            report.Error(path + ": unsupported field type " + type.Name + ".");
            return false;
        }

        /// <summary>
        /// Range validation without repair: every float is finite; a field with a [Range] attribute is inside
        /// it; a Color is inside [0, 1]; and the class's own Sanitize() (the authoritative clamp / snap rules)
        /// must not change anything. Out-of-range values are errors, never silently clamped.
        /// </summary>
        public static void Validate(object settings, string group, ArchiveReport report)
        {
            int errorsBefore = report.ErrorCount;
            foreach (FieldInfo field in Of(settings.GetType()))
            {
                object value = field.GetValue(settings);
                string path = group + "." + field.Name;
                if (value is float f)
                {
                    if (float.IsNaN(f) || float.IsInfinity(f))
                    {
                        report.Error(path + ": not a finite number.");
                        continue;
                    }
                    var range = field.GetCustomAttribute<RangeAttribute>();
                    if (range != null && (f < range.min || f > range.max))
                    {
                        report.Error(path + ": " + Format(f) + " is outside [" + Format(range.min) + ", " + Format(range.max) + "].");
                    }
                }
                else if (value is int i)
                {
                    var range = field.GetCustomAttribute<RangeAttribute>();
                    if (range != null && (i < range.min || i > range.max))
                    {
                        report.Error(path + ": " + i + " is outside [" + Format(range.min) + ", " + Format(range.max) + "].");
                    }
                }
                else if (value is Vector3 v)
                {
                    if (!IsFinite(v.x) || !IsFinite(v.y) || !IsFinite(v.z))
                    {
                        report.Error(path + ": not a finite vector.");
                    }
                }
                else if (value is Color c)
                {
                    if (!(c.r >= 0f && c.r <= 1f && c.g >= 0f && c.g <= 1f && c.b >= 0f && c.b <= 1f && c.a >= 0f && c.a <= 1f))
                    {
                        report.Error(path + ": color components must be in [0, 1].");
                    }
                }
                else if (value != null && field.FieldType.IsEnum && !Enum.IsDefined(field.FieldType, value))
                {
                    report.Error(path + ": undefined value.");
                }
            }

            MethodInfo sanitize = settings.GetType().GetMethod("Sanitize", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (sanitize != null && report.ErrorCount == errorsBefore)
            {
                object copy = Activator.CreateInstance(settings.GetType());
                Copy(settings, copy);
                sanitize.Invoke(copy, null);
                foreach (string field in Differences(settings, copy, group))
                {
                    report.Error(field + ": value outside the allowed range or set of values.");
                }
            }
        }

        static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

        static string Format(float f) => f.ToString("G6", CultureInfo.InvariantCulture);
    }

    /// <summary>Errors, warnings and defaulted fields collected while reading or validating an archive.</summary>
    public sealed class ArchiveReport
    {
        readonly List<string> errors = new List<string>();
        readonly List<string> warnings = new List<string>();
        readonly List<string> defaulted = new List<string>();

        public IReadOnlyList<string> Errors => errors;
        public IReadOnlyList<string> Warnings => warnings;
        /// <summary>Optional fields that were absent and received their documented default.</summary>
        public IReadOnlyList<string> DefaultedFields => defaulted;
        public int ErrorCount => errors.Count;
        public bool Ok => errors.Count == 0;

        public void Error(string message) => errors.Add(message);
        public void Warning(string message) => warnings.Add(message);
        public void Defaulted(string field) => defaulted.Add(field);

        /// <summary>First error (or a short summary when there are several).</summary>
        public string Summary()
        {
            if (errors.Count == 0)
            {
                return "";
            }
            return errors.Count == 1 ? errors[0] : errors[0] + " (+" + (errors.Count - 1) + " more)";
        }
    }
}
