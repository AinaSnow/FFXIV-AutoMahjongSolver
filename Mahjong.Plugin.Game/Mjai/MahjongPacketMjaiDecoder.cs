using System.Buffers.Binary;

namespace Mahjong.Plugin.Game.Mjai;

/// <summary>
/// Converts the confirmed Doman Mahjong receive-message payloads into the
/// player-0 MJAI stream consumed by Mortal. Message IDs are stable analysis
/// identifiers; patch-specific opcodes are mapped by the Dalamud capture layer.
/// </summary>
public sealed class MahjongPacketMjaiDecoder
{
    public const int MatchStartMessageId = 636;
    public const int HandStartMessageId = 637;
    public const int DrawOrCallMessageId = 638;
    public const int HandResultAMessageId = 639;
    public const int HandResultBMessageId = 640;
    public const int DiscardMessageId = 641;
    // 642 remains the historical roster ID. This is an analysis ID, not a wire opcode.
    public const int DrawResultMessageId = 643;

    private static readonly string[] SeatNames = ["E", "S", "W", "N"];

    private bool gameStarted;
    private bool handOpen;
    private int? selfSeat;
    private LastDiscard? lastDiscard;
    private LastDraw? lastDraw;
    private readonly bool[] declaredRiichi = new bool[4];
    private readonly List<ConfirmedPon> pons = [];
    private readonly HashSet<byte> kanKinds = [];
    private int? pendingKanActor;

    /// <summary>Raw tile-kind values from the most recently decoded hand-start packet.</summary>
    public IReadOnlyList<int> LastHandStartTileKinds { get; private set; } = [];

    public int? LastHandStartDoraKind { get; private set; }

    public ushort? LastDiscardPhysical { get; private set; }

    public uint? LastDiscardAction { get; private set; }

    public IReadOnlyList<IMjaiEvent> Process(int messageId, ReadOnlySpan<byte> payload)
    {
        var output = new List<IMjaiEvent>(3);
        switch (messageId)
        {
            case MatchStartMessageId:
                if (payload.Length >= 44)
                {
                    if (handOpen)
                        output.Add(new MjaiEndKyoku());
                    if (gameStarted)
                    {
                        output.Add(new MjaiEndGame());
                        gameStarted = false;
                    }
                    StartGame(output);
                    handOpen = false;
                    selfSeat = null;
                    lastDiscard = null;
                    lastDraw = null;
                }
                break;

            case HandStartMessageId:
                DecodeHandStart(payload, output);
                break;

            case DrawOrCallMessageId:
                DecodeDrawOrCall(payload, output);
                break;

            case DiscardMessageId:
                DecodeDiscard(payload, output);
                break;

            case DrawResultMessageId:
                // Only the observed 264-byte result boundary is supported; do not infer
                // noten, nagashi, or other result semantics from its opaque fields.
                if (payload.Length != 264)
                    throw new InvalidDataException("Draw result payload must be exactly 264 bytes.");
                goto case HandResultAMessageId;
            case HandResultAMessageId:
            case HandResultBMessageId:
                if (handOpen)
                {
                    output.Add(new MjaiEndKyoku());
                    handOpen = false;
                    lastDiscard = null;
                    lastDraw = null;
                }
                break;
        }
        return output;
    }

    public IReadOnlyList<IMjaiEvent> Finish()
    {
        var output = new List<IMjaiEvent>(2);
        if (handOpen)
            output.Add(new MjaiEndKyoku());
        if (gameStarted)
            output.Add(new MjaiEndGame());

        handOpen = false;
        gameStarted = false;
        selfSeat = null;
        lastDiscard = null;
        lastDraw = null;
        return output;
    }

    /// <summary>Repairs one UI-observed discard that was absent from capture so a following call can be decoded.</summary>
    public void RecoverLastDiscard(int actor, string tile)
    {
        if (!handOpen || !IsSeat(actor) || actor == 0 || tile == MjaiTile.Unknown)
            throw new ArgumentException("Recovered discard must be a known opponent tile in an open hand.");
        lastDiscard = new LastDiscard(actor, tile);
        lastDraw = null;
    }

    public static string DecodePhysicalTile(ushort physical)
    {
        if (physical == ushort.MaxValue)
            return MjaiTile.Unknown;

        int id = physical >> 2;
        if (id is >= 34 and <= 36)
            return DecodeTileKind(id);
        if (id >= Tile.Count34)
            return MjaiTile.Unknown;

        int copy = physical & 3;
        // Captured self draws/discards confirm the red flag for all three suited fives.
        bool isVerifiedRed = id is 4 or 13 or 22 && copy == 1;
        return MjaiTile.Format(new Tile((byte)id), isVerifiedRed);
    }

    /// <summary>
    /// Decodes the known tile-kind aliases used by hand-start payloads. Unknown
    /// values are deliberately retained as "?" so the live bridge can repair
    /// them from the observed UI hand without inventing a tile.
    /// </summary>
    public static string DecodeTileKind(int id) => id switch
    {
        >= 0 and < Tile.Count34 => MjaiTile.Format(new Tile((byte)id)),
        34 => MjaiTile.Format(Tile.FromId(4), isRed: true),
        35 => MjaiTile.Format(Tile.FromId(13), isRed: true),
        36 => MjaiTile.Format(Tile.FromId(22), isRed: true),
        0x104 => MjaiTile.Format(Tile.FromId(4), isRed: true),
        0x10D => MjaiTile.Format(Tile.FromId(13), isRed: true),
        0x116 => MjaiTile.Format(Tile.FromId(22), isRed: true),
        _ => MjaiTile.Unknown,
    };

    private static int ScaleScore(int hundreds)
    {
        if (hundreds is < -2000 or > 2000)
            throw new InvalidDataException("Opening score outside the validated range.");
        return hundreds * 100;
    }

    private void DecodeHandStart(ReadOnlySpan<byte> payload, List<IMjaiEvent> output)
    {
        if (payload.Length < 100)
            return;

        int absoluteSelfSeat = ReadInt32(payload, 24);
        if (!IsSeat(absoluteSelfSeat))
            return;

        StartGame(output);
        if (handOpen)
            output.Add(new MjaiEndKyoku());

        int handIndex = ReadInt32(payload, 8);
        int doraId = (int)(ReadUInt32(payload, 28) & 0xff);
        LastHandStartDoraKind = doraId;
        string dora = DecodeTileKind(doraId);

        var scores = new int[4];
        for (int absoluteSeat = 0; absoluteSeat < 4; absoluteSeat++)
            scores[RelativeSeat(absoluteSeat, absoluteSelfSeat)] = ScaleScore(ReadInt32(payload, 32 + absoluteSeat * 4));

        int kyotaku = DomanMortalTrialGuard.InferOpeningRiichiSticks(scores)
            ?? throw new InvalidDataException("Opening scores cannot establish the Doman riichi pool.");
        var tehais = new string[4][];
        for (int actor = 0; actor < 4; actor++)
            tehais[actor] = Enumerable.Repeat(MjaiTile.Unknown, 13).ToArray();
        var rawTileKinds = new int[13];
        for (int i = 0; i < 13; i++)
        {
            int id = ReadInt32(payload, 48 + i * 4);
            rawTileKinds[i] = id;
            tehais[0][i] = DecodeTileKind(id);
        }
        LastHandStartTileKinds = rawTileKinds;

        int round = Math.Max(0, handIndex / 4);
        output.Add(new MjaiStartKyoku(
            Bakaze: round < SeatNames.Length ? SeatNames[round] : "E",
            DoraMarker: dora,
            Kyoku: Math.Abs(handIndex % 4) + 1,
            Honba: ReadInt32(payload, 12),
            Kyotaku: kyotaku,
            Oya: RelativeSeat(0, absoluteSelfSeat),
            Scores: scores,
            Tehais: tehais));

        selfSeat = absoluteSelfSeat;
        Array.Clear(declaredRiichi);
        pons.Clear();
        kanKinds.Clear();
        pendingKanActor = null;
        handOpen = true;
        lastDiscard = null;
        lastDraw = null;
    }

    private void DecodeDrawOrCall(ReadOnlySpan<byte> payload, List<IMjaiEvent> output)
    {
        if (!handOpen || selfSeat is null || payload.Length < 24)
            return;

        int seat = ReadInt32(payload, 0);
        if (!IsSeat(seat))
            return;

        int actor = RelativeSeat(seat, selfSeat.Value);
        uint action = ReadUInt32(payload, 4);
        if (action == 0x200)
        {
            if (pendingKanActor != actor)
                throw new InvalidDataException("0x200 replacement draw has no matching confirmed kan.");
            // Independent check: marker @10 + revealed result hands reproduce the
            // server's normal/ura/red bonus counts. Opponent @8 is NOT a 1m draw.
            string marker = KnownPhysical(payload, 10);
            // Self @8 is independently matched to UI hand changes and 0x212.
            // Opponent @8 remains hidden, even when the payload contains zero.
            string tile = actor == 0 ? KnownPhysical(payload, 8) : MjaiTile.Unknown;
            output.Add(new MjaiDora(marker));
            output.Add(new MjaiTsumo(actor, tile));
            pendingKanActor = null;
            lastDraw = new LastDraw(actor, tile, Replacement: true);
            return;
        }
        RequireNoPendingKan(action);
        if (action == 0x130)
        {
            RequireDraw(actor, replacement: false, action);
            string[] consumed = [KnownPhysical(payload, 12), KnownPhysical(payload, 14),
                KnownPhysical(payload, 16), KnownPhysical(payload, 18)];
            BeginKan(actor, consumed, action);
            output.Add(new MjaiAnkan(actor, consumed));
            return;
        }
        if (lastDraw is { Replacement: true })
            throw new InvalidDataException($"0x{action:X} skipped the replacement draw's discard/kan.");
        if (action == 0x100)
        {
            string tile = seat == selfSeat.Value
                ? DecodePhysicalTile(ReadUInt16(payload, 8))
                : MjaiTile.Unknown;
            output.Add(new MjaiTsumo(actor, tile));
            lastDiscard = null;
            lastDraw = new LastDraw(actor, tile);
            return;
        }

        if (action is not (0x400 or 0x500 or 0x600))
            throw new InvalidDataException($"Unsupported draw/call action 0x{action:X}; hand history is incomplete.");
        if (lastDiscard is not { } previous || previous.Actor == actor)
            throw new InvalidDataException($"0x{action:X} call has no preceding opponent discard.");
        string[] used = action == 0x400
            ? [KnownPhysical(payload, 12), KnownPhysical(payload, 14), KnownPhysical(payload, 16)]
            : [KnownPhysical(payload, 12), KnownPhysical(payload, 14)];
        if (action == 0x400)
        {
            BeginKan(actor, [previous.Tile, .. used], action);
            output.Add(new MjaiDaiminkan(actor, previous.Actor, previous.Tile, used));
        }
        else
        {
            if (action == 0x500)
            {
                string[] full = [previous.Tile, .. used];
                byte kind = RequireSameKind(full);
                if (pons.Any(p => p.Actor == actor && p.Kind == kind))
                    throw new InvalidDataException("Duplicate pon in hand history.");
                pons.Add(new ConfirmedPon(actor, kind, full));
            }
            output.Add(new MjaiOpenCall(action == 0x500 ? "pon" : "chi",
                actor, previous.Actor, previous.Tile, used));
        }
        lastDiscard = null;
        lastDraw = null;
    }

    private void DecodeDiscard(ReadOnlySpan<byte> payload, List<IMjaiEvent> output)
    {
        if (!handOpen || selfSeat is null || payload.Length < 28)
            return;

        int seat = ReadInt32(payload, 0);
        if (!IsSeat(seat))
            return;

        int actor = RelativeSeat(seat, selfSeat.Value);
        uint action = ReadUInt32(payload, 8);
        RequireNoPendingKan(action);
        if (action is 0x140 or 0x240)
        {
            RequireDraw(actor, replacement: action == 0x240, action);
            // @18 may still contain the preceding kan's tile in a consecutive
            // kan. Only @16 carries the newly added tile.
            string added = KnownPhysical(payload, 16);
            MjaiTile.TryParse(added, out var addedKind, out _);
            var pon = pons.SingleOrDefault(p => p.Actor == actor && p.Kind == addedKind.Id)
                ?? throw new InvalidDataException($"0x{action:X} added kan has no matching confirmed pon.");
            BeginKan(actor, [.. pon.Tiles, added], action);
            pons.Remove(pon);
            output.Add(new MjaiKakan(actor, added, pon.Tiles));
            return;
        }
        // The 0x200 phase uses the same 0x10 discard / 0x02 drawn-tile bits
        // as the independently observed 0x100 family; require a matching rinshan.
        if (action is not (0x110 or 0x111 or 0x112 or 0x113 or 0x210 or 0x212 or 0xA10))
            throw new InvalidDataException($"Unsupported discard action 0x{action:X}; hand history is incomplete.");
        if (action is 0x210 or 0x212)
            RequireDraw(actor, replacement: true, action);
        else if (lastDraw is { Replacement: true })
            throw new InvalidDataException($"0x{action:X} conflicts with the replacement-draw phase.");
        ushort physical = ReadUInt16(payload, 12);
        LastDiscardPhysical = physical;
        LastDiscardAction = action;
        string tile = DecodePhysicalTile(physical);
        if (action == 0x113)
        {
            if (lastDraw is not { } draw || draw.Actor != actor)
                throw new InvalidDataException("Combined riichi requires the same actor's preceding draw.");
            if (declaredRiichi[actor])
                throw new InvalidDataException("Combined riichi repeats an accepted declaration.");
            if (actor == 0 && (tile == MjaiTile.Unknown || draw.Tile != tile))
                throw new InvalidDataException("Combined riichi self discard conflicts with the drawn tile/red identity.");
        }
        if (action == 0x212 && actor == 0
            && (tile == MjaiTile.Unknown || lastDraw?.Tile != tile))
            throw new InvalidDataException("Replacement tsumogiri conflicts with the drawn tile/red identity.");
        bool riichi = action is 0x111 or 0x113;
        if (riichi)
            output.Add(new MjaiReach(actor));
        output.Add(new MjaiDahai(actor, tile, action is 0x112 or 0x113 or 0x212));
        if (riichi)
        {
            output.Add(new MjaiReachAccepted(actor));
            declaredRiichi[actor] = true;
        }
        lastDiscard = new LastDiscard(actor, tile);
        lastDraw = null;
    }

    private void RequireNoPendingKan(uint action)
    {
        if (pendingKanActor is not null)
            throw new InvalidDataException($"0x{action:X} arrived before the confirmed kan's replacement draw/dora.");
    }

    private void RequireDraw(int actor, bool replacement, uint action)
    {
        if (lastDraw is not { } draw || draw.Actor != actor || draw.Replacement != replacement)
            throw new InvalidDataException($"0x{action:X} requires the same actor's preceding {(replacement ? "replacement" : "ordinary")} draw.");
    }

    private void BeginKan(int actor, string[] tiles, uint action)
    {
        if (actor == 0 && action is not (0x130 or 0x140))
            throw new InvalidDataException($"0x{action:X} self open/consecutive added kan layout is not yet independently observed.");
        byte kind = RequireSameKind(tiles);
        if (tiles.Length != 4 || kanKinds.Count >= 4 || !kanKinds.Add(kind))
            throw new InvalidDataException($"0x{action:X} duplicate or excessive kan.");
        pendingKanActor = actor;
        lastDiscard = null;
        lastDraw = null;
    }

    private static byte RequireSameKind(string[] tiles)
    {
        byte? kind = null;
        int reds = 0;
        foreach (string value in tiles)
        {
            if (!MjaiTile.TryParse(value, out var tile, out bool red) || kind is { } previous && previous != tile.Id)
                throw new InvalidDataException("Unknown or mismatched tiles in pon/kan.");
            kind = tile.Id;
            if (red) reds++;
        }
        if (kind is null || reds > 1)
            throw new InvalidDataException("Invalid red identity in pon/kan.");
        return kind.Value;
    }

    private static string KnownPhysical(ReadOnlySpan<byte> payload, int offset)
    {
        string tile = DecodePhysicalTile(ReadUInt16(payload, offset));
        return tile != MjaiTile.Unknown ? tile : throw new InvalidDataException("Unknown physical tile in call/dora.");
    }

    private void StartGame(List<IMjaiEvent> output)
    {
        if (gameStarted)
            return;
        output.Add(new MjaiStartGame());
        gameStarted = true;
    }

    private static bool IsSeat(int seat) => seat is >= 0 and < 4;

    private static bool IsTile(int id) => id is >= 0 and < Tile.Count34;

    private static int RelativeSeat(int absoluteSeat, int absoluteSelfSeat) =>
        (absoluteSeat - absoluteSelfSeat + 4) % 4;

    private static ushort ReadUInt16(ReadOnlySpan<byte> payload, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset, sizeof(ushort)));

    private static int ReadInt32(ReadOnlySpan<byte> payload, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, sizeof(int)));

    private static uint ReadUInt32(ReadOnlySpan<byte> payload, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, sizeof(uint)));

    private sealed record LastDiscard(int Actor, string Tile);
    private sealed record LastDraw(int Actor, string Tile, bool Replacement = false);
    private sealed record ConfirmedPon(int Actor, byte Kind, string[] Tiles);
}
