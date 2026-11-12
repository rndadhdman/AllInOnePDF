using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllInOnePdf.Core.Conversion;

/// <summary>Kind of control the UI should render for an option.</summary>
public enum OptionType
{
    Boolean,
    Integer,
    Number,
    Text,
    MultilineText,
    /// <summary>One of <see cref="OptionDefinition.Choices"/>.</summary>
    Choice,
    /// <summary>Hex colour string "#RRGGBB".</summary>
    Color,
    /// <summary>A font family name (bundled or system).</summary>
    Font,
    /// <summary>A PDF theme id; the UI shows the theme picker.</summary>
    Theme,
    /// <summary>A page range string such as "1-3,7".</summary>
    PageRange,
}

public sealed record OptionChoice(string Value, string Label, string? Description = null);

/// <summary>
/// Describes one user-facing option of an importer or exporter. The UI renders these generically,
/// so a converter adds options by declaring them, not by touching the UI.
/// </summary>
public sealed record OptionDefinition(
    string Key,
    string Label,
    OptionType Type,
    object? DefaultValue,
    string? Description = null,
    string? Group = null,
    double? Min = null,
    double? Max = null,
    double? Step = null,
    IReadOnlyList<OptionChoice>? Choices = null,
    string? Unit = null,
    bool Advanced = false,
    /// <summary>Key of a boolean option that must be true for this option to be shown.</summary>
    string? VisibleWhen = null)
{
    public static OptionDefinition Bool(string key, string label, bool @default, string? description = null, string? group = null, bool advanced = false, string? visibleWhen = null) =>
        new(key, label, OptionType.Boolean, @default, description, group, Advanced: advanced, VisibleWhen: visibleWhen);

    public static OptionDefinition Int(string key, string label, int @default, int min, int max, string? description = null, string? group = null, string? unit = null, bool advanced = false, string? visibleWhen = null) =>
        new(key, label, OptionType.Integer, @default, description, group, min, max, 1, Unit: unit, Advanced: advanced, VisibleWhen: visibleWhen);

    public static OptionDefinition Number(string key, string label, double @default, double min, double max, double step = 0.1, string? description = null, string? group = null, string? unit = null, bool advanced = false, string? visibleWhen = null) =>
        new(key, label, OptionType.Number, @default, description, group, min, max, step, Unit: unit, Advanced: advanced, VisibleWhen: visibleWhen);

    public static OptionDefinition Text(string key, string label, string? @default, string? description = null, string? group = null, bool advanced = false, string? visibleWhen = null) =>
        new(key, label, OptionType.Text, @default, description, group, Advanced: advanced, VisibleWhen: visibleWhen);

    public static OptionDefinition Choice(string key, string label, string @default, IReadOnlyList<OptionChoice> choices, string? description = null, string? group = null, bool advanced = false, string? visibleWhen = null) =>
        new(key, label, OptionType.Choice, @default, description, group, Choices: choices, Advanced: advanced, VisibleWhen: visibleWhen);

    public static OptionDefinition Enum<TEnum>(string key, string label, TEnum @default, string? description = null, string? group = null, bool advanced = false, string? visibleWhen = null)
        where TEnum : struct, Enum
    {
        var choices = System.Enum.GetValues<TEnum>()
            .Select(v => new OptionChoice(v.ToString(), SplitPascalCase(v.ToString())))
            .ToList();
        return new OptionDefinition(key, label, OptionType.Choice, @default.ToString(), description, group, Choices: choices, Advanced: advanced, VisibleWhen: visibleWhen);
    }

    public static OptionDefinition Theme(string key, string label, string? defaultThemeId, string? description = null, string? group = null) =>
        new(key, label, OptionType.Theme, defaultThemeId, description, group);

    /// <summary>A page range such as "1-3,7"; empty means all pages.</summary>
    public static OptionDefinition PageRange(string key, string label, string? @default = null, string? description = null, string? group = null, bool advanced = false) =>
        new(key, label, OptionType.PageRange, @default ?? string.Empty, description, group, Advanced: advanced);

    private static string SplitPascalCase(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]) && !char.IsUpper(value[i - 1]))
            {
                sb.Append(' ');
            }

            sb.Append(value[i]);
        }

        return sb.ToString();
    }
}

/// <summary>
/// A bag of option values keyed by <see cref="OptionDefinition.Key"/>. Values are stored as primitives
/// (bool, long/int, double, string) so the bag serialises cleanly to JSON for presets and history.
/// </summary>
[JsonConverter(typeof(OptionValuesJsonConverter))]
public sealed class OptionValues
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    public static OptionValues Empty => new();

    public int Count => _values.Count;

    public IReadOnlyDictionary<string, object?> Values => _values;

    public bool Contains(string key) => _values.ContainsKey(key);

    public OptionValues Set(string key, object? value)
    {
        _values[key] = value;
        return this;
    }

    public OptionValues Remove(string key)
    {
        _values.Remove(key);
        return this;
    }

    public object? Get(string key) => _values.GetValueOrDefault(key);

    public bool GetBool(string key, bool @default = false)
    {
        var v = Get(key);
        return v switch
        {
            null => @default,
            bool b => b,
            string s when bool.TryParse(s, out var b) => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => @default,
        };
    }

    public int GetInt(string key, int @default = 0)
    {
        var v = Get(key);
        return v switch
        {
            null => @default,
            int i => i,
            long l => (int)l,
            double d => (int)Math.Round(d),
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
            JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt32(out var i) => i,
            _ => @default,
        };
    }

    public double GetDouble(string key, double @default = 0)
    {
        var v = Get(key);
        return v switch
        {
            null => @default,
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            JsonElement { ValueKind: JsonValueKind.Number } je => je.GetDouble(),
            _ => @default,
        };
    }

    public string? GetString(string key, string? @default = null)
    {
        var v = Get(key);
        return v switch
        {
            null => @default,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            JsonElement je => je.ToString(),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture),
        };
    }

    public TEnum GetEnum<TEnum>(string key, TEnum @default)
        where TEnum : struct, Enum
    {
        var s = GetString(key);
        return s is not null && Enum.TryParse<TEnum>(s, ignoreCase: true, out var e) ? e : @default;
    }

    /// <summary>Fills in defaults from the definitions for any key not already set.</summary>
    public OptionValues WithDefaults(IEnumerable<OptionDefinition> definitions)
    {
        var copy = Clone();
        foreach (var def in definitions)
        {
            if (!copy._values.ContainsKey(def.Key))
            {
                copy._values[def.Key] = def.DefaultValue;
            }
        }

        return copy;
    }

    public static OptionValues FromDefaults(IEnumerable<OptionDefinition> definitions) => Empty.WithDefaults(definitions);

    public OptionValues Clone()
    {
        var copy = new OptionValues();
        foreach (var (k, v) in _values)
        {
            copy._values[k] = v;
        }

        return copy;
    }

    public string ToJson() => JsonSerializer.Serialize(this, CoreJson.Options);

    public static OptionValues FromJson(string json) =>
        JsonSerializer.Deserialize<OptionValues>(json, CoreJson.Options) ?? new OptionValues();
}

internal sealed class OptionValuesJsonConverter : JsonConverter<OptionValues>
{
    public override OptionValues Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new OptionValues();
        if (reader.TokenType == JsonTokenType.Null)
        {
            return result;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected object for OptionValues");
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var key = reader.GetString()!;
            reader.Read();
            object? value = reader.TokenType switch
            {
                JsonTokenType.True => true,
                JsonTokenType.False => false,
                JsonTokenType.Null => null,
                JsonTokenType.Number => reader.TryGetInt32(out var i) ? i : reader.GetDouble(),
                JsonTokenType.String => reader.GetString(),
                _ => JsonDocument.ParseValue(ref reader).RootElement.Clone(),
            };
            result.Set(key, value);
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, OptionValues value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (k, v) in value.Values)
        {
            writer.WritePropertyName(k);
            switch (v)
            {
                case null:
                    writer.WriteNullValue();
                    break;
                case bool b:
                    writer.WriteBooleanValue(b);
                    break;
                case int i:
                    writer.WriteNumberValue(i);
                    break;
                case long l:
                    writer.WriteNumberValue(l);
                    break;
                case double d:
                    writer.WriteNumberValue(d);
                    break;
                case float f:
                    writer.WriteNumberValue(f);
                    break;
                case decimal m:
                    writer.WriteNumberValue(m);
                    break;
                case string s:
                    writer.WriteStringValue(s);
                    break;
                case Enum e:
                    writer.WriteStringValue(e.ToString());
                    break;
                case JsonElement je:
                    je.WriteTo(writer);
                    break;
                default:
                    JsonSerializer.Serialize(writer, v, v.GetType(), options);
                    break;
            }
        }

        writer.WriteEndObject();
    }
}

/// <summary>Shared JSON settings for everything the app persists (themes, history, presets).</summary>
public static class CoreJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
