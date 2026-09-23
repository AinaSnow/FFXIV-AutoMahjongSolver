using System.Buffers.Binary;

namespace Mahjong.Plugin.Dalamud.Logging;

/// <summary>Local diagnostic only. Bytes are a candidate header, never a validated network packet.</summary>
public sealed record PacketReadFailure(string Reason, uint ExpectedTarget = 0, byte[]? Header = null,
    string[]? FailedChecks = null, int? Win32Error = null)
{
    public DateTimeOffset Time { get; } = DateTimeOffset.UtcNow;
    internal static string NormalizeReason(string reason) => reason switch
    {
        "invalid-ipc-pointer" or "unreadable-header" or "short-header" or "invalid-segment-length"
        or "segment-type-mismatch" or "target-mismatch" or "ipc-marker-mismatch"
        or "unreadable-segment" or "segment-changed-during-copy" or "capture-exception"
        or "pre-roll-incomplete" or "invalid-segment-header" or "transport-invalid-frame" or "transport-disconnected" => reason,
        _ => "other",
    };

    internal object ToDiagnosticRecord()
    {
        var header = Header is { Length: 32 } ? Header : null;
        return new
        {
            e = "capture-diagnostic", t = Time, reason = NormalizeReason(Reason),
            failed_checks = FailedChecks ?? Array.Empty<string>(), win32_error = Win32Error,
            header_offset_from_ipc = Reason.StartsWith("transport-",StringComparison.Ordinal) ? (int?)null : -16,
            requested_header_bytes = Reason.StartsWith("transport-",StringComparison.Ordinal) ? 0 : 32,
            header_hex = header is null ? null : Convert.ToHexString(header),
            expected_target = ExpectedTarget,
            candidate_length = header is null ? (uint?)null : BinaryPrimitives.ReadUInt32LittleEndian(header),
            candidate_target = header is null ? (uint?)null : BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)),
            candidate_segment_type = header is null ? (ushort?)null : BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12)),
            candidate_ipc_marker = header is null ? (ushort?)null : BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(16)),
            candidate_opcode = header is null ? null : $"0x{BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18)):X4}",
            layout_verified = false,
        };
    }
}
