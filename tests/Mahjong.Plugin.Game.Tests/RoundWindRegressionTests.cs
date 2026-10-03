using System.Buffers.Binary;
using Mahjong.Core;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Game.Tests;

public sealed class RoundWindRegressionTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void East_to_south_and_repeated_hands_keep_real_wind_and_relative_seat(int seat)
    {
        var decoder = new MahjongPacketMjaiDecoder();
        var reducer = new PublicStateReducer();
        // The observed half-game resets kyoku at South and repeats South 2 with honba.
        foreach (var (wind, kyoku, honba) in new[] { (0, 3, 0), (1, 0, 0), (1, 1, 0), (1, 1, 1), (1, 1, 2) })
        {
            var packet = Opening(wind, kyoku, honba, seat);
            var events = decoder.Process(637, packet);
            var start = Assert.Single(events.OfType<MjaiStartKyoku>());
            Assert.Equal(wind == 0 ? "E" : "S", start.Bakaze);
            Assert.Equal(kyoku + 1, start.Kyoku);
            Assert.Equal(honba, start.Honba);
            foreach (var e in events) reducer.Apply(e, knownCounters: false);
            var snapshot = reducer.Snapshot(LegalActions.None);
            Assert.Equal(wind, snapshot.RoundWind);
            Assert.Equal(seat, snapshot.SeatWind);
            Assert.Equal(0, snapshot.OurSeat);
            Assert.Equal((4 - seat) % 4, snapshot.DealerSeat);
            Assert.Equal(kyoku + 1, snapshot.Kyoku);
            Assert.True(snapshot.PublicStateConsistent);
            // Successful offline decoding must never admit South to the East-only live trial.
            Assert.Equal(wind == 0, DomanMortalTrialGuard.RejectPacket(637, packet) is null);
            Assert.Equal(wind == 0, DomanMortalTrialGuard.RejectStart(start) is null);
        }
    }

    [Theory]
    [InlineData(-1, 0)] [InlineData(2, 0)] [InlineData(0, -1)] [InlineData(1, 4)]
    public void Rejected_opening_does_not_start_or_close_a_hand(int wind, int kyoku)
    {
        var fresh = new MahjongPacketMjaiDecoder();
        var invalid = Opening(wind, kyoku, 0, 0);
        Assert.NotNull(DomanMortalTrialGuard.RejectPacket(637, invalid));
        Assert.Throws<InvalidDataException>(() => fresh.Process(637, invalid));
        Assert.Empty(fresh.Finish());
        fresh.Process(637, Opening(0, 0, 0, 0));
        Assert.Throws<InvalidDataException>(() => fresh.Process(637, invalid));
        var next = fresh.Process(637, Opening(0, 1, 0, 0));
        Assert.IsType<MjaiEndKyoku>(next[0]);
        Assert.Equal(2, Assert.IsType<MjaiStartKyoku>(next[1]).Kyoku);
    }

    private static byte[] Opening(int wind, int kyoku, int honba, int seat)
    {
        var bytes = new byte[104];
        void Write(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
        Write(4, wind); Write(8, kyoku); Write(12, honba); Write(24, seat); Write(28, 8);
        for (int i = 0; i < 4; i++) Write(32 + i * 4, 250);
        int[] hand = [0, 1, 2, 9, 10, 11, 18, 19, 20, 27, 27, 28, 28];
        for (int i = 0; i < hand.Length; i++) Write(48 + i * 4, hand[i]);
        return bytes;
    }
}
