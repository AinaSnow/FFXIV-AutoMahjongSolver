using System.Buffers.Binary;

namespace Mahjong.Plugin.Game.Mjai;

/// <summary>One winner block of a 7.58 result packet; seats are relative to us (0 = self).</summary>
public sealed record ServerWinner(int Seat, int Han, int Fu, int Limit, int Value, string WinTile, int Honba, int Sticks);

/// <summary>
/// Server-observed hand outcome. Seats are relative to us and points are full points.
/// <see cref="Outcome"/> is "ron", "tsumo", "exhaustive_draw", "abortive_draw" or "unknown";
/// every field is cross-checked against the decoded hand before it is reported, and any
/// disagreement yields "unknown" with <see cref="UnknownReason"/> instead of a guess.
/// This is a side channel for logs: it is never sent to Mortal.
/// </summary>
public sealed record ServerHandResult(
    string Outcome,
    IReadOnlyList<int> Winners,
    int? Discarder,
    IReadOnlyList<int>? Deltas,
    IReadOnlyList<int>? ScoresAfter,
    IReadOnlyList<bool>? Tenpai,
    IReadOnlyList<ServerWinner> WinnerDetails,
    string? UnknownReason)
{
    public bool WeWon => Winners.Contains(0);

    internal static ServerHandResult Unknown(string reason) => new("unknown", [], null, null, null, null, [], reason);
}

/// <summary>Decoded-hand facts the result reader cross-checks against; all seats relative.</summary>
internal sealed record HandResultContext(
    int SelfSeat,
    int[] StartScoresAbsolute,
    int Honba,
    IReadOnlySet<int> RiichiDeclared,
    (int Actor, string Tile, bool Riichi)? LastDiscard,
    (int Actor, string Tile)? LastKakan,
    (int Actor, string Tile)? LastDraw);

/// <summary>
/// Reads the 7.58 result packets (639 tsumo, 640 ron with up to two winner blocks, 643 draw).
/// Layout and evidence: docs/reviews/20261116-c1-live-data-loop.md. The mirror for archived
/// packets is tools/live_hand_labels.py; both are checked against the same sanitized fixture.
/// Never throws: an unexpected shape degrades to an "unknown" result, not a quarantined hand.
/// </summary>
internal static class ServerHandResultReader
{
    private const int BlockWords = 63;

    internal static ServerHandResult Read(int messageId, ReadOnlySpan<byte> payload, HandResultContext context)
    {
        try
        {
            return ReadCore(messageId, payload, context);
        }
        catch (Exception ex)
        {
            // Runs inside the live decode path: any escape would invalidate the hand's public state.
            return ServerHandResult.Unknown(ex.Message);
        }
    }

    private static ServerHandResult ReadCore(int messageId, ReadOnlySpan<byte> payload, HandResultContext context)
    {
        if (payload.Length % 4 != 0)
            throw new InvalidDataException("result payload is not word aligned");
        var w = new int[payload.Length / 4];
        for (int i = 0; i < w.Length; i++)
            w[i] = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4));

        int[] start = context.StartScoresAbsolute;
        int Rel(int absolute) => (absolute - context.SelfSeat + 4) % 4;
        int Abs(int relative) => (relative + context.SelfSeat) % 4;

        if (messageId == MahjongPacketMjaiDecoder.DrawResultMessageId)
        {
            if (w.Length != 66 || w[0] is not (0 or 1))
                throw new InvalidDataException("643 length/draw type");
            var after = new int[4];
            var tenpai = new bool[4];
            for (int seat = 0; seat < 4; seat++)
            {
                int count = w[3 + 16 * seat];
                if (count is < 0 or > 13) throw new InvalidDataException("643 revealed count");
                after[seat] = w[2 + 16 * seat] * 100;
                tenpai[seat] = count > 0;
            }
            var expected = new int[4];
            bool exhaustive = w[0] == 0;
            int tenpaiCount = tenpai.Count(t => t);
            if (exhaustive && tenpaiCount is >= 1 and <= 3)
                for (int seat = 0; seat < 4; seat++)
                    expected[seat] = tenpai[seat] ? 3000 / tenpaiCount : -3000 / (4 - tenpaiCount);
            foreach (int actor in context.RiichiDeclared)
                expected[Abs(actor)] -= 1000;
            var reasons = new List<string>();
            for (int seat = 0; seat < 4; seat++)
                if (after[seat] - start[seat] != expected[seat])
                {
                    reasons.Add("draw payments disagree with tenpai flags (nagashi or unknown draw rule)");
                    break;
                }
            if (exhaustive && context.RiichiDeclared.Any(actor => !tenpai[Abs(actor)]))
                reasons.Add("a riichi player is shown not tenpai");
            if (!exhaustive)
            {
                // One 7.58 sample: only the player who had just drawn reveals a hand.
                for (int seat = 0; seat < 4; seat++)
                    if (tenpai[seat] && context.LastDraw?.Actor != Rel(seat))
                        reasons.Add("abortive draw reveals a hand other than the last drawer's");
            }
            if (reasons.Count > 0)
                return ServerHandResult.Unknown(string.Join("; ", reasons.Distinct()));
            return new(exhaustive ? "exhaustive_draw" : "abortive_draw", [], null,
                Relative(Deltas(after, start), context.SelfSeat), Relative(after, context.SelfSeat),
                exhaustive ? Relative(tenpai, context.SelfSeat) : null, [], null);
        }

        bool tsumo = messageId == MahjongPacketMjaiDecoder.HandResultAMessageId;
        if (tsumo ? w.Length != 64 || w[63] != 0 : w.Length != 2 * BlockWords)
            throw new InvalidDataException($"{messageId} length/trailer");
        var blocks = new List<Block>();
        for (int b = 0; b < (tsumo ? 1 : 2); b++)
            if (DecodeBlock(w.AsSpan(b * BlockWords, BlockWords)) is { } block)
                blocks.Add(block);
            else if (b == 0)
                throw new InvalidDataException("result without a first winner");
        if (blocks.Select(b => b.Winner).Distinct().Count() != blocks.Count)
            throw new InvalidDataException("result repeats a winner");
        if (tsumo ? blocks[0].DealerPays < 0 || blocks[0].OtherPays <= 0
            : blocks.Any(b => b.DealerPays != 0 || b.OtherPays != 0))
            throw new InvalidDataException("result payment fields disagree with the win type");

        var scoresAfter = blocks[^1].ScoresAfter;
        var deltas = Deltas(scoresAfter, start);
        var problems = new List<string>();
        if (blocks[0].Honba != context.Honba) problems.Add("result honba disagrees with hand start");
        int? discarderAbs = null;
        if (tsumo)
        {
            if (context.LastDraw is not { } draw || Abs(draw.Actor) != blocks[0].Winner)
                problems.Add("winner is not the last drawer");
            else if (draw.Tile != MjaiTile.Unknown && draw.Tile != blocks[0].WinTile)
                problems.Add("winning tile is not the drawn tile");
        }
        else
        {
            var discard = context.LastDiscard is { } d ? (d.Actor, d.Tile) : context.LastKakan;
            if (discard is not { } source || blocks.Any(b => b.Winner == Abs(source.Actor)))
                problems.Add("no discarder distinct from the winners");
            else
            {
                discarderAbs = Abs(source.Actor);
                if (source.Tile != MjaiTile.Unknown && blocks.Any(b => b.WinTile != source.Tile))
                    problems.Add("winning tile is not the discarded tile");
            }
        }
        if (problems.Count == 0 && !ExpectedDeltas(blocks, discarderAbs, context).SequenceEqual(deltas))
            problems.Add("payment arithmetic failed");
        if (problems.Count > 0)
            return ServerHandResult.Unknown(string.Join("; ", problems));
        return new(tsumo ? "tsumo" : "ron", blocks.Select(b => Rel(b.Winner)).ToArray(),
            discarderAbs is { } abs ? Rel(abs) : null,
            Relative(deltas, context.SelfSeat), Relative(scoresAfter, context.SelfSeat), null,
            blocks.Select(b => new ServerWinner(Rel(b.Winner), b.Han, b.Fu, b.Limit, b.Value, b.WinTile, b.Honba, b.Sticks)).ToArray(),
            null);
    }

    private static int[] ExpectedDeltas(List<Block> blocks, int? discarderAbs, HandResultContext context)
    {
        var deltas = new int[4];
        var deposits = context.RiichiDeclared.Select(actor => (actor + context.SelfSeat) % 4).ToHashSet();
        // A riichi discard that is itself ronned never becomes a deposit; a winner's own
        // deposit is returned and is not counted in the awarded sticks.
        if (discarderAbs is not null && context.LastDiscard is { Riichi: true } riichiDiscard)
            deposits.Remove((riichiDiscard.Actor + context.SelfSeat) % 4);
        foreach (int seat in deposits)
            if (blocks.All(b => b.Winner != seat))
                deltas[seat] -= 1000;
        foreach (var block in blocks)
        {
            deltas[block.Winner] += block.Value + block.Honba * 300 + block.Sticks * 1000;
            if (discarderAbs is { } loser)
                deltas[loser] -= block.Value + block.Honba * 300;
            else
                for (int payer = 0; payer < 4; payer++)
                    if (payer != block.Winner)
                        deltas[payer] -= (payer == 0 ? block.DealerPays : block.OtherPays) + block.Honba * 100;
        }
        return deltas;
    }

    private static Block? DecodeBlock(ReadOnlySpan<int> w)
    {
        if (w[4] == 4)
        {
            if (w[0] != 255) throw new InvalidDataException("empty winner block carries a tile");
            for (int i = 13; i < 26; i++)
                if (w[i] != 255) throw new InvalidDataException("empty winner block carries tiles");
            return null;
        }
        if (w[4] is < 0 or > 3) throw new InvalidDataException("winner seat outside 0..3");
        int kind = w[0] & 0xFF;
        bool red = (w[0] & 0x100) != 0;
        if ((w[0] & ~0x1FF) != 0 || kind >= Tile.Count34 || w[1] is < 1 or > 13 || w[1] % 3 != 1)
            throw new InvalidDataException("winning tile or revealed count");
        if (red && kind is not (4 or 13 or 22)) throw new InvalidDataException("red flag on a non-five");
        if (w[3] < 0 || (w[3] >> 24) != 0) throw new InvalidDataException("packed han/sticks/honba");
        return new Block(w[4], MjaiTile.Format(new Tile((byte)kind), red), w[2], w[3] & 0xFF, (w[3] >> 8) & 0xFF,
            (w[3] >> 16) & 0xFF, w[5] * 100, -w[6] * 100, -w[7] * 100, w[8],
            [w[9] * 100, w[10] * 100, w[11] * 100, w[12] * 100]);
    }

    private static int[] Deltas(int[] after, int[] start) => [.. Enumerable.Range(0, 4).Select(s => after[s] - start[s])];

    private static T[] Relative<T>(T[] absolute, int selfSeat) =>
        [.. Enumerable.Range(0, 4).Select(r => absolute[(r + selfSeat) % 4])];

    private sealed record Block(int Winner, string WinTile, int Limit, int Han, int Sticks, int Honba,
        int Value, int DealerPays, int OtherPays, int Fu, int[] ScoresAfter);
}
