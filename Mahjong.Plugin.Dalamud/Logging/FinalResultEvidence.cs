using System.Text.Json;

namespace Mahjong.Plugin.Dalamud.Logging;

/// <summary>Updated settlement scores are final only if play never resumes before the archive closes.</summary>
internal sealed class FinalResultEvidence
{
    private int[]? scores;
    public bool Malformed { get; private set; }
    public int[]? Scores => Malformed ? null : scores;
    public string? MissingReason { get; private set; } = "final-result-unobserved";

    public void InvalidateMalformed()
    {
        Malformed = true; scores = null; MissingReason = "malformed-game-log";
    }

    public void Observe(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) { InvalidateMalformed(); return; }
        if (!row.TryGetProperty("e", out var kind) || kind.ValueKind != JsonValueKind.String) return;
        if (kind.GetString() is "hand-start" or "hand-incomplete")
        {
            scores = null; MissingReason = "hand-not-settled";
            return;
        }
        if (kind.GetString() != "state") return;
        if (row.TryGetProperty("hand", out var hand) && hand.ValueKind == JsonValueKind.Array && hand.GetArrayLength() > 0)
        {
            scores = null; MissingReason = "gameplay-after-settlement";
            return;
        }
        if (!row.TryGetProperty("state_code", out var state) || state.ValueKind != JsonValueKind.Number || !state.TryGetInt32(out int code) || code != 27) return;
        scores = null; MissingReason = "invalid-settlement-scores";
        if (hand.ValueKind != JsonValueKind.Array || hand.GetArrayLength() != 0
            || !row.TryGetProperty("scores", out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4) return;
        var candidate = new int[4];
        for (int i = 0; i < 4; i++)
            if (value[i].ValueKind != JsonValueKind.Number || !value[i].TryGetInt32(out candidate[i]) || candidate[i] is < -200000 or > 200000) return;
        if (candidate.All(x => x == 0)) return;
        scores = candidate;
        if (!Malformed) MissingReason = null;
    }

    public static FinalResultEvidence Read(IEnumerable<string> paths)
    {
        var result = new FinalResultEvidence();
        foreach (var path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { using var doc = JsonDocument.Parse(line); result.Observe(doc.RootElement); }
            catch (JsonException) { result.InvalidateMalformed(); }
        }
        return result;
    }
}
