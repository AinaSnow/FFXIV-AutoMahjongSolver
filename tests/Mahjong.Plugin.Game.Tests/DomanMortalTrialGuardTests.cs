using System.Buffers.Binary;
using Mahjong.Plugin.Game.Mjai;
namespace Mahjong.Plugin.Game.Tests;
public sealed class DomanMortalTrialGuardTests
{
    private static MjaiStartKyoku Start => new("E", "1m", 1, 0, 0, 0, [25000,25000,25000,25000], []);
    [Fact]
    public void Only_score_conserving_pool_east_rounds_are_admitted()
    {
        Assert.Null(DomanMortalTrialGuard.RejectStart(Start));
        Assert.Null(DomanMortalTrialGuard.RejectStart(Start with { Scores = [-800,37000,39400,24400], Honba = 3 }));
        Assert.NotNull(DomanMortalTrialGuard.RejectStart(Start with { Scores = [24000,25000,25000,25000] }));
        Assert.NotNull(DomanMortalTrialGuard.RejectStart(Start with { Kyotaku = 1 }));
        Assert.Null(DomanMortalTrialGuard.RejectStart(Start with { Scores = [24000,25000,25000,25000], Kyotaku = 1 }));
        Assert.Null(DomanMortalTrialGuard.RejectStart(Start with { Scores = [24000,24000,25000,25000], Kyotaku = 2 }));
        Assert.NotNull(DomanMortalTrialGuard.RejectStart(Start with { Bakaze = "S" }));
    }
    [Theory]
    [InlineData(25100)] // Sum exceeds the fixed table total.
    [InlineData(24900)] // Pool is not a whole 1000-point stick.
    [InlineData(int.MaxValue)] // No arithmetic wraparound into a plausible total.
    [InlineData(-200100)]
    public void Invalid_totals_do_not_create_a_guessed_counter(int score)
    {
        Assert.Null(DomanMortalTrialGuard.InferOpeningRiichiSticks([score, 25000, 25000, 25000]));
    }

    [Theory]
    [InlineData(-1)] [InlineData(4)] [InlineData(16)]
    public void Invalid_round_cannot_be_normalized_into_an_admitted_east_hand(int index)
    {
        var bytes = new byte[104]; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), index);
        Assert.NotNull(DomanMortalTrialGuard.RejectPacket(637, bytes));
    }
}
