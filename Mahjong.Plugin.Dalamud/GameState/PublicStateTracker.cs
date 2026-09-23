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
    private long dropped;
    private bool wasPresent;
    public PublicStateTracker(MahjongNetworkCapture capture, IFramework framework, AddonEmjReader reader,
        IPluginLog log, Action<CapturedMahjongPacket>? observer = null)
    {
        this.capture = capture; this.framework = framework; this.reader = reader; this.log = log; this.observer = observer;
        framework.Update += Update;
    }
    private void Update(IFramework _)
    {
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
                foreach (var evt in decoder.Process(packet.MessageId, packet.Payload))
                    state.Apply(evt, knownCounters: false);
            }
            catch (Exception ex)
            {
                state.Invalidate("public event rejected");
                log.Warning(ex, "[Mahjong] Public state unavailable until the next verified hand.");
            }
        }
    }
    public StateSnapshot Merge(StateSnapshot snapshot) => capture.ProtocolVerified ? state.Merge(snapshot) : snapshot;
    public void Reset() { state.Reset(); decoder = new(); while (capture.TryDequeuePublic(out _)) { } }
    public void Dispose() { framework.Update -= Update; capture.PublicCaptureEnabled = false; Reset(); }
}
