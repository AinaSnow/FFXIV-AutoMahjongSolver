using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mahjong.Policy.Efficiency;
using Mahjong.Rules.Rulesets;

namespace Mahjong.Plugin.Dalamud.Logging;

public sealed record TrainingProvenance(
    string PolicyWeightsSha256, string PolicyBuildId, string RulesBuildId, string RuleSet,
    string Strategy, bool MortalEnabled, bool MortalLimitedTrial, int SearchBudgetMs,
    string CalibrationIdentity, JsonElement? MortalModel);

/// <summary>Immutable, cheap snapshots: no checkpoint/file reads on the game thread.</summary>
public sealed class TrainingProvenanceProvider(IWeightProvider weights, Func<Configuration> configuration, Func<string?> model)
{
    public string WeightsJson { get; } = JsonSerializer.Serialize(weights.Current);
    private readonly string policyBuild = typeof(EfficiencyPolicy).Module.ModuleVersionId.ToString();
    private readonly string rulesBuild = typeof(DomanRuleSet).Module.ModuleVersionId.ToString();
    private string? weightsHash;
    public TrainingProvenance Snapshot()
    {
        var cfg = configuration();
        JsonElement? identity = null;
        if (model() is { } json) { using var doc = JsonDocument.Parse(json); identity = doc.RootElement.Clone(); }
        return new(weightsHash ??= Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(WeightsJson))),
            policyBuild, rulesBuild, nameof(DomanRuleSet),
            cfg.EnhancedStrategy ? (cfg.EnhancedShadowOnly ? "enhanced-shadow" : "enhanced") : "stable",
            cfg.MortalEnabled, cfg.MortalLimitedTrial, cfg.SearchBudgetMs,
            cfg.EnhancedStrategy && !string.IsNullOrWhiteSpace(cfg.CalibrationPath) ? "unknown-external-calibration" : "not-used",
            identity);
    }
}

public sealed record TrainingRawCapture(string Path, Task Completion);

public sealed record TrainingArchiveContext(string? RawPath, Task RawCompletion, TrainingProvenance Provenance, string WeightsJson,
    IReadOnlyList<TrainingRawCapture>? AdditionalRawCaptures = null)
{
    public IReadOnlyList<TrainingRawCapture> Captures =>
        [.. RawPath is { } path ? new[] { new TrainingRawCapture(path, RawCompletion) } : [], .. AdditionalRawCaptures ?? []];
}
