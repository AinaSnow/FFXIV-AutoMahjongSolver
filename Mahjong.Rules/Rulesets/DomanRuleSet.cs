using Mahjong.Rules.Scoring;

namespace Mahjong.Rules.Rulesets;

/// <summary>
/// FFXIV Doman uses the stock riichi yaku floor and includes one red five per suit.
/// </summary>
public sealed class DomanRuleSet : IRuleSet
{
    private readonly RiichiRuleSet riichi = new();

    public string Name => "Doman";

    public IReadOnlyList<IYakuRule> YakuRules => riichi.YakuRules;
    public IScoringRule ScoringRule => riichi.ScoringRule;
    public IDoraRule DoraRule => riichi.DoraRule;
    public IFuRule FuRule => riichi.FuRule;

    public bool AllowsRedDora => true;
    public bool AllowsKuitan => true;
    public int MinHan => 1;
    public int KazoeThreshold => ScoringConstants.KazoeYakumanHan;
    public int MaxYakuman => 2;
}
