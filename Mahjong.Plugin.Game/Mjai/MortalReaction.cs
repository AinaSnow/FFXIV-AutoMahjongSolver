using System.Text.Json;

namespace Mahjong.Plugin.Game.Mjai;

public sealed record MortalReaction(
    string Type,
    int? Actor,
    int? Target,
    string? Pai,
    string[] Consumed)
{
    public static bool TryParse(string json, out MortalReaction? reaction)
    {
        reaction = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeValue)
                || typeValue.ValueKind != JsonValueKind.String)
                return false;

            string? type = typeValue.GetString();
            if (string.IsNullOrWhiteSpace(type))
                return false;

            reaction = new MortalReaction(
                type,
                ReadOptionalInt(root, "actor"),
                ReadOptionalInt(root, "target"),
                ReadOptionalString(root, "pai"),
                ReadStringArray(root, "consumed"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int? ReadOptionalInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out int result)
            ? result
            : null;

    private static string? ReadOptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string[] ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
    }
}
