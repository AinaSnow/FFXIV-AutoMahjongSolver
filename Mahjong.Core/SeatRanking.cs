namespace Mahjong.Core;

public static class SeatRanking
{
    /// <summary>Null for a tied seat when the initial East seat has not been observed.</summary>
    public static int? Rank(IReadOnlyList<int> scores, int seat, int? initialDealerSeat = null)
    {
        if (scores.Count != 4 || seat is < 0 or > 3) return null;
        bool knownOrder = initialDealerSeat is >= 0 and < 4;
        int rank = 1;
        for (int other = 0; other < 4; other++)
        {
            if (other == seat) continue;
            if (scores[other] > scores[seat]) rank++;
            else if (scores[other] == scores[seat])
            {
                if (!knownOrder) return null;
                if ((other - initialDealerSeat!.Value + 4) % 4 < (seat - initialDealerSeat.Value + 4) % 4) rank++;
            }
        }
        return rank;
    }
}
