using System.Buffers.Binary;
using System.Text.Json;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Game.Tests;

public sealed class LiveLaterHandsCaptureTests
{
    [Theory]
    [InlineData("20260924-later-hands.json", "EmjL", 72, 118, 57, 57, 9, 4, 483, 18, 2)]
    [InlineData("20260924-full-match.json", "Emj", 0, 129, 62, 62, 0, 5, 524, 7, 8)]
    [InlineData("20260924-added-kan-match.json", "Emj", 0, 97, 47, 45, 3, 5, 388, 2, 7)]
    public void Live_capture_matches_self_hands_and_reports_public_history_gaps(
        string fixtureName, string variant, int expectedPrefix, int expectedCompared,
        int expectedDraws, int expectedDiscards, int expectedGaps, int expectedHands,
        int expectedEvents, int expectedCalls, int expectedRiichi)
    {
        string fixtures = Path.Combine(AppContext.BaseDirectory, "RegressionFixtures");
        var profile = JsonSerializer.Deserialize<MahjongProtocolProfile>(
            File.ReadAllText(Path.Combine(fixtures, variant == "Emj" ? "candidate-profile.json" : "emjl-candidate-profile.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.False(profile.Verified);
        Assert.False(profile.Matches("2026.09.15.0000.0000", variant));
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, fixtureName)));
        var decoder = new MahjongPacketMjaiDecoder();
        var publicState = new PublicStateReducer();
        var hand = new List<string>();
        var events = new List<IMjaiEvent>();
        int compared = 0, gaps = 0, prefix = 0, results = 0, selfSeat = -1;
        int selfDraws = 0, selfDiscards = 0;
        bool historyGap = false;
        foreach (var packet in document.RootElement.GetProperty("packets").EnumerateArray())
        {
            ushort opcode = Convert.ToUInt16(packet.GetProperty("opcode").GetString()![2..], 16);
            Assert.True(profile.TryGet(opcode, out var spec));
            byte[] payload = Convert.FromHexString(packet.GetProperty("payload_hex").GetString()!);
            Assert.Equal(spec!.PayloadLength, payload.Length);
            Assert.Equal(spec.MessageId, packet.GetProperty("message_id").GetInt32());
            if (packet.TryGetProperty("unsupported_action", out var unsupported))
            {
                var error = Assert.Throws<InvalidDataException>(() => decoder.Process(spec.MessageId, payload));
                Assert.Contains(unsupported.GetString()!, error.Message);
                publicState.Invalidate(error.Message);
                historyGap = true;
                Assert.False(publicState.Complete);
                gaps++;
                continue;
            }
            var decoded = decoder.Process(spec.MessageId, payload);
            if (selfSeat < 0 && spec.MessageId is not (MahjongPacketMjaiDecoder.HandStartMessageId or MahjongPacketMjaiDecoder.MatchStartMessageId))
            {
                Assert.Empty(decoded); // Mid-hand capture must not synthesize an opening.
                prefix++;
            }
            foreach (var evt in decoded)
            {
                events.Add(evt);
                publicState.Apply(evt, knownCounters: false);
                if (evt is MjaiStartKyoku) historyGap = false;
                if (evt is not MjaiStartGame)
                    Assert.Equal(!historyGap, publicState.Complete); // Only a real opening repairs a gap.
                switch (evt)
                {
                    case MjaiStartKyoku start:
                        selfSeat = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(24));
                        hand.Clear();
                        hand.AddRange(start.Tehais[0]);
                        var round = packet.GetProperty("expected_round");
                        Assert.Equal("E", start.Bakaze);
                        Assert.Equal(round.GetProperty("kyoku").GetInt32(), start.Kyoku);
                        Assert.Equal(round.GetProperty("honba").GetInt32(), start.Honba);
                        Assert.Equal(round.GetProperty("oya").GetInt32(), start.Oya);
                        Assert.Equal(Ints(packet.GetProperty("ui_scores")), start.Scores);
                        Assert.All(start.Tehais.Skip(1).SelectMany(x => x), tile => Assert.Equal("?", tile));
                        Assert.True(publicState.Complete); // Next observed opening clears quarantine.
                        if (variant == "Emj")
                        {
                            Assert.True(MjaiTile.TryParse(start.DoraMarker, out var marker, out _));
                            Assert.Equal(packet.GetProperty("ui_dora")[0].GetInt32(), marker.Id);
                        }
                        break;
                    case MjaiTsumo draw when draw.Actor == 0:
                        hand.Add(draw.Pai); selfDraws++; break;
                    case MjaiDahai discard when discard.Actor == 0:
                        Assert.True(hand.Remove(discard.Pai), $"Missing {discard.Pai} at packet {packet.GetProperty("sequence")}");
                        selfDiscards++; break;
                }
            }
            if (packet.TryGetProperty("ui_hand", out var uiHand))
            {
                string[] expected = uiHand.EnumerateArray().Select((tile, i) => MjaiTile.Format(
                    Tile.FromId(tile.GetInt32()), packet.GetProperty("ui_hand_red")[i].GetBoolean())).Order().ToArray();
                Assert.Equal(expected, hand.Order().ToArray());
                var delay = DateTimeOffset.Parse(packet.GetProperty("ui_t").GetString()!)
                    - DateTimeOffset.Parse(packet.GetProperty("t").GetString()!);
                Assert.InRange(delay, TimeSpan.Zero, TimeSpan.FromSeconds(2));
                compared++;
            }
            if (packet.TryGetProperty("ui_result", out var result))
            {
                Assert.Single(decoded.OfType<MjaiEndKyoku>());
                int[] scores = Enumerable.Range(0, 4).Select(actor =>
                    BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(36 + ((selfSeat + actor) % 4) * 4)) * 100).ToArray();
                Assert.Equal(Ints(result.GetProperty("scores_after")), scores);
                results++;
            }
        }
        Assert.Equal(expectedPrefix, prefix);
        Assert.Equal(expectedCompared, compared);
        Assert.Equal(expectedDraws, selfDraws);
        Assert.Equal(expectedDiscards, selfDiscards);
        Assert.Equal(expectedGaps, gaps);
        Assert.Equal(expectedHands, results);
        Assert.Equal(expectedEvents, events.Count);
        Assert.Single(events.OfType<MjaiStartGame>());
        Assert.Equal(expectedHands, events.OfType<MjaiStartKyoku>().Count());
        Assert.Equal(expectedHands, events.OfType<MjaiEndKyoku>().Count());
        Assert.Equal(expectedCalls, events.OfType<MjaiOpenCall>().Count());
        Assert.Equal(expectedRiichi, events.OfType<MjaiReach>().Count());
        Assert.Equal(expectedRiichi, events.OfType<MjaiReachAccepted>().Count());
        Assert.All(events.OfType<MjaiTsumo>().Where(e => e.Actor != 0), e => Assert.Equal("?", e.Pai));
        if (variant == "Emj")
        {
            string expectedRedDiscard = fixtureName == "20260924-full-match.json" ? "5mr" : "5sr";
            Assert.Contains(events.OfType<MjaiDahai>(), e => e.Actor == 0 && e.Pai == expectedRedDiscard);
            Assert.Equal(new[] { 0, 1, 2, 3 }, events.OfType<MjaiStartKyoku>().Select(e => e.Oya).Distinct().Order());
        }
        else
        {
            Assert.Contains(events, e => e is MjaiTsumo { Actor: 0, Pai: "5pr" });
            Assert.Contains(events, e => e is MjaiDahai { Actor: 0, Pai: "5pr" });
        }
        Assert.DoesNotContain(events, e => e is MjaiEndGame);
    }

    private static int[] Ints(JsonElement array) => array.EnumerateArray().Select(x => x.GetInt32()).ToArray();
}
