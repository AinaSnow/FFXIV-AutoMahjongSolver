using System.Buffers.Binary;
using System.Text.Json;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Game.Tests;

public sealed class CarriedRiichiPoolReplayTests
{
    [Theory]
    [InlineData(1, 2)] [InlineData(2, 1)] [InlineData(3, 1)] [InlineData(4, 1)]
    public void Pool_matches_previous_reach_ledger_and_full_next_hand_remains_consistent(int index, int expected)
    {
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "RegressionFixtures", $"20260925-carried-sticks-{index}.json")));
        var previous = new MahjongPacketMjaiDecoder();
        var ledger = data.RootElement.GetProperty("previous_hand_evidence").EnumerateArray().ToArray();
        var before = Assert.Single(Decode(previous, ledger[0]).OfType<MjaiStartKyoku>());
        Assert.Equal(643, ledger[^1].GetProperty("message_id").GetInt32()); // Observed exhaustive draw boundary.
        var reachActors = ledger.Skip(1).SkipLast(1).SelectMany(row => Decode(previous, row))
            .OfType<MjaiReachAccepted>().Select(e => e.Actor).ToArray();
        Assert.Equal(reachActors.Length, reachActors.Distinct().Count());
        Assert.Equal(expected, before.Kyotaku + reachActors.Length);

        var decoder = new MahjongPacketMjaiDecoder(); var reducer = new PublicStateReducer();
        MjaiStartKyoku? start = null;
        long lastSequence = -1; DateTimeOffset lastTime = DateTimeOffset.MinValue;
        bool ended = false;
        foreach (var row in data.RootElement.GetProperty("packets").EnumerateArray())
        {
            long sequence = row.GetProperty("sequence").GetInt64();
            var time = DateTimeOffset.Parse(row.GetProperty("t").GetString()!);
            Assert.True(sequence > lastSequence && time >= lastTime);
            lastSequence = sequence; lastTime = time;
            foreach (var evt in Decode(decoder, row))
            {
                if (evt is MjaiStartKyoku opening)
                {
                    start = opening;
                    Assert.Null(DomanMortalTrialGuard.RejectStart(opening));
                    Assert.Equal(expected, opening.Kyotaku);
                    Assert.Equal(100000, opening.Scores.Sum() + 1000 * expected);
                    Assert.All(opening.Tehais.Skip(1).SelectMany(h => h), tile => Assert.Equal("?", tile));
                }
                if (evt is MjaiTsumo { Actor: > 0 } draw) Assert.Equal("?", draw.Pai);
                reducer.Apply(evt, knownCounters: false);
                if (evt is not MjaiStartGame) Assert.True(reducer.Complete, reducer.Failure);
                if (evt is MjaiEndKyoku) ended = true;
            }
        }
        Assert.NotNull(start); Assert.True(ended); Assert.False(reducer.Active);
    }

    [Theory]
    [InlineData(251)] [InlineData(249)] [InlineData(int.MaxValue)]
    public void Invalid_score_payload_is_rejected_instead_of_sending_zero_sticks(int score)
    {
        var payload = new byte[104];
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(32 + 4 * i), i == 0 ? score : 250);
        Assert.Throws<InvalidDataException>(() => new MahjongPacketMjaiDecoder().Process(637, payload));
    }

    private static IReadOnlyList<IMjaiEvent> Decode(MahjongPacketMjaiDecoder decoder, JsonElement row) =>
        decoder.Process(row.GetProperty("message_id").GetInt32(), Convert.FromHexString(row.GetProperty("payload_hex").GetString()!));
}
