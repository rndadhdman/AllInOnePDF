using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AllInOnePdf.Core.Importers.Data;

/// <summary>Re-indents a parsed JSON value, optionally sorting object keys (ordinal), keeping non-ASCII text readable.</summary>
internal static class JsonPrettyPrinter
{
    public static string Format(JsonElement root, int indent, bool sortKeys)
    {
        var options = new JsonWriterOptions
        {
            Indented = true,
            IndentSize = Math.Clamp(indent, 1, 8),
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NewLine = "\n",
        };

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            Write(writer, root, sortKeys);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element, bool sortKeys)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                IEnumerable<JsonProperty> properties = element.EnumerateObject();
                if (sortKeys)
                {
                    properties = properties.OrderBy(p => p.Name, StringComparer.Ordinal);
                }

                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value, sortKeys);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(writer, item, sortKeys);
                }

                writer.WriteEndArray();
                break;
            default:
                // Numbers keep their original spelling (1.50 stays 1.50), strings their characters.
                element.WriteTo(writer);
                break;
        }
    }
}
