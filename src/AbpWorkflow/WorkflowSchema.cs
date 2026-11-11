using System.Text.Json;

namespace AbpWorkflow;

/// <summary>
/// Checks extension settings against their JSON Schema: the subset extensions use (type, required,
/// properties, items, enum). Unknown keywords are ignored; the extension's settings check does the rest.
/// </summary>
public static class WorkflowSchema
{
    public static List<string> Check(string schemaText, JsonElement? settings, string where)
    {
        List<string> problems = [];
        using JsonDocument schema = JsonDocument.Parse(schemaText);
        JsonElement value = settings is { ValueKind: not JsonValueKind.Undefined } s ? s : JsonDocument.Parse("{}").RootElement;
        Check(schema.RootElement, value, where, problems);
        return problems;
    }

    private static void Check(JsonElement schema, JsonElement value, string path, List<string> problems)
    {
        if (schema.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String && !Is(type.GetString()!, value))
        {
            problems.Add($"{path} should be {Article(type.GetString()!)}.");
            return;
        }

        if (schema.TryGetProperty("enum", out JsonElement options) && options.ValueKind == JsonValueKind.Array
            && !options.EnumerateArray().Any(o => o.GetRawText() == value.GetRawText()))
        {
            problems.Add($"{path} is one of {string.Join(", ", options.EnumerateArray().Select(o => o.ValueKind == JsonValueKind.String ? o.GetString() : o.GetRawText()))}.");
            return;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out JsonElement required) && required.ValueKind == JsonValueKind.Array)
            {
                foreach (string name in required.EnumerateArray().Select(r => r.GetString()!))
                {
                    if (!value.TryGetProperty(name, out JsonElement present) || present.ValueKind == JsonValueKind.Null)
                    {
                        problems.Add($"{path} needs {name}.");
                    }
                }
            }

            if (schema.TryGetProperty("properties", out JsonElement properties) && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    if (properties.TryGetProperty(property.Name, out JsonElement sub) && property.Value.ValueKind != JsonValueKind.Null)
                    {
                        Check(sub, property.Value, $"{path}.{property.Name}", problems);
                    }
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Object)
        {
            int i = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                Check(items, item, $"{path}[{i++}]", problems);
            }
        }
    }

    private static bool Is(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        _ => true,
    };

    private static string Article(string type) => type is "object" or "array" or "integer" ? $"an {type}" : $"a {type}";
}
