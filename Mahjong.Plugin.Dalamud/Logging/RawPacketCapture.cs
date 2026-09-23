using Mahjong.Plugin.Dalamud.Hooks;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Network;

namespace Mahjong.Plugin.Dalamud.Logging;

public sealed record RawReceivedPacket(DateTimeOffset Time, long Timestamp, ushort Opcode, int SegmentLength, byte[] Payload);

/// <summary>Read-only diagnostic tap. Raw bytes never enter the public-state or Mortal queues.</summary>
internal sealed unsafe class RawPacketCapture(IGameInteropProvider interop) : IDisposable
{
    internal const int HeaderLength = 32;
    internal const int MaxSegmentLength = 65536;
    private delegate void ReceiveDelegate(PacketDispatcher* dispatcher, uint target, byte* ipc);
    private Hook<ReceiveDelegate>? hook;
    private ReceiveDelegate? original;
    private bool failed;
    private volatile bool enabled;
    public event Action<RawReceivedPacket>? Received;
    public event Action<string>? Rejected;
    public string? Error { get; private set; }
    public bool IsEnabled => enabled;
    public long RejectedPackets => Interlocked.Read(ref rejected);
    private long rejected;

    public void SetEnabled(bool value)
    {
        if (!value)
        {
            bool wasEnabled = enabled;
            enabled = false;
            if (wasEnabled) hook?.Disable();
            failed = false;
            return;
        }
        if (enabled || failed) return;
        try
        {
            if (PacketDispatcher.StaticVirtualTablePointer is null) throw new InvalidOperationException("Receive vtable unavailable");
            // The 2026-09-23 live capture proved the vtable-slot tap observed no calls.
            // Intercept the function entry so direct calls are covered as well.
            if (!HookSetup.TryEnable(ref hook,
                () => interop.HookFromAddress<ReceiveDelegate>(
                    (nint)PacketDispatcher.StaticVirtualTablePointer->OnReceivePacket, Receive),
                current => { original = current.Original; current.Enable(); }, out var failure))
            {
                failed = true;
                Error = $"Capture hook unavailable: {HookSetup.DescribeFailure(failure!)}";
                return;
            }
            Error = null;
            enabled = true;
        }
        catch (Exception ex) { failed = true; Error = $"Capture hook unavailable: {ex.Message}"; }
    }

    private void Receive(PacketDispatcher* dispatcher, uint target, byte* ipc)
    {
        var callOriginal = original;
        if (callOriginal is null) return;
        try
        {
            if (enabled)
            {
                if (TryCopy((nint)ipc, target, out var packet, out string error)) Received?.Invoke(packet!);
                else { Interlocked.Increment(ref rejected); Rejected?.Invoke(error); }
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref rejected);
            Error = $"Capture rejected: {ex.GetType().Name}";
            try { Rejected?.Invoke(Error); } catch { /* Never interfere with the original receiver. */ }
        }
        finally { callOriginal(dispatcher, target, ipc); }
    }

    internal static bool TryReadHeader(ReadOnlySpan<byte> header, uint target, out int size, out ushort opcode)
    {
        size = 0; opcode = 0;
        if (header.Length < HeaderLength) return false;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        // Segment header immediately precedes IPC header. See docs/auto-packet-logger.md for sources.
        if (length < HeaderLength || length > MaxSegmentLength
            || BinaryPrimitives.ReadUInt16LittleEndian(header[12..]) != 3
            || BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != target
            || BinaryPrimitives.ReadUInt16LittleEndian(header[16..]) != 0x14) return false;
        size = (int)length;
        opcode = BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
        return true;
    }

    internal static bool TryCopy(nint ipc, uint target, out RawReceivedPacket? packet, out string error)
    {
        packet = null; error = "invalid-segment-header";
        if ((nuint)ipc < 16) return false;
        Span<byte> header = stackalloc byte[HeaderLength];
        if (!Read(ipc - 16, header) || !TryReadHeader(header, target, out int size, out ushort opcode)) return false;
        var segment = new byte[size];
        if (!Read(ipc - 16, segment)) { error = "unreadable-segment"; return false; }
        if (!segment.AsSpan(0, HeaderLength).SequenceEqual(header)) { error = "segment-changed-during-copy"; return false; }
        packet = new(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), opcode, size, segment[HeaderLength..]);
        error = "";
        return true;
    }

    private static bool Read(nint address, Span<byte> bytes)
    {
        fixed (byte* destination = bytes)
            return ReadProcessMemory((nint)(-1), address, destination, (nuint)bytes.Length, out nuint read) && read == (nuint)bytes.Length;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint process, nint address, void* buffer, nuint size, out nuint read);

    public void Dispose() { enabled = false; hook?.Dispose(); hook = null; }
}
