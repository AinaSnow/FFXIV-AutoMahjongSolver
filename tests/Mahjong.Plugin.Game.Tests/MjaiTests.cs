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

    [Theory]
    [InlineData("1m", 0, false)]
    [InlineData("5pr", 13, true)]
    [InlineData("C", 33, false)]
    public void Tile_parse_accepts_mortal_notation(string text, int expectedId, bool expectedRed)
    {
        Assert.True(MjaiTile.TryParse(text, out var tile, out bool isRed));
        Assert.Equal(expectedId, tile.Id);
        Assert.Equal(expectedRed, isRed);
    }

    [Theory]
    [InlineData("?")]
    [InlineData("5zr")]
    [InlineData("4mr")]
    [InlineData("")]
    public void Tile_parse_rejects_unknown_or_invalid_notation(string text)
    {
        Assert.False(MjaiTile.TryParse(text, out _, out _));
    }

    [Fact]
    public void Mortal_reaction_parser_reads_action_fields_and_evaluation_time()
    {
        const string json = "{\"type\":\"chi\",\"actor\":0,\"target\":3,\"pai\":\"4s\",\"consumed\":[\"5sr\",\"6s\"],\"meta\":{\"eval_time_ns\":123}}";

        Assert.True(MortalReaction.TryParse(json, out var reaction));
        Assert.Equal("chi", reaction!.Type);
        Assert.Equal(0, reaction.Actor);
        Assert.Equal(3, reaction.Target);
        Assert.Equal("4s", reaction.Pai);
        Assert.Equal(["5sr", "6s"], reaction.Consumed);
        Assert.Equal(123, reaction.EvalTimeNs);
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

    [Fact]
    public void Journal_replaces_only_the_latest_provisional_event()
    {
        var journal = new MjaiEventJournal();
        var provisional = new MjaiDahai(1, "5p", false);
        journal.Append(new MjaiStartGame());
        journal.Append(provisional);

        Assert.True(journal.TryReplaceLast(
            provisional,
            [new MjaiReach(1), new MjaiDahai(1, "1s", false), new MjaiReachAccepted(1)]));
        Assert.Equal(
            "{\"type\":\"start_game\"}\n" +
            "{\"type\":\"reach\",\"actor\":1}\n" +
            "{\"type\":\"dahai\",\"actor\":1,\"pai\":\"1s\",\"tsumogiri\":false}\n" +
            "{\"type\":\"reach_accepted\",\"actor\":1}\n",
            journal.ToReplayText());

        Assert.False(journal.TryReplaceLast(provisional, [new MjaiDahai(1, "2s", false)]));
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

    [Fact]
    public void Journal_serializes_mortal_lifecycle_events()
    {
        var journal = new MjaiEventJournal();
        journal.Append(new MjaiEndKyoku());
        journal.Append(new MjaiEndGame());

        Assert.Equal(
            "{\"type\":\"end_kyoku\"}\n" +
            "{\"type\":\"end_game\"}\n",
            journal.ToReplayText());
    }

    [Fact]
    public void Journal_serializes_kan_events_for_mortal_state_confirmation()
    {
        var journal = new MjaiEventJournal();
        journal.Append(new MjaiAnkan(0, ["5sr", "5s", "5s", "5s"]));
        journal.Append(new MjaiKakan(0, "E", ["E", "E", "E"]));
        journal.Append(new MjaiDaiminkan(0, 3, "C", ["C", "C", "C"]));

        Assert.Equal(
            "{\"type\":\"ankan\",\"actor\":0,\"consumed\":[\"5sr\",\"5s\",\"5s\",\"5s\"]}\n" +
            "{\"type\":\"kakan\",\"actor\":0,\"pai\":\"E\",\"consumed\":[\"E\",\"E\",\"E\"]}\n" +
            "{\"type\":\"daiminkan\",\"actor\":0,\"target\":3,\"pai\":\"C\",\"consumed\":[\"C\",\"C\",\"C\"]}\n",
            journal.ToReplayText());
    }
}
