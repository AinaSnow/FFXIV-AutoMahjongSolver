using Mahjong.Rules.Rulesets;

namespace Mahjong.Engine.Tests;

public class DomanScoringTests
{
    [Theory]
    [InlineData("11m111z222z333z444z", "1m", Yaku.Daisuushii)]
    [InlineData("11112345678999m", "1m", Yaku.ChuurenPoutou)]
    public void Special_waits_and_big_four_winds_are_single_yakuman_in_Doman(string hand, string win, Yaku yaku)
    {
        var ctx = new WinContext(Tiles.Parse(win)[0], WinKind.Tsumo);
        var doman = new Scorer(new DomanRuleSet()).Evaluate(Hand.FromNotation(hand), ctx)!;
        var riichi = new Scorer(new RiichiRuleSet()).Evaluate(Hand.FromNotation(hand), ctx)!;
        Assert.Equal(13, Assert.Single(doman.Yaku, hit => hit.Yaku == yaku).Han);
        Assert.Equal(26, Assert.Single(riichi.Yaku, hit => hit.Yaku == yaku).Han);
    }

    [Fact]
    public void Four_different_yakuman_stack_and_generic_limit_is_enforced()
    {
        var hand = Hand.FromNotation("11122233344455z");
        var ctx = new WinContext(Tiles.Parse("5z")[0], WinKind.Tsumo, IsTenhou: true, IsDealer: true);
        var doman = new Scorer(new DomanRuleSet()).Evaluate(hand, ctx)!;
        Assert.Equal(4, doman.Yaku.Count);
        Assert.Equal(52, doman.Han);
        Assert.Equal(192000, doman.Payments.Total);
        var generic = new Scorer(new RiichiRuleSet()).Evaluate(hand, ctx)!;
        Assert.Equal(26, generic.Han);
        Assert.Equal(96000, generic.Payments.Total);
    }

    [Fact]
    public void Kuitan_disabled_rejects_open_tanyao_even_with_dora()
    {
        var hand = Hand.FromNotation("234456p678s55m", [Meld.Pon(Tile.FromId(1), Tile.FromId(1), 1)]);
        var ctx = new WinContext(Tiles.Parse("5m")[0], WinKind.Ron, DoraIndicators: [Tile.FromId(3)]);
        Assert.NotNull(new Scorer(new DomanRuleSet()).Evaluate(hand, ctx));
        Assert.Null(new Scorer(new DomanRuleSet(allowsKuitan: false)).Evaluate(hand, ctx));
    }

    [Fact]
    public void Kuitan_disabled_still_allows_closed_tanyao()
    {
        var hand = Hand.FromNotation("234m345p678234s55m");
        var score = new Scorer(new DomanRuleSet(false)).Evaluate(hand,
            new WinContext(Tiles.Parse("5m")[0], WinKind.Ron));
        Assert.NotNull(score);
        Assert.Contains(score.Yaku, hit => hit.Yaku == Yaku.Tanyao);
    }
}
