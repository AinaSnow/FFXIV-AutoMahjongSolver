using System.Buffers.Binary;
using System.Text.Json;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Game.Tests;

/// <summary>
/// Hand-result labels from sanitized 7.58 hands (docs/reviews/20261116-c1-live-data-loop.md).
/// tools/tests/test_live_hand_labels.py checks the Python labeller against the same fixture.
/// </summary>
public sealed class Live758HandResultTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "RegressionFixtures");

    public static TheoryData<string> Hands()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "20261116-758-hand-results.json")));
        var data = new TheoryData<string>();
        foreach (var hand in document.RootElement.GetProperty("hands").EnumerateArray())
            data.Add(hand.GetProperty("label").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(Hands))]
    public void Result_packet_label_matches_cross_checked_expectation_ui_and_next_opening(string label)
    {
        var hand = LoadHand(label);
        var (result, next, endEvents) = Replay(hand, mutate: null);

        Assert.Equal(1, endEvents);
        var expected = hand.GetProperty("expected");
        Assert.NotNull(result);
        Assert.Null(result!.UnknownReason);
        Assert.Equal(expected.GetProperty("outcome").GetString(), result.Outcome);
        Assert.Equal(Ints(expected.GetProperty("winners")), result.Winners);
        Assert.Equal(expected.GetProperty("discarder").ValueKind == JsonValueKind.Null ? null : expected.GetProperty("discarder").GetInt32(),
            result.Discarder);
        Assert.Equal(Ints(expected.GetProperty("deltas")), result.Deltas);
        if (expected.GetProperty("tenpai").ValueKind != JsonValueKind.Null)
            Assert.Equal(expected.GetProperty("tenpai").EnumerateArray().Select(t => t.GetBoolean()), result.Tenpai!);
        var details = expected.GetProperty("winner_details").EnumerateArray().ToArray();
        Assert.Equal(details.Length, result.WinnerDetails.Count);
        for (int i = 0; i < details.Length; i++)
        {
            Assert.Equal(details[i].GetProperty("seat").GetInt32(), result.WinnerDetails[i].Seat);
            Assert.Equal(details[i].GetProperty("han").GetInt32(), result.WinnerDetails[i].Han);
            Assert.Equal(details[i].GetProperty("fu").GetInt32(), result.WinnerDetails[i].Fu);
            Assert.Equal(details[i].GetProperty("value").GetInt32(), result.WinnerDetails[i].Value);
        }

        // Independent evidence: the UI score record of the same hand and the next hand's opening scores.
        if (hand.GetProperty("ui_hand_end").ValueKind == JsonValueKind.Object)
        {
            Assert.Equal(Ints(hand.GetProperty("ui_hand_end").GetProperty("deltas")), result.Deltas);
            Assert.Equal(Ints(hand.GetProperty("ui_hand_end").GetProperty("scores_after")), result.ScoresAfter);
        }
        if (next is not null)
            Assert.Equal(next.Scores, result.ScoresAfter);
    }

    [Fact]
    public void Double_ron_reports_both_winners_and_the_second_block_cumulative_scores()
    {
        var (result, _, _) = Replay(LoadHand("double-ron"), mutate: null);
        Assert.Equal("ron", result!.Outcome);
        Assert.Equal(2, result.Winners.Count);
        Assert.Equal(0, result.Discarder);
        Assert.Equal(0, result.Deltas!.Sum()); // No riichi deposit was on the table.
    }

    [Theory]
    [InlineData("self-ron", 20, 1)]          // hand value: payment arithmetic disagrees with the scores
    [InlineData("self-ron", 12, 0x050000)]   // packed honba: disagrees with the hand opening
    [InlineData("self-tsumo", 28, 0)]        // non-dealer tsumo payment zeroed: not a tsumo shape
    [InlineData("exhaustive-draw-riichi-tenpai", 140, 0)] // a tenpai seat's revealed count zeroed
    [InlineData("self-ron", 0, 0x7FFF)]      // undecodable winning tile
    [InlineData("double-ron", 268, 0)]       // second block's winner set to an existing winner/seat 0
    public void Inconsistent_result_packet_degrades_to_unknown_without_failing_the_hand(string label, int offset, int value)
    {
        var (result, next, endEvents) = Replay(LoadHand(label), mutate: (message, payload) =>
        {
            if (message is 639 or 640 or 643)
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset), value);
        });
        Assert.Equal(1, endEvents); // The hand still ends normally; nothing is quarantined.
        Assert.Equal("unknown", result!.Outcome);
        Assert.NotNull(result.UnknownReason);
        Assert.Empty(result.Winners);
        Assert.Null(result.Discarder);
        _ = next;
    }

    [Fact]
    public void End_kyoku_event_and_its_json_are_unchanged_by_the_result_side_channel()
    {
        Assert.Equal("""{"type":"end_kyoku"}""", JsonSerializer.Serialize(new MjaiEndKyoku()));
    }

    private static JsonElement LoadHand(string label)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "20261116-758-hand-results.json")));
        return document.RootElement.GetProperty("hands").EnumerateArray()
            .Single(h => h.GetProperty("label").GetString() == label).Clone();
    }

    private static (ServerHandResult? Result, MjaiStartKyoku? Next, int EndEvents) Replay(
        JsonElement hand, Action<int, byte[]>? mutate)
    {
        var profile = JsonSerializer.Deserialize<MahjongProtocolProfile>(
            File.ReadAllText(Path.Combine(Fixtures, "758-candidate-profile.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var decoder = new MahjongPacketMjaiDecoder();
        var state = new PublicStateReducer();
        ServerHandResult? result = null;
        MjaiStartKyoku? next = null;
        int starts = 0, ends = 0;
        foreach (var packet in hand.GetProperty("packets").EnumerateArray())
        {
            ushort opcode = Convert.ToUInt16(packet.GetProperty("opcode").GetString()![2..], 16);
            Assert.True(profile.TryGet(opcode, out var spec));
            byte[] payload = Convert.FromHexString(packet.GetProperty("payload_hex").GetString()!);
            Assert.Equal(spec!.PayloadLength, payload.Length);
            mutate?.Invoke(spec.MessageId, payload);
            foreach (var evt in decoder.Process(spec.MessageId, payload))
            {
                if (evt is MjaiStartKyoku start && starts++ > 0)
                {
                    next = start;
                    continue;
                }
                if (next is not null) continue;
                state.Apply(evt, knownCounters: false);
                if (evt is not MjaiStartGame) Assert.True(state.Complete, state.Failure);
                if (evt is MjaiEndKyoku)
                {
                    ends++;
                    result = decoder.LastHandResult;
                }
            }
        }
        return (result, next, ends);
    }

    private static int[] Ints(JsonElement array) => array.EnumerateArray().Select(x => x.GetInt32()).ToArray();
}
