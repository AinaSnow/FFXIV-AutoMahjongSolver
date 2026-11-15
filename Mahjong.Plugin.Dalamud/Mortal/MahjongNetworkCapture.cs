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
    private MahjongProtocolProfile[] profiles;
    private readonly MahjongProtocolProfile[] bundledProfiles;
    private readonly string? learnedDirectory;
    private readonly Func<bool> learningEnabled;
    private readonly IPluginLog? log;
    // Same-length traffic seen at the table while this build has no profile; replayed on leaving the table.
    private readonly List<(DateTimeOffset Time, DiscoveryPacket Packet)> learningBuffer = [];
    private bool learningAtTable;
    // The addon (and so the variant) is gone by the time the table closes; keep the one seen while seated.
    private string? learningVariant;
    private static readonly TimeSpan LearningPreRoll = TimeSpan.FromSeconds(10);
    private readonly List<int[]> tableScores = [];
    private const int MaxLearningPackets = 20000;
    private volatile bool learningRunning;
    private string learningStatus = "Not needed";
    public string LearningStatus
    {
        get => LearningWanted && learningStatus == "Not needed"
            ? "Waiting for a mahjong table; play one match by hand with Mortal enabled" : learningStatus;
        private set => learningStatus = value;
    }
    /// <summary>Any bundled or learned profile exists for this exact build, regardless of the trial switch.</summary>
    public bool HasProfileForVersion => profiles.Any(p => p.GameVersion == gameVersion);
    public bool LearningWanted => learningEnabled() && !HasProfileForVersion && gameVersion is not null;
    /// <summary>A verified or limited-trial-scoped profile exists for this build and variant. Read-only; admission is unchanged.</summary>
    public bool HasUsableProfileFor(string? variant) =>
        profiles.Any(p => p.Matches(gameVersion, variant) || p.MatchesLimitedTrial(gameVersion, variant));
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
        : LimitedTrialActive ? $"Limited trial: East rounds, score-derived riichi pool, combined riichi, opponent kans, self closed/ordinary added kan/dora; self open/consecutive added kan unverified; {TransportStatus}"
        : $"No admitted protocol for {gameVersion ?? "unknown build"}/{variantAccessor() ?? "unknown variant"}";
    public bool TryDequeuePublic(out CapturedMahjongPacket packet) => publicQueue.TryDequeue(out packet);
    private volatile bool disposed;
    private long droppedPackets;

    public MahjongNetworkCapture(IPluginLog log,
        Func<string?>? variantAccessor = null, string? profilesDirectory = null, Func<bool>? trialEnabled = null,
        string? learnedDirectory = null, Func<bool>? learningEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        this.variantAccessor = variantAccessor ?? (() => null);
        this.trialEnabled = trialEnabled ?? (() => false);
        string? processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        string versionPath = Path.Combine(processDirectory ?? "", "ffxivgame.ver");
        try { gameVersion = File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : null; }
        catch (IOException) { gameVersion = null; }
        this.log = log;
        this.learningEnabled = learningEnabled ?? (() => false);
        this.learnedDirectory = learnedDirectory;
        try { bundledProfiles = MahjongProtocolProfile.LoadDirectory(profilesDirectory ?? ""); }
        catch (Exception ex) { bundledProfiles = []; log.Warning(ex, "[Mahjong] Invalid protocol profiles; network capture disabled."); }
        profiles = [.. bundledProfiles, .. LoadLearned()];
        if (profiles.Length > bundledProfiles.Length) LearningStatus = "Using auto-learned protocol for this build";
        RefreshProfile();
        if (!ProtocolAdmitted) log.Warning($"[Mahjong] {ProtocolStatus}; UI-only until an admitted table is detected.");
    }

    internal MahjongNetworkCapture(string version, Func<string?> variant, MahjongProtocolProfile[] profiles, Func<bool>? trialEnabled = null,
        Func<bool>? learningEnabled = null, string? learnedDirectory = null)
    {
        gameVersion = version; variantAccessor = variant; this.profiles = profiles; bundledProfiles = profiles;
        this.trialEnabled = trialEnabled ?? (() => false); this.learningEnabled = learningEnabled ?? (() => false);
        this.learnedDirectory = learnedDirectory; RefreshProfile();
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
        if (disposed) return;
        if (!HasProfileForVersion)
        {
            RecordForLearning(packet);
            return;
        }
        if (!HasAdmittedBuild) return;
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

    internal void ForgetTableHistory()
    {
        lock (admissionLock)
        {
            preRoll.Clear();
            while (queue.TryDequeue(out _)) { }
            while (publicQueue.TryDequeue(out _)) { }
        }
    }

    internal void MarkTransportGap() => Interlocked.Increment(ref droppedPackets);

    private void RecordForLearning(RawReceivedPacket packet)
    {
        if (!LearningWanted || learningRunning || packet.Transport != "deucalion"
            || !ProtocolDiscovery.IsCandidateLength(packet.Payload.Length)) return;
        lock (learningBuffer)
        {
            // Away from the table keep only a short pre-roll: the match-start packet precedes the addon.
            if (!learningAtTable) learningBuffer.RemoveAll(e => e.Time < packet.Time - LearningPreRoll);
            if (learningBuffer.Count >= MaxLearningPackets) return;
            learningBuffer.Add((packet.Time, new DiscoveryPacket(packet.Opcode, packet.Payload.ToArray())));
            if (learningAtTable) LearningStatus = $"Learning protocol for {gameVersion}: {learningBuffer.Count} candidate packets this match";
        }
    }

    /// <summary>The table became visible: keep the short pre-roll, then collect the whole match.</summary>
    internal void BeginLearningSession(DateTimeOffset now)
    {
        lock (learningBuffer)
        {
            learningAtTable = true;
            learningVariant = variantAccessor();
            learningBuffer.RemoveAll(e => e.Time < now - LearningPreRoll);
            tableScores.Clear();
        }
    }

    /// <summary>Independent ground truth from the client's own table; distinct snapshots only.</summary>
    internal void ObserveTableScores(int[]? scores)
    {
        if (!LearningWanted) return;
        lock (learningBuffer)
        {
            if (variantAccessor() is { } variant) learningVariant = variant;
            if (scores is not null && tableScores.Count < 256 && !tableScores.Any(s => s.SequenceEqual(scores))) tableScores.Add(scores);
        }
    }

    /// <summary>Called when the table closes. Learning runs off the framework thread; a result applies from the next match.</summary>
    internal Task FinishLearningSession()
    {
        DiscoveryPacket[] packets;
        int[][] scores;
        string? variant;
        lock (learningBuffer)
        {
            bool wasAtTable = learningAtTable;
            learningAtTable = false;
            if (!wasAtTable || !LearningWanted || learningRunning) return Task.CompletedTask;
            packets = [.. learningBuffer.Select(e => e.Packet)];
            scores = [.. tableScores];
            variant = learningVariant ?? variantAccessor();
            learningBuffer.Clear();
            tableScores.Clear();
            learningVariant = null;
        }
        if (packets.Length == 0) return Task.CompletedTask;
        learningRunning = true;
        LearningStatus = $"Analysing {packets.Length} packets from the last match";
        return Task.Run(() =>
        {
            try { ApplyLearning(packets, scores, variant); }
            catch (Exception ex) { LearningStatus = $"Learning failed: {ex.Message}"; log?.Warning(ex, "[Mahjong] Protocol learning failed."); }
            finally { learningRunning = false; }
        });
    }

    private void ApplyLearning(DiscoveryPacket[] packets, int[][] scores, string? variant)
    {
        if (variant is null || gameVersion is null) { LearningStatus = "Learning skipped: client variant unknown"; return; }
        var result = ProtocolDiscovery.Discover(packets, observedHandScores: scores);
        string tableCheck = scores.Length > 0 ? $"table-score check on {scores.Length} snapshots" : "table state unavailable";
        if (!result.Success)
        {
            LearningStatus = $"Not learned yet ({result.Reason}); play another match";
            log?.Information($"[Mahjong] Protocol learning for {gameVersion}: {result.Reason} ({result.CombinationsTried} combinations, {tableCheck}).");
            return;
        }
        var s = result.Score;
        var profile = new MahjongProtocolProfile(gameVersion, variant, Verified: false,
            Evidence: $"Auto-learned {DateTimeOffset.UtcNow:yyyy-MM-dd}: unique mapping of {result.CombinationsTried} combinations; "
                + $"{s.CompleteHands} clean hands, {s.DrawDiscardEvents} draw/discard events; {tableCheck}.",
            Packets: new Dictionary<string, MahjongPacketSpec>(result.Packets),
            LimitedTrialScope: DomanMortalTrialGuard.Scope,
            LimitedTrialEvidence: "Auto-learned opcode remap of the 2026.09.15 limited-trial decoder; payload formats unchanged.");
        if (learnedDirectory is not null)
        {
            Directory.CreateDirectory(learnedDirectory);
            File.WriteAllText(Path.Combine(learnedDirectory, $"{gameVersion}-{variant}.json"),
                System.Text.Json.JsonSerializer.Serialize(profile, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        lock (admissionLock) profiles = [.. bundledProfiles, profile];
        LearningStatus = $"Learned protocol for {gameVersion}: {string.Join(", ", result.Packets.Select(kv => $"{kv.Key}={kv.Value.MessageId}"))}";
        log?.Information($"[Mahjong] {LearningStatus}. {profile.Evidence}");
    }

    private MahjongProtocolProfile[] LoadLearned()
    {
        if (learnedDirectory is null || gameVersion is null) return [];
        // Bundled profiles always win; learned files only cover builds without one.
        if (bundledProfiles.Any(p => p.GameVersion == gameVersion)) return [];
        try { return MahjongProtocolProfile.LoadDirectory(learnedDirectory).Where(p => p.GameVersion == gameVersion).ToArray(); }
        catch (Exception ex) { log?.Warning(ex, "[Mahjong] Ignoring unreadable learned protocol profiles."); return []; }
    }

    private readonly record struct PacketSpec(int MessageId, int PayloadLength);
}
