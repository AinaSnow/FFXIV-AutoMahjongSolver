using System.Buffers.Binary;

namespace Mahjong.Plugin.Game.Mjai;

/// <summary>Deliberately narrow trial admission; observed opponent kans, self closed kans and result-checked dora; opening sticks use score conservation.</summary>
public static class DomanMortalTrialGuard
{
    public const string Scope = "doman-east-self-ankan-v5";

    public static string? RejectStart(MjaiStartKyoku start)
    {
        if (start.Bakaze != "E" || start.Kyoku is < 1 or > 4 || start.Honba < 0)
            return "Limited trial: round outside the captured East-round scope";
        if (InferOpeningRiichiSticks(start.Scores) is not { } sticks || start.Kyotaku != sticks)
            return "Limited trial: riichi pool conflicts with opening score conservation";
        return null;
    }

    /// <summary>
    /// Doman starts with 4 * 25000; exchanges and noten payments conserve points.
    /// Only deposited riichi sticks leave player totals. This derives a counter
    /// from confirmed scores, not an unverified packet offset. See the four
    /// independent reach -> draw-result -> opening traces dated 2026-09-25.
    /// </summary>
    public static int? InferOpeningRiichiSticks(IReadOnlyList<int> scores)
    {
        if (scores.Count != 4 || scores.Any(s => s is < -200000 or > 200000 || s % 100 != 0))
            return null;
        long total = scores.Sum(s => (long)s);
        long pool = 100000 - total;
        return total >= 0 && pool >= 0 && pool % 1000 == 0 ? (int)(pool / 1000) : null;
    }

    public static string? RejectPacket(int messageId, ReadOnlySpan<byte> payload)
    {
        if (messageId == MahjongPacketMjaiDecoder.HandStartMessageId && payload.Length >= 28)
        {
            int round = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(8, 4));
            int seat = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(24, 4));
            if (round is < 0 or > 3 || seat is < 0 or > 3)
                return "Limited trial: invalid or unsupported opening context";
        }
        if (messageId is MahjongPacketMjaiDecoder.DrawOrCallMessageId or MahjongPacketMjaiDecoder.DiscardMessageId
            && payload.Length >= 4 && BinaryPrimitives.ReadInt32LittleEndian(payload[..4]) is < 0 or > 3)
            return "Limited trial: unknown event actor";
        // The captured self declaration + drawn-tile click independently verifies
        // 0x113. The decoder additionally requires the actor's preceding draw and
        // checks exact self tile/red identity. Opponent kan/dora sequences are
        // validated by the decoder; self open/added kan remains outside scope.
        // Evidence: docs/reviews/20260925-combined-riichi-protocol.md.
        return null;
    }
}
