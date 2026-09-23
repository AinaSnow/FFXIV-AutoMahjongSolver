using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace Mahjong.Plugin.Dalamud.Logging;

/// <summary>Deucalion 1.5 framing. Length comes from the pipe, never adjacent game memory.</summary>
internal static class DeucalionWire
{
    internal const int MaxFrameLength = 65545;
    internal static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellation)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellation).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length is < 9 or > MaxFrameLength) throw new InvalidDataException("Invalid Deucalion frame length");
        byte[] frame = new byte[length];
        prefix.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(4), cancellation).ConfigureAwait(false);
        return frame;
    }

    internal static byte[] Command(byte op, uint channel, string data = "")
    {
        byte[] text = Encoding.UTF8.GetBytes(data), frame = new byte[9 + text.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)frame.Length);
        frame[4] = op;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5), channel);
        text.CopyTo(frame,9);
        return frame;
    }

    internal static string? Hello(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 9 || frame[4] != 0 || BinaryPrimitives.ReadUInt32LittleEndian(frame[5..]) != 9000) return null;
        string text = Encoding.UTF8.GetString(frame[9..]);
        return text.StartsWith("SERVER HELLO.", StringComparison.Ordinal) ? text : null;
    }

    internal static bool CompatibleHello(string hello) =>
        hello.Contains("VERSION: 1.5.", StringComparison.Ordinal)
        && hello.Contains("RECV ON", StringComparison.Ordinal)
        && hello.Contains("CREATE_TARGET ON", StringComparison.Ordinal);

    internal static RawReceivedPacket? Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 9 || frame.Length > MaxFrameLength || BinaryPrimitives.ReadUInt32LittleEndian(frame) != frame.Length)
            throw new InvalidDataException("Invalid Deucalion envelope");
        // Only received Zone IPC is relevant. Never send game packets through this pipe.
        if (frame[4] != 3 || BinaryPrimitives.ReadUInt32LittleEndian(frame[5..]) != 1) return null;
        if (frame.Length < 41) throw new InvalidDataException("Truncated Deucalion IPC");
        var data = frame[9..];
        if (BinaryPrimitives.ReadUInt16LittleEndian(data[16..]) != 0x14)
            throw new InvalidDataException("Unexpected Deucalion IPC marker");
        return new RawReceivedPacket(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(),
            BinaryPrimitives.ReadUInt16LittleEndian(data[18..]), null, data[32..].ToArray(),
            "deucalion", frame.Length, BinaryPrimitives.ReadUInt32LittleEndian(data),
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]), BinaryPrimitives.ReadUInt64LittleEndian(data[8..]),
            data.Slice(16,16).ToArray());
    }
}
