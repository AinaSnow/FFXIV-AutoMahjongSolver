using System.Buffers.Binary;

namespace Mahjong.Plugin.Game.Mjai;

/// <summary>Deliberately narrow trial admission; no guessed kan, dora or nonzero kyotaku.</summary>
public static class DomanMortalTrialGuard
{
    public const string Scope = "doman-east-zero-stick-no-kan-v1";

    public static string? RejectStart(MjaiStartKyoku start)
    {
        if (start.Bakaze != "E" || start.Kyoku is < 1 or > 4 || start.Honba < 0)
            return "Limited trial: round outside the captured East-round scope";
        // Standard Doman tables start at 4 * 25000. Sticks leave player scores until
        // awarded. Only an unchanged total admits the decoder's zero-kyotaku input.
        // This is a rule-based inference, not an observed counter flag.
        if (start.Scores.Length != 4 || start.Scores.Any(s => s is < -200000 or > 200000)
            || start.Scores.Sum(s => (long)s) != 100000 || start.Kyotaku != 0)
            return "Limited trial: riichi pool is not confirmed zero by score conservation";
        return null;
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
        // Combined declaration/tsumogiri has decoder tests but no independent live
        // field verification. Leave it outside this opt-in scope as well as all kans.
        if (messageId == MahjongPacketMjaiDecoder.DiscardMessageId && payload.Length >= 12
            && BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4)) == 0x113)
            return "Limited trial: combined riichi flag is not independently verified";
        return null;
    }
}
