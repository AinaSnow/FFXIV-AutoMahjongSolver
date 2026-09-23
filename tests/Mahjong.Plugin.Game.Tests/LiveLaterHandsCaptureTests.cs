using System.Buffers.Binary;
using System.Text.Json;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Game.Tests;

public sealed class LiveLaterHandsCaptureTests
{
    [Fact]
    public void Mid_hand_capture_matches_later_self_hands_and_reports_public_history_gaps()
    {
        string fixtures = Path.Combine(AppContext.BaseDirectory, "RegressionFixtures");
        var profile = JsonSerializer.Deserialize<MahjongProtocolProfile>(
            File.ReadAllText(Path.Combine(fixtures, "emjl-candidate-profile.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.False(profile.Verified);
        Assert.False(profile.Matches("2026.09.15.0000.0000", "EmjL"));
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "20260924-later-hands.json")));
        var decoder = new MahjongPacketMjaiDecoder();
        var publicState = new PublicStateReducer();
        var hand = new List<string>();
        var events = new List<IMjaiEvent>();
        int compared = 0, gaps = 0, prefix = 0, results = 0, selfSeat = -1;
        int selfDraws = 0, selfDiscards = 0;
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
                Assert.False(publicState.Complete);
                gaps++;
                continue;
            }
            var decoded = decoder.Process(spec.MessageId, payload);
            if (selfSeat < 0 && spec.MessageId != MahjongPacketMjaiDecoder.HandStartMessageId)
            {
                Assert.Empty(decoded); // Mid-hand capture must not synthesize an opening.
                prefix++;
            }
            foreach (var evt in decoded)
            {
                events.Add(evt);
                publicState.Apply(evt, knownCounters: false);
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
        Assert.Equal(72, prefix);
        Assert.Equal(118, compared);
        Assert.Equal(57, selfDraws);
        Assert.Equal(57, selfDiscards);
        Assert.Equal(9, gaps); // 2 open kans + 1 closed kan + 3 rinshan draws + 3 unverified discard flags.
        Assert.Equal(4, results);
        Assert.Equal(483, events.Count);
        Assert.Single(events.OfType<MjaiStartGame>());
        Assert.Equal(4, events.OfType<MjaiStartKyoku>().Count());
        Assert.Equal(4, events.OfType<MjaiEndKyoku>().Count());
        Assert.Equal(18, events.OfType<MjaiOpenCall>().Count());
        Assert.Equal(2, events.OfType<MjaiReach>().Count());
        Assert.Equal(2, events.OfType<MjaiReachAccepted>().Count());
        Assert.All(events.OfType<MjaiTsumo>().Where(e => e.Actor != 0), e => Assert.Equal("?", e.Pai));
        Assert.Contains(events, e => e is MjaiTsumo { Actor: 0, Pai: "5pr" });
        Assert.Contains(events, e => e is MjaiDahai { Actor: 0, Pai: "5pr" });
        Assert.DoesNotContain(events, e => e is MjaiEndGame);
    }

    private static int[] Ints(JsonElement array) => array.EnumerateArray().Select(x => x.GetInt32()).ToArray();
}
