namespace Mahjong.Core.Tests;

public class DomanLegalityTests
{
    [Theory]
    [InlineData("123m", "1m", "14m")]
    [InlineData("234m", "4m", "14m")]
    [InlineData("123p", "2p", "2p")]
    [InlineData("123s", "3s", "3s")]
    [InlineData("789s", "7s", "7s")]
    public void Chi_forbids_both_ends_without_crossing_suit_boundaries(string sequence, string claimed, string forbidden)
    {
        var tiles = Tiles.Parse(sequence); var tile = Tiles.Parse(claimed)[0];
        var meld = new Meld(MeldKind.Chi, tiles.ToArray(), tile, 3);
        Assert.Equal(Tiles.Parse(forbidden), Kuikae.ForbiddenDiscards(meld));
    }

    [Fact]
    public void Pon_forbids_same_kind_kan_does_not()
    {
        var tile = Tile.FromId(4);
        Assert.Equal(new[] { tile }, Kuikae.ForbiddenDiscards(Meld.Pon(tile, tile, 1)));
        Assert.Empty(Kuikae.ForbiddenDiscards(Meld.MinKan(tile, tile, 1)));
        Assert.Empty(Kuikae.ForbiddenDiscards(Meld.AnKan(tile)));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Ties_follow_initial_east_order_for_all_relative_seats(int initialEast)
    {
        for (int wind = 0; wind < 4; wind++)
            Assert.Equal(wind + 1, SeatRanking.Rank([25000,25000,25000,25000], (initialEast + wind) % 4, initialEast));
    }

    [Fact]
    public void Unknown_initial_order_does_not_claim_a_tied_rank()
    {
        Assert.Null(SeatRanking.Rank([25000,25000,25000,25000], 0));
        Assert.Equal(1, SeatRanking.Rank([30000,25000,25000,20000], 0));
    }

    [Fact]
    public void Legacy_empty_discards_stay_unknown_but_known_empty_is_not_unrestricted()
    {
        var old = new LegalActions(ActionFlags.Discard, [], [], [], []);
        Assert.True(old.AllowsDiscard(Tile.FromId(0)));
        Assert.False((old with { DiscardRestrictionKnown = true }).AllowsDiscard(Tile.FromId(0)));
    }
}
