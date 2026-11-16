using Dalamud.Plugin.Services;
using Mahjong.Plugin.Dalamud.Mortal;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Dalamud.GameState;

/// <summary>Framework-thread owner of the network public history, independent of model selection.</summary>
public sealed class PublicStateTracker : IDisposable
{
    private readonly MahjongNetworkCapture capture;
    private readonly IFramework framework;
    private readonly AddonEmjReader reader;
    private readonly IPluginLog log;
    private readonly Action<CapturedMahjongPacket>? observer;
    private MahjongPacketMjaiDecoder decoder = new();
    private readonly PublicStateReducer state = new();
    private long dropped, admissionGeneration = -1;
    private bool wasPresent;
    public bool AwaitingNextHand => capture.ProtocolAdmitted && state.HandEnded;
    public event Action? HandEnded;
    /// <summary>
    /// Every accepted server event, after the whole packet decoded cleanly. A result-packet
    /// end_kyoku carries the decoder's cross-checked hand result (null otherwise). Observers are
    /// isolated: their failures never reset the public state.
    /// </summary>
    public event Action<IMjaiEvent, ServerHandResult?>? ServerEventDecoded;
    public PublicStateTracker(MahjongNetworkCapture capture, IFramework framework, AddonEmjReader reader,
        IPluginLog log, Action<CapturedMahjongPacket>? observer = null)
    {
        this.capture = capture; this.framework = framework; this.reader = reader; this.log = log; this.observer = observer;
        framework.Update += Update;
    }
    private void Update(IFramework _)
    {
        if (admissionGeneration != capture.AdmissionGeneration)
        {
            state.Reset(); decoder = new();
            admissionGeneration = capture.AdmissionGeneration;
        }
        bool present = reader.LastObservation.Present;
        capture.PublicCaptureEnabled = present;
        if (!present && wasPresent) Reset();
        wasPresent = present;
        if (capture.DroppedPackets != dropped)
        {
            dropped = capture.DroppedPackets;
            state.Invalidate("receive queue overflow; waiting for the next hand");
        }
        int budget = 128;
        while (budget-- > 0 && capture.TryDequeuePublic(out var packet))
        {
            try { observer?.Invoke(packet); } catch (Exception ex) { log.Warning(ex, "[Mahjong] Packet archive rejected write."); }
            IReadOnlyList<IMjaiEvent> accepted = [];
            ServerHandResult? handResult = null;
            try
            {
                if (capture.LimitedTrialActive && DomanMortalTrialGuard.RejectPacket(packet.MessageId, packet.Payload) is { } packetReason)
                    throw new InvalidDataException(packetReason);
                var events = decoder.Process(packet.MessageId, packet.Payload);
                foreach (var evt in events)
                {
                    if (capture.LimitedTrialActive && evt is MjaiStartKyoku start
                        && DomanMortalTrialGuard.RejectStart(start) is { } startReason)
                        throw new InvalidDataException(startReason);
                    bool wasEnded = state.HandEnded;
                    state.Apply(evt, knownCounters: false);
                    if (!wasEnded && state.HandEnded) HandEnded?.Invoke();
                }
                accepted = events;
                handResult = decoder.LastHandResult;
            }
            catch (Exception ex)
            {
                decoder = new MahjongPacketMjaiDecoder();
                state.Reset();
                state.Invalidate("public event rejected");
                log.Warning(ex, "[Mahjong] Public state unavailable until the next verified hand.");
            }
            foreach (var evt in accepted)
            {
                try { ServerEventDecoded?.Invoke(evt, evt is MjaiEndKyoku ? handResult : null); }
                catch (Exception ex) { log.Warning(ex, "[Mahjong] Server event observer failed."); }
            }
        }
    }
    public StateSnapshot Merge(StateSnapshot snapshot) =>
        capture.ProtocolAdmitted && (state.Complete || state.HandEnded) ? state.Merge(snapshot) : snapshot;
    public void Reset() { state.Reset(); decoder = new(); while (capture.TryDequeuePublic(out _)) { } }
    public void Dispose() { framework.Update -= Update; capture.PublicCaptureEnabled = false; Reset(); }
}
