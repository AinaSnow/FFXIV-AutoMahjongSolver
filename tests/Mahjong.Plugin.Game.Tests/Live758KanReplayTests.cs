using System.Buffers.Binary;
using System.Text.Json;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Game.Tests;

public sealed class Live758KanReplayTests
{
    // Sanitized 7.58 hands; see docs/reviews/20261116-758-kan-and-0x211.md.
    [Theory]
    [InlineData(0, "ankan", 0, 640, 1)]
    [InlineData(1, "kakan", 0, 643, 0)]
    [InlineData(2, "daiminkan", 2, 643, 0)]
    public void Kan_hand_decodes_to_result_and_matches_ui_and_next_opening(
        int index, string kanType, int kanActor, int resultMessageId, int expectedRiichi)
    {
        string fixtures = Path.Combine(AppContext.BaseDirectory, "RegressionFixtures");
        var profile = JsonSerializer.Deserialize<MahjongProtocolProfile>(
            File.ReadAllText(Path.Combine(fixtures, "758-candidate-profile.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.False(profile.Verified);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "20261116-758-kan-hands.json")));
        string version = document.RootElement.GetProperty("game_version").GetString()!;
        Assert.False(profile.Matches(version, "Emj"));
        Assert.True(profile.MatchesLimitedTrial(version, "Emj"));
        var data = document.RootElement.GetProperty("hands")[index];
        var checkpoints = data.GetProperty("ui_checkpoints").EnumerateArray()
            .ToDictionary(c => c.GetProperty("after_sequence").GetInt64());

        var decoder = new MahjongPacketMjaiDecoder();
        var state = new PublicStateReducer();
        var events = new List<IMjaiEvent>();
        int[]? resultScores = null;
        MjaiStartKyoku? next = null;
        int selfSeat = -1, starts = 0, checkedPoints = 0;
        foreach (var packet in data.GetProperty("packets").EnumerateArray())
        {
            ushort opcode = Convert.ToUInt16(packet.GetProperty("opcode").GetString()![2..], 16);
            Assert.True(profile.TryGet(opcode, out var spec));
            Assert.Equal(spec!.MessageId, packet.GetProperty("message_id").GetInt32());
            byte[] payload = Convert.FromHexString(packet.GetProperty("payload_hex").GetString()!);
            Assert.Equal(spec.PayloadLength, payload.Length);
            Assert.Null(next); // The next opening is the last row.
            foreach (var evt in decoder.Process(spec.MessageId, payload))
            {
                if (evt is MjaiStartKyoku start && starts++ > 0)
                {
                    next = start;
                    continue;
                }
                events.Add(evt);
                state.Apply(evt, knownCounters: false);
                if (evt is not MjaiStartGame) Assert.True(state.Complete, state.Failure);
            }
            if (spec.MessageId == MahjongPacketMjaiDecoder.HandStartMessageId && starts == 1)
                selfSeat = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(24));
            if (spec.MessageId is 639 or 640 or 643)
            {
                Assert.Equal(resultMessageId, spec.MessageId);
                Assert.IsType<MjaiEndKyoku>(events[^1]);
                int offset = spec.MessageId == 643 ? 8 : 36, stride = spec.MessageId == 643 ? 64 : 4;
                resultScores = Enumerable.Range(0, 4).Select(actor => BinaryPrimitives.ReadInt32LittleEndian(
                    payload.AsSpan(offset + (selfSeat + actor) % 4 * stride)) * 100).ToArray();
            }
            if (checkpoints.TryGetValue(packet.GetProperty("sequence").GetInt64(), out var ui))
            {
                var snapshot = state.Snapshot(LegalActions.None);
                if (ui.TryGetProperty("hand", out var hand))
                    Assert.Equal(hand.EnumerateArray().Select(t => (byte)t.GetInt32()).Order(), snapshot.Hand.Select(t => t.Id).Order());
                if (ui.TryGetProperty("riichi", out var riichi))
                    Assert.Equal(riichi.GetBoolean(), snapshot.OurRiichi);
                if (ui.TryGetProperty("dora", out var dora))
                    Assert.Equal(dora.EnumerateArray().Select(t => (byte)t.GetInt32()), snapshot.DoraIndicators.Select(t => t.Id));
                checkedPoints++;
            }
        }

        Assert.Equal(checkpoints.Count, checkedPoints);
        int[] uiScores = data.GetProperty("ui_scores_after").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Assert.Equal(uiScores, resultScores);
        Assert.NotNull(next);
        Assert.Equal(uiScores, next!.Scores);
        Assert.False(state.Active);
        Assert.Single(events.OfType<MjaiEndKyoku>());

        var kan = Assert.Single(events, e => e is MjaiAnkan or MjaiKakan or MjaiDaiminkan);
        int kanIndex = events.IndexOf(kan);
        Assert.Equal(kanType, kan switch { MjaiAnkan => "ankan", MjaiKakan => "kakan", _ => "daiminkan" });
        Assert.Equal(kanActor, kan switch { MjaiAnkan a => a.Actor, MjaiKakan k => k.Actor, MjaiDaiminkan d => d.Actor, _ => -1 });
        Assert.IsType<MjaiDora>(events[kanIndex + 1]);
        Assert.Equal(kanActor, Assert.IsType<MjaiTsumo>(events[kanIndex + 2]).Actor);
        Assert.Single(events.OfType<MjaiDora>());
        Assert.Equal(expectedRiichi, events.OfType<MjaiReachAccepted>().Count(e => e.Actor == 0));
        Assert.All(events.OfType<MjaiTsumo>().Where(e => e.Actor != 0), e => Assert.Equal("?", e.Pai));
        if (expectedRiichi > 0)
        {
            // 0x211 directly follows the replacement draw: reach, hand discard, accepted.
            Assert.Equal(new IMjaiEvent[] { new MjaiReach(0), new MjaiDahai(0, "2m", false), new MjaiReachAccepted(0) },
                events.Skip(kanIndex + 3).Take(3));
            int[] deltas = data.GetProperty("ui_deltas").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Assert.Equal(-1000, deltas[0]); // Only the riichi stick; self neither won nor dealt in.
        }
    }
}
