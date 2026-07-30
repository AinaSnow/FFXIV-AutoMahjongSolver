using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Game.Tests;

public class MjaiTests
{
    [Theory]
    [InlineData(0, false, "1m")]
    [InlineData(4, true, "5mr")]
    [InlineData(13, true, "5pr")]
    [InlineData(22, true, "5sr")]
    [InlineData(27, false, "E")]
    [InlineData(31, false, "P")]
    [InlineData(33, false, "C")]
    public void Tile_format_matches_mjai(int tileId, bool isRed, string expected)
    {
        Assert.Equal(expected, MjaiTile.Format(Tile.FromId(tileId), isRed));
    }

    [Fact]
    public void FormatHand_requires_observed_red_identity()
    {
        Assert.Throws<InvalidOperationException>(() => MjaiTile.FormatHand(
            StateSnapshot.Empty with { Hand = [Tile.FromId(4)] }));
    }

    [Fact]
    public void Journal_serializes_and_replays_ndjson()
    {
        var journal = new MjaiEventJournal();
        journal.Append(new MjaiStartGame());
        journal.Append(new MjaiTsumo(Actor: 0, Pai: "5mr"));
        journal.Append(new MjaiDahai(Actor: 0, Pai: "1m", Tsumogiri: false));

        Assert.Equal(3, journal.Count);
        Assert.Equal(
            "{\"type\":\"start_game\"}\n" +
            "{\"type\":\"tsumo\",\"actor\":0,\"pai\":\"5mr\"}\n" +
            "{\"type\":\"dahai\",\"actor\":0,\"pai\":\"1m\",\"tsumogiri\":false}\n",
            journal.ToReplayText());
    }

    [Theory]
    [InlineData("ankan")]
    [InlineData("kakan")]
    [InlineData("invalid")]
    public void OpenCall_rejects_unsupported_types(string type)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MjaiOpenCall(type, 0, 1, "5m", ["5m", "5m"]));
    }
}
