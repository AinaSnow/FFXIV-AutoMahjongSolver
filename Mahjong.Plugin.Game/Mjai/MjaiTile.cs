namespace Mahjong.Plugin.Game.Mjai;

public static class MjaiTile
{
    public const string Unknown = "?";

    private static readonly string[] Honors = ["E", "S", "W", "N", "P", "F", "C"];

    public static string Format(Tile tile, bool isRed = false)
    {
        if (tile.Id < 27)
        {
            int number = tile.Id % 9 + 1;
            if (isRed && number != 5)
                throw new ArgumentException("only a suited five can be red", nameof(isRed));

            char suit = tile.Id switch
            {
                < 9 => 'm',
                < 18 => 'p',
                _ => 's',
            };
            return isRed ? $"{number}{suit}r" : $"{number}{suit}";
        }

        if (isRed)
            throw new ArgumentException("honor tiles cannot be red", nameof(isRed));
        return Honors[tile.Id - 27];
    }

    public static string[] FormatHand(StateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.Observations.HasFlag(SnapshotObservationFlags.HandRedIdentity))
            throw new InvalidOperationException("snapshot does not contain observed red-tile identity");
        if (snapshot.Hand.Count != snapshot.HandIsRed.Count)
            throw new InvalidOperationException("hand and red-identity arrays are not aligned");

        var result = new string[snapshot.Hand.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = Format(snapshot.Hand[i], snapshot.HandIsRed[i]);
        return result;
    }
}
