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
    public event Action<PacketReadFailure>? Rejected;
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
            nint entry = (nint)PacketDispatcher.StaticVirtualTablePointer->OnReceivePacket;
            using var process = Process.GetCurrentProcess();
            var module = process.MainModule ?? throw new InvalidOperationException("Game module unavailable");
            if (!IsGameCodeAddress(entry, module.BaseAddress, module.ModuleMemorySize))
                throw new InvalidOperationException("Receive entry is outside the game module (possibly a previous vtable hook). Fully restart the game before capturing.");
            // The 2026-09-23 live capture proved the vtable-slot tap observed no calls.
            // Intercept the function entry so direct calls are covered as well.
            if (!HookSetup.TryEnable(ref hook,
                () => interop.HookFromAddress<ReceiveDelegate>(
                    entry, Receive),
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
                if (TryCopyDetailed((nint)ipc, target, Read, out var packet, out var failure)) Received?.Invoke(packet!);
                else { Interlocked.Increment(ref rejected); Rejected?.Invoke(failure!); }
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref rejected);
            Error = $"Capture rejected: {ex.GetType().Name}";
            try { Rejected?.Invoke(new PacketReadFailure("capture-exception", target)); } catch { /* Never interfere with the original receiver. */ }
        }
        finally { callOriginal(dispatcher, target, ipc); }
    }

    internal static bool IsGameCodeAddress(nint entry, nint moduleBase, int moduleSize) =>
        moduleBase > 0 && moduleSize > 0 && entry >= moduleBase && (nuint)(entry - moduleBase) < (nuint)moduleSize;

    internal static bool TryReadHeader(ReadOnlySpan<byte> header, uint target, out int size, out ushort opcode)
    {
        size = 0; opcode = 0;
        if (HeaderFailures(header, target).Length != 0) return false;
        size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
        opcode = BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
        return true;
    }

    internal static string[] HeaderFailures(ReadOnlySpan<byte> header, uint target)
    {
        if (header.Length < HeaderLength) return ["short-header"];
        List<string> failures = [];
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length < HeaderLength || length > MaxSegmentLength) failures.Add("invalid-segment-length");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[12..]) != 3) failures.Add("segment-type-mismatch");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != target) failures.Add("target-mismatch");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[16..]) != 0x14) failures.Add("ipc-marker-mismatch");
        return failures.ToArray();
    }

    internal delegate bool MemoryReader(nint address, Span<byte> bytes, out int win32Error);

    internal static bool TryCopy(nint ipc, uint target, out RawReceivedPacket? packet, out string error)
    {
        bool copied = TryCopyDetailed(ipc, target, Read, out packet, out var failure);
        error = failure?.Reason ?? "";
        return copied;
    }

    internal static bool TryCopyDetailed(nint ipc, uint target, MemoryReader read,
        out RawReceivedPacket? packet, out PacketReadFailure? failure)
    {
        packet = null; failure = null;
        if (ipc < 16) { failure = new("invalid-ipc-pointer", target); return false; }
        Span<byte> header = stackalloc byte[HeaderLength];
        if (!read(ipc - 16, header, out int readError))
        {
            // Never serialize a partial or uninitialized stack buffer.
            failure = new("unreadable-header", target, Win32Error: readError);
            return false;
        }
        var checks = HeaderFailures(header, target);
        if (checks.Length != 0)
        {
            failure = new(checks[0], target, header.ToArray(), checks);
            return false; // Do not allocate or read a payload using an unvalidated length.
        }
        int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
        ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
        var segment = new byte[size];
        if (!read(ipc - 16, segment, out readError))
            { failure = new("unreadable-segment", target, header.ToArray(), Win32Error: readError); return false; }
        if (!segment.AsSpan(0, HeaderLength).SequenceEqual(header))
            { failure = new("segment-changed-during-copy", target, header.ToArray()); return false; }
        packet = new(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), opcode, size, segment[HeaderLength..]);
        return true;
    }

    private static bool Read(nint address, Span<byte> bytes, out int win32Error)
    {
        fixed (byte* destination = bytes)
        {
            bool ok = ReadProcessMemory((nint)(-1), address, destination, (nuint)bytes.Length, out nuint read);
            win32Error = ok ? 0 : Marshal.GetLastPInvokeError();
            return ok && read == (nuint)bytes.Length;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint process, nint address, void* buffer, nuint size, out nuint read);

    public void Dispose() { enabled = false; hook?.Dispose(); hook = null; }
}
