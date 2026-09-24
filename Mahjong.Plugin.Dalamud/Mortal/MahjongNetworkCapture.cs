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
/// Accepts length-delimited packets through a verified or explicitly opted-in limited-trial profile.
/// Bounded queues are consumed by the public-state reducer and Mortal on the framework thread.
/// </summary>
public sealed class MahjongNetworkCapture : IDisposable
{
    private const int MaxQueuedPackets = 2048;

    // Historical fixtures only. Live admission uses exact version/variant protocol profiles.
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
    private readonly Func<bool> trialEnabled;
    private readonly object admissionLock = new();
    private readonly Queue<RawReceivedPacket> preRoll = new();
    private static readonly TimeSpan PreRollWindow = TimeSpan.FromSeconds(2);
    public long AdmissionGeneration { get; private set; }
    private readonly string? gameVersion;
    public string? GameVersion => gameVersion;
    public bool PublicCaptureEnabled { get => publicEnabled; set => SetConsumerEnabled(value, isPublic: true); }
    private volatile bool publicEnabled, captureEnabled;
    public bool HasVerifiedBuild => profiles.Any(p => p.Verified && p.GameVersion == gameVersion && !string.IsNullOrWhiteSpace(p.Evidence));
    public bool TransportReady { get; internal set; }
    public string TransportStatus { get; internal set; } = "Capture not connected";
    public bool HasAdmittedBuild => HasVerifiedBuild || trialEnabled() && profiles.Any(p =>
        p.MatchesLimitedTrial(gameVersion, p.Variant));
    public bool LimitedTrialActive => !ProtocolVerified && trialEnabled() && profiles.Any(p =>
        p.MatchesLimitedTrial(gameVersion, variantAccessor()));
    public bool ProtocolAdmitted => ProtocolVerified || LimitedTrialActive;
    internal void RefreshProfile()
    {
        lock (admissionLock)
        {
            var next = profiles.FirstOrDefault(p => p.Matches(gameVersion, variantAccessor()))
                ?? (trialEnabled() ? profiles.FirstOrDefault(p => p.MatchesLimitedTrial(gameVersion, variantAccessor())) : null);
            if (ReferenceEquals(next, activeProfile)) return;
            activeProfile = next;
            AdmissionGeneration++;
            while (queue.TryDequeue(out _)) { }
            while (publicQueue.TryDequeue(out _)) { }
            if (captureEnabled) ReplayPreRoll(queue);
            if (publicEnabled) ReplayPreRoll(publicQueue);
        }
    }
    public bool ProtocolVerified => profiles.Any(p => p.Matches(gameVersion, variantAccessor()));
    public string ProtocolStatus => ProtocolVerified ? $"Verified protocol; {TransportStatus}"
        : LimitedTrialActive ? $"Limited trial: East rounds, zero opening riichi pool, no kans; {TransportStatus}"
        : $"No admitted protocol for {gameVersion ?? "unknown build"}/{variantAccessor() ?? "unknown variant"}";
    public bool TryDequeuePublic(out CapturedMahjongPacket packet) => publicQueue.TryDequeue(out packet);
    private volatile bool disposed;
    private long droppedPackets;

    public MahjongNetworkCapture(IPluginLog log,
        Func<string?>? variantAccessor = null, string? profilesDirectory = null, Func<bool>? trialEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        this.variantAccessor = variantAccessor ?? (() => null);
        this.trialEnabled = trialEnabled ?? (() => false);
        string? processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        string versionPath = Path.Combine(processDirectory ?? "", "ffxivgame.ver");
        try { gameVersion = File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : null; }
        catch (IOException) { gameVersion = null; }
        try { profiles = MahjongProtocolProfile.LoadDirectory(profilesDirectory ?? ""); }
        catch (Exception ex) { profiles = []; log.Warning(ex, "[Mahjong] Invalid protocol profiles; network capture disabled."); }
        RefreshProfile();
        if (!ProtocolAdmitted) log.Warning($"[Mahjong] {ProtocolStatus}; UI-only until an admitted table is detected.");
    }

    internal MahjongNetworkCapture(string version, Func<string?> variant, MahjongProtocolProfile[] profiles, Func<bool>? trialEnabled = null)
    {
        gameVersion = version; variantAccessor = variant; this.profiles = profiles;
        this.trialEnabled = trialEnabled ?? (() => false); RefreshProfile();
    }

    public bool CaptureEnabled { get => captureEnabled; set => SetConsumerEnabled(value, isPublic: false); }

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
        lock (admissionLock) preRoll.Clear();
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

    private void SetConsumerEnabled(bool value, bool isPublic)
    {
        lock (admissionLock)
        {
            if (isPublic ? publicEnabled == value : captureEnabled == value) return;
            if (isPublic) publicEnabled = value; else captureEnabled = value;
            if (value && !disposed) ReplayPreRoll(isPublic ? publicQueue : queue);
        }
    }

    // The first deal arrives before the addon is visible. Hold only mapped packets
    // for two seconds, then admit them after the actual client variant is known.
    internal void Record(RawReceivedPacket packet)
    {
        if (disposed || !HasAdmittedBuild) return;
        lock (admissionLock)
        {
            if (disposed) return;
            bool candidate = profiles.Any(p => (p.Matches(gameVersion, p.Variant)
                || trialEnabled() && p.MatchesLimitedTrial(gameVersion, p.Variant)) && p.TryGet(packet.Opcode, out _));
            if (!candidate) return;
            while (preRoll.TryPeek(out var old) && old.Time < packet.Time - PreRollWindow) preRoll.Dequeue();
            if (preRoll.Count >= 128) preRoll.Dequeue();
            preRoll.Enqueue(packet);
            if (!TryAdmit(packet, out var captured)) return;
            if (captureEnabled) Enqueue(queue, captured);
            if (publicEnabled) Enqueue(publicQueue, captured);
        }
    }

    private void ReplayPreRoll(ConcurrentQueue<CapturedMahjongPacket> target)
    {
        var cutoff = DateTimeOffset.UtcNow - PreRollWindow;
        foreach (var packet in preRoll)
            if (packet.Time >= cutoff && TryAdmit(packet, out var captured)) Enqueue(target, captured);
    }

    private bool TryAdmit(RawReceivedPacket packet, out CapturedMahjongPacket captured)
    {
        captured = default;
        var profile = activeProfile;
        if (profile is null || !profile.TryGet(packet.Opcode, out var spec) || spec is null) return false;
        if (packet.Transport != "deucalion" || packet.Payload.Length != spec.PayloadLength)
        { MarkTransportGap(); return false; }
        captured = new(spec.MessageId, packet.Opcode, packet.Time, packet.Payload);
        return true;
    }

    private void Enqueue(ConcurrentQueue<CapturedMahjongPacket> target, CapturedMahjongPacket packet)
    {
        if (target.Count >= MaxQueuedPackets) { MarkTransportGap(); return; }
        target.Enqueue(packet);
    }

    internal void MarkTransportGap() => Interlocked.Increment(ref droppedPackets);

    private readonly record struct PacketSpec(int MessageId, int PayloadLength);
}
