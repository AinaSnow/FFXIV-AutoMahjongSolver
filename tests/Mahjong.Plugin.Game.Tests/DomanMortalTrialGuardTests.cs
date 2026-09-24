using System.Buffers.Binary;
using Mahjong.Plugin.Game.Mjai;
namespace Mahjong.Plugin.Game.Tests;
public sealed class DomanMortalTrialGuardTests
{
    private static MjaiStartKyoku Start => new("E", "1m", 1, 0, 0, 0, [25000,25000,25000,25000], []);
    [Fact]
    public void Only_zero_pool_east_rounds_are_admitted()
    {
        Assert.Null(DomanMortalTrialGuard.RejectStart(Start));
        Assert.Null(DomanMortalTrialGuard.RejectStart(Start with { Scores = [-800,37000,39400,24400], Honba = 3 }));
        Assert.NotNull(DomanMortalTrialGuard.RejectStart(Start with { Scores = [24000,25000,25000,25000] }));
        Assert.NotNull(DomanMortalTrialGuard.RejectStart(Start with { Kyotaku = 1 }));
        Assert.NotNull(DomanMortalTrialGuard.RejectStart(Start with { Bakaze = "S" }));
    }
    [Theory]
    [InlineData(-1)] [InlineData(4)] [InlineData(16)]
    public void Invalid_round_cannot_be_normalized_into_an_admitted_east_hand(int index)
    {
        var bytes = new byte[104]; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), index);
        Assert.NotNull(DomanMortalTrialGuard.RejectPacket(637, bytes));
    }
}
