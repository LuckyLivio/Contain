using System.Text.Json;

namespace Contain.Viewer;

/// <summary>Display and equality helpers; this does not infer attribution or ownership.</summary>
public static class JsonView
{
    public static JsonElement Field(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) ? result : default;

    public static string Text(JsonElement value, string missing = "未记录") => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? missing,
        JsonValueKind.Null or JsonValueKind.Undefined => missing,
        _ => value.GetRawText()
    };

    public static string Text(JsonElement value, string name, string missing) => Text(Field(value, name), missing);

    public static IEnumerable<JsonElement> Items(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();

    public static bool JsonEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        return left.ValueKind switch
        {
            JsonValueKind.Object => ObjectsEqual(left, right),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength() &&
                left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => JsonEqual(pair.First, pair.Second)),
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.GetRawText() == right.GetRawText(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => true,
            _ => false
        };
    }

    private static bool ObjectsEqual(JsonElement left, JsonElement right)
    {
        var a = left.EnumerateObject().ToArray();
        var b = right.EnumerateObject().ToArray();
        return a.Length == b.Length && a.All(field => right.TryGetProperty(field.Name, out var value) && JsonEqual(field.Value, value));
    }
}
