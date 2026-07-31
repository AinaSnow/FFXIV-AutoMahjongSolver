using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Application.Network;
using FFXIVClientStructs.FFXIV.Client.Network;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Dalamud.Mortal;

public readonly record struct CapturedMahjongPacket(
    int MessageId,
    ushort Opcode,
    DateTimeOffset Timestamp,
    byte[] Payload);

/// <summary>
/// Captures only the confirmed Mahjong receive messages. The detour performs a
/// fixed-size copy into a bounded queue; decoding and process I/O happen later
/// on the framework thread.
/// </summary>
public sealed unsafe class MahjongNetworkCapture : IDisposable
{
    private const int MaxQueuedPackets = 2048;
    private const int ReceivePayloadOffset = 0x10;
    private const int ReceiveOpcodeOffset = 0x02;

    private static readonly IReadOnlyDictionary<ushort, PacketSpec> CurrentOpcodes =
        new Dictionary<ushort, PacketSpec>
        {
            [0x0129] = new(MahjongPacketMjaiDecoder.MatchStartMessageId, 48),
            [0x018e] = new(MahjongPacketMjaiDecoder.HandStartMessageId, 104),
            [0x00b0] = new(MahjongPacketMjaiDecoder.DrawOrCallMessageId, 24),
            [0x0273] = new(MahjongPacketMjaiDecoder.HandResultAMessageId, 256),
            [0x039c] = new(MahjongPacketMjaiDecoder.HandResultBMessageId, 504),
            [0x0156] = new(MahjongPacketMjaiDecoder.DiscardMessageId, 32),
        };

    private readonly ConcurrentQueue<CapturedMahjongPacket> queue = new();
    private readonly IPluginLog log;
    private Hook<ReceivePacketDelegate>? receiveHook;
    private bool disposed;
    private long droppedPackets;

    private delegate void ReceivePacketDelegate(PacketDispatcher* dispatcher, uint targetId, byte* packet);

    public MahjongNetworkCapture(IGameInteropProvider gameInterop, IPluginLog log)
    {
        ArgumentNullException.ThrowIfNull(gameInterop);
        ArgumentNullException.ThrowIfNull(log);
        this.log = log;

        nint address = GetVirtualFunctionAddress(
            PacketDispatcher.StaticVirtualTablePointer,
            "OnReceivePacket");
        receiveHook = gameInterop.HookFromAddress<ReceivePacketDelegate>(address, ReceivePacketDetour);
        receiveHook.Enable();
        log.Information("[Mortal] Mahjong receive capture installed (6 payload-only opcodes; roster excluded).");
    }

    public bool CaptureEnabled { get; set; }

    public int QueuedPackets => queue.Count;

    public long DroppedPackets => Interlocked.Read(ref droppedPackets);

    public bool TryDequeue(out CapturedMahjongPacket packet) => queue.TryDequeue(out packet);

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        CaptureEnabled = false;
        receiveHook?.Dispose();
        receiveHook = null;
        while (queue.TryDequeue(out _)) { }
    }

    internal static bool TryGetPacketSpec(ushort opcode, out int messageId, out int payloadLength)
    {
        if (CurrentOpcodes.TryGetValue(opcode, out var spec))
        {
            messageId = spec.MessageId;
            payloadLength = spec.PayloadLength;
            return true;
        }

        messageId = 0;
        payloadLength = 0;
        return false;
    }

    private void ReceivePacketDetour(PacketDispatcher* dispatcher, uint targetId, byte* packet)
    {
        var hook = receiveHook;
        if (hook is null)
            return;

        // The dispatcher may reuse or mutate the receive buffer, so preserve
        // the wire payload before handing it to the game.
        if (CaptureEnabled && packet is not null)
            CapturePacket(packet);

        hook.Original(dispatcher, targetId, packet);
    }

    private void CapturePacket(byte* packet)
    {
        try
        {
            ushort opcode = *(ushort*)(packet + ReceiveOpcodeOffset);
            if (!TryGetPacketSpec(opcode, out int messageId, out int payloadLength))
                return;
            if (queue.Count >= MaxQueuedPackets)
            {
                Interlocked.Increment(ref droppedPackets);
                return;
            }

            var payload = new byte[payloadLength];
            Marshal.Copy((nint)(packet + ReceivePayloadOffset), payload, 0, payloadLength);
            queue.Enqueue(new CapturedMahjongPacket(messageId, opcode, DateTimeOffset.UtcNow, payload));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Mortal] Mahjong receive capture failed; packet ignored.");
        }
    }

    private static nint GetVirtualFunctionAddress<T>(T* virtualTable, string fieldName)
        where T : unmanaged
    {
        var field = typeof(T).GetField(fieldName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException(typeof(T).FullName, fieldName);
        var offset = field.GetCustomAttribute<FieldOffsetAttribute>()?.Value
            ?? throw new InvalidOperationException($"Virtual-table field {fieldName} has no FieldOffset.");
        return *(nint*)((byte*)virtualTable + offset);
    }

    private readonly record struct PacketSpec(int MessageId, int PayloadLength);
}
