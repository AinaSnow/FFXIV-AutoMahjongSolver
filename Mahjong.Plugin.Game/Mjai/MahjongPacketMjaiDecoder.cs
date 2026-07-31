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

    private static readonly string[] SeatNames = ["E", "S", "W", "N"];

    private bool gameStarted;
    private bool handOpen;
    private int? selfSeat;
    private LastDiscard? lastDiscard;

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

            case HandResultAMessageId:
            case HandResultBMessageId:
                if (handOpen)
                {
                    output.Add(new MjaiEndKyoku());
                    handOpen = false;
                    lastDiscard = null;
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
        return output;
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
        bool isVerifiedRed = id == 22 && copy == 1;
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
            scores[RelativeSeat(absoluteSeat, absoluteSelfSeat)] = ReadInt32(payload, 32 + absoluteSeat * 4) * 100;

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
            Honba: 0,
            Kyotaku: 0,
            Oya: RelativeSeat(0, absoluteSelfSeat),
            Scores: scores,
            Tehais: tehais));

        selfSeat = absoluteSelfSeat;
        handOpen = true;
        lastDiscard = null;
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
        if (action == 0x100)
        {
            string tile = seat == selfSeat.Value
                ? DecodePhysicalTile(ReadUInt16(payload, 8))
                : MjaiTile.Unknown;
            output.Add(new MjaiTsumo(actor, tile));
            lastDiscard = null;
            return;
        }

        if (action is 0x500 or 0x600 && lastDiscard is { } previous)
        {
            output.Add(new MjaiOpenCall(
                action == 0x500 ? "pon" : "chi",
                actor,
                previous.Actor,
                previous.Tile,
                [DecodePhysicalTile(ReadUInt16(payload, 12)), DecodePhysicalTile(ReadUInt16(payload, 14))]));
        }
        lastDiscard = null;
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
        ushort physical = ReadUInt16(payload, 12);
        LastDiscardPhysical = physical;
        LastDiscardAction = action;
        string tile = DecodePhysicalTile(physical);
        bool riichi = action == 0x111;
        if (riichi)
            output.Add(new MjaiReach(actor));
        output.Add(new MjaiDahai(actor, tile, action == 0x112));
        if (riichi)
            output.Add(new MjaiReachAccepted(actor));
        lastDiscard = new LastDiscard(actor, tile);
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
}
