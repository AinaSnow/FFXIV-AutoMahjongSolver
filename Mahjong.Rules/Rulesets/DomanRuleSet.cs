using Mahjong.Rules.Scoring;

namespace Mahjong.Rules.Rulesets;

/// <summary>FFXIV rules: single-valued yakuman, up to four combined; ranked tables allow kuitan.</summary>
public sealed class DomanRuleSet : IRuleSet
{
    private readonly RiichiRuleSet riichi = new();
    public DomanRuleSet(bool allowsKuitan = true)
    {
        AllowsKuitan = allowsKuitan;
        YakuRules = riichi.YakuRules.Select(rule => rule.Definition.IsYakuman
            ? (IYakuRule)new SingleYakumanRule(rule) : rule).ToArray();
    }

    public string Name => "Doman";
    public IReadOnlyList<IYakuRule> YakuRules { get; }
    public IScoringRule ScoringRule => riichi.ScoringRule;
    public IDoraRule DoraRule => riichi.DoraRule;
    public IFuRule FuRule => riichi.FuRule;
    public bool AllowsRedDora => true;
    public bool AllowsKuitan { get; }
    public int MinHan => 1;
    public int KazoeThreshold => ScoringConstants.KazoeYakumanHan;
    public int MaxYakuman => 4;

    // Keep the generic riichi rules intact; only the Doman adapter normalizes their value.
    private sealed class SingleYakumanRule(IYakuRule inner) : IYakuRule
    {
        public YakuDefinition Definition { get; } = inner.Definition with
        { ClosedHan = HanValues.Yakuman, OpenHan = HanValues.Yakuman };
        public IReadOnlyList<Yaku> Conflicts => inner.Conflicts;
        public IReadOnlyList<YakuHit> Detect(Decomposition d, WinContext ctx) =>
            inner.Detect(d, ctx).Select(hit => hit with { Han = HanValues.Yakuman }).ToArray();
    }
}
