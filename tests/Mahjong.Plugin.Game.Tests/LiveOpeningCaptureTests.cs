using System.Text.Json;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Game.Tests;

public sealed class LiveOpeningCaptureTests
{
    [Fact]
    public void Candidate_opening_replays_all_observed_self_hands_without_enabling_live_protocol()
    {
        string fixtures=Path.Combine(AppContext.BaseDirectory,"RegressionFixtures");
        var profile=JsonSerializer.Deserialize<MahjongProtocolProfile>(File.ReadAllText(Path.Combine(fixtures,"candidate-profile.json")),
            new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
        Assert.False(profile.Verified);
        Assert.False(profile.Matches("2026.09.15.0000.0000","Emj"));
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures,"20260924-opening.json")));
        var packets=doc.RootElement.GetProperty("packets");
        Assert.Equal(37,packets.GetArrayLength());
        var decoder=new MahjongPacketMjaiDecoder();
        var events=new List<IMjaiEvent>();
        var hand=new List<string>();
        int compared=0;
        foreach(var packet in packets.EnumerateArray())
        {
            Assert.True(profile.TryGet(Convert.ToUInt16(packet.GetProperty("opcode").GetString()![2..],16),out var spec));
            var payload=Convert.FromHexString(packet.GetProperty("payload_hex").GetString()!);
            Assert.Equal(spec!.PayloadLength,payload.Length);
            Assert.Equal(packet.GetProperty("message_id").GetInt32(),spec.MessageId);
            foreach(var evt in decoder.Process(spec.MessageId,payload))
            {
                events.Add(evt);
                switch(evt)
                {
                    case MjaiStartKyoku start:
                        hand.AddRange(start.Tehais[0]);
                        Assert.Equal(new[]{25000,25000,25000,25000},start.Scores);
                        Assert.All(start.Tehais.Skip(1).SelectMany(x=>x),tile=>Assert.Equal("?",tile));
                        break;
                    case MjaiTsumo draw when draw.Actor==0: hand.Add(draw.Pai);break;
                    case MjaiDahai discard when discard.Actor==0: Assert.True(hand.Remove(discard.Pai));break;
                }
            }
            if(packet.TryGetProperty("ui_hand",out var uiHand))
            {
                string[] expected=uiHand.EnumerateArray().Select((tile,i)=>MjaiTile.Format(Tile.FromId(tile.GetInt32()),
                    packet.GetProperty("ui_red")[i].GetBoolean())).Order().ToArray();
                Assert.Equal(expected,hand.Order().ToArray());
                var delay=DateTimeOffset.Parse(packet.GetProperty("ui_t").GetString()!)-DateTimeOffset.Parse(packet.GetProperty("t").GetString()!);
                Assert.InRange(delay,TimeSpan.Zero,TimeSpan.FromSeconds(1));
                compared++;
            }
        }
        Assert.Equal(11,compared);
        Assert.Equal(37,events.Count);
        Assert.Single(events.OfType<MjaiStartGame>());
        Assert.Single(events.OfType<MjaiStartKyoku>());
        Assert.Equal(18,events.OfType<MjaiTsumo>().Count());
        Assert.Equal(17,events.OfType<MjaiDahai>().Count());
        Assert.All(events.OfType<MjaiTsumo>().Where(e=>e.Actor!=0),e=>Assert.Equal("?",e.Pai));
        Assert.DoesNotContain(events,e=>e is MjaiEndKyoku or MjaiEndGame);
        Assert.DoesNotContain("?",hand);
    }
}
