using System.Collections.Concurrent;
using Dalamud.Plugin.Services;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Dalamud.Mortal;

public readonly record struct CapturedMahjongPacket(
    int MessageId,
    ushort Opcode,
    DateTimeOffset Timestamp,
    byte[] Payload);

/// <summary>
/// Accepts length-delimited packets only through a verified version/variant profile.
/// Bounded queues are consumed by the public-state reducer and Mortal on the framework thread.
/// </summary>
public sealed class MahjongNetworkCapture : IDisposable
{
    private const int MaxQueuedPackets = 2048;

    // Historical fixtures only. Live admission exclusively uses a verified protocol profile.
    private static readonly IReadOnlyDictionary<ushort, PacketSpec> LegacyOpcodes =
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
    private MahjongProtocolProfile? activeProfile;
    private readonly ConcurrentQueue<CapturedMahjongPacket> publicQueue = new();
    private readonly MahjongProtocolProfile[] profiles;
    private readonly Func<string?> variantAccessor;
    private readonly string? gameVersion;
    public string? GameVersion => gameVersion;
    public bool PublicCaptureEnabled { get => publicEnabled; set => publicEnabled = value; }
    private volatile bool publicEnabled, captureEnabled;
    public bool HasVerifiedBuild => profiles.Any(p => p.Verified && p.GameVersion == gameVersion && !string.IsNullOrWhiteSpace(p.Evidence));
    public bool TransportReady { get; internal set; }
    public string TransportStatus { get; internal set; } = "Capture not connected";
    internal void RefreshProfile() => Volatile.Write(ref activeProfile, profiles.FirstOrDefault(p => p.Matches(gameVersion, variantAccessor())));
    public bool ProtocolVerified => profiles.Any(p => p.Matches(gameVersion, variantAccessor()));
    public string ProtocolStatus => ProtocolVerified ? $"Verified protocol; {TransportStatus}" : $"No verified protocol for {gameVersion ?? "unknown build"}/{variantAccessor() ?? "unknown variant"}";
    public bool TryDequeuePublic(out CapturedMahjongPacket packet) => publicQueue.TryDequeue(out packet);
    private volatile bool disposed;
    private long droppedPackets;

    public MahjongNetworkCapture(IPluginLog log,
        Func<string?>? variantAccessor = null, string? profilesDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        this.variantAccessor = variantAccessor ?? (() => null);
        string? processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        string versionPath = Path.Combine(processDirectory ?? "", "ffxivgame.ver");
        try { gameVersion = File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : null; }
        catch (IOException) { gameVersion = null; }
        try { profiles = MahjongProtocolProfile.LoadDirectory(profilesDirectory ?? ""); }
        catch (Exception ex) { profiles = []; log.Warning(ex, "[Mahjong] Invalid protocol profiles; network capture disabled."); }
        RefreshProfile();
        if (!HasVerifiedBuild) log.Warning($"[Mahjong] {ProtocolStatus}; UI-only mode.");
    }

    internal MahjongNetworkCapture(string version, Func<string?> variant, MahjongProtocolProfile[] profiles)
    {
        gameVersion = version; variantAccessor = variant; this.profiles = profiles; RefreshProfile();
    }

    public bool CaptureEnabled { get => captureEnabled; set => captureEnabled = value; }

    public int QueuedPackets => queue.Count;

    public long DroppedPackets => Interlocked.Read(ref droppedPackets);

    public bool TryDequeue(out CapturedMahjongPacket packet) => queue.TryDequeue(out packet);

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        CaptureEnabled = false;
        PublicCaptureEnabled = false;
        while (publicQueue.TryDequeue(out _)) { }
        while (queue.TryDequeue(out _)) { }
    }

    internal static bool TryGetPacketSpec(ushort opcode, out int messageId, out int payloadLength)
    {
        if (LegacyOpcodes.TryGetValue(opcode, out var spec))
        {
            messageId = spec.MessageId;
            payloadLength = spec.PayloadLength;
            return true;
        }

        messageId = 0;
        payloadLength = 0;
        return false;
    }

    internal void Record(RawReceivedPacket packet)
    {
        if (disposed || !(CaptureEnabled || PublicCaptureEnabled)) return;
        var profile = Volatile.Read(ref activeProfile);
        if (profile is null || !profile.TryGet(packet.Opcode,out var spec) || spec is null) return;
        // A profile length mismatch is lost history, never a reason to truncate or pad the payload.
        if (packet.Transport != "deucalion" || packet.Payload.Length != spec.PayloadLength)
        {
            MarkTransportGap(); return;
        }
        if ((CaptureEnabled && queue.Count >= MaxQueuedPackets) || (PublicCaptureEnabled && publicQueue.Count >= MaxQueuedPackets))
        {
            MarkTransportGap(); return;
        }
        var captured = new CapturedMahjongPacket(spec.MessageId,packet.Opcode,packet.Time,packet.Payload);
        if (CaptureEnabled) queue.Enqueue(captured);
        if (PublicCaptureEnabled) publicQueue.Enqueue(captured);
    }

    internal void MarkTransportGap() => Interlocked.Increment(ref droppedPackets);

    private readonly record struct PacketSpec(int MessageId, int PayloadLength);
}
