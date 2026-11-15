namespace Mahjong.Plugin.Dalamud.Logging;

public sealed record RawReceivedPacket(DateTimeOffset Time, long Timestamp, ushort Opcode, int? SegmentLength, byte[] Payload,
    string Transport = "function-entry", int? TransportLength = null, uint? SourceActor = null, uint? TargetActor = null,
    ulong? ServerTimestampMilliseconds = null, byte[]? IpcHeader = null);
