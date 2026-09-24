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
            try
            {
                if (capture.LimitedTrialActive && DomanMortalTrialGuard.RejectPacket(packet.MessageId, packet.Payload) is { } packetReason)
                    throw new InvalidDataException(packetReason);
                foreach (var evt in decoder.Process(packet.MessageId, packet.Payload))
                {
                    if (capture.LimitedTrialActive && evt is MjaiStartKyoku start
                        && DomanMortalTrialGuard.RejectStart(start) is { } startReason)
                        throw new InvalidDataException(startReason);
                    state.Apply(evt, knownCounters: false);
                }
            }
            catch (Exception ex)
            {
                state.Invalidate("public event rejected");
                log.Warning(ex, "[Mahjong] Public state unavailable until the next verified hand.");
            }
        }
    }
    public StateSnapshot Merge(StateSnapshot snapshot) =>
        capture.ProtocolAdmitted && state.Complete ? state.Merge(snapshot) : snapshot;
    public void Reset() { state.Reset(); decoder = new(); while (capture.TryDequeuePublic(out _)) { } }
    public void Dispose() { framework.Update -= Update; capture.PublicCaptureEnabled = false; Reset(); }
}
