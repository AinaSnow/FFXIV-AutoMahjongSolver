using Dalamud.Plugin.Services;
using Mahjong.Plugin.Dalamud.Mortal;

namespace Mahjong.Plugin.Dalamud.Logging;

/// <summary>Framework owner of the shared receive subscriber and optional table-triggered recorder.</summary>
public sealed class DebugPacketLogger : IDisposable
{
    private readonly DeucalionCapture source;
    private readonly MahjongNetworkCapture network;
    private readonly AutoPacketRecorder recorder;
    private readonly IFramework framework;
    private readonly IPluginLog? log;
    private string? warnedPath, warnedHookError;
    private readonly Func<bool> enabled, present;
    private readonly Func<MatchArchiveEnvironment> environment;
    private DebugPacketSession? acknowledgedCollectionFailure;
    // An explicit restart can retry after an old file failure; active recordings are never excused.
    public void AcknowledgeCollectionFailure()
    {
        if (!present()) acknowledgedCollectionFailure = recorder.Latest;
    }
    public bool ReadyForCollection => enabled() && source.IsEnabled && CollectionError is null;
    public string? CollectionError =>
        (source.Status.StartsWith("Deucalion unavailable", StringComparison.Ordinal) ? source.Status : null) ??
        (ReferenceEquals(recorder.Latest, acknowledgedCollectionFailure) ? null : recorder.Latest?.Error ??
            (recorder.Latest?.Status == "size-limit" ? "Packet capture reached its 64 MiB limit; collection stopped" : null));
    public string DirectoryPath => recorder.DirectoryPath;
    public string? CurrentPath => recorder.Latest?.Path;
    public long Packets => recorder.Latest?.Written ?? 0;
    public long Dropped => recorder.Latest?.Dropped ?? 0;
    public long Rejected => source.RejectedPackets;
    public string Status => !enabled() ? "Off" : recorder.Latest?.Error ??
        (!source.IsEnabled ? source.Status :
        recorder.IsRecording ? recorder.Latest!.Progress : present() && recorder.Latest is {} last ? last.Status : "Armed; waiting for mahjong table");
    private bool disposed, recordingEnabled, tablePresent;

    public DebugPacketLogger(string pluginDirectory, MahjongNetworkCapture network, IFramework framework, string configDirectory,
        Func<bool> enabled, Func<bool> present, Func<MatchArchiveEnvironment> environment, IPluginLog? log = null)
    {
        this.log = log;
        this.network = network;
        this.framework=framework; this.enabled=enabled; this.present=present; this.environment=environment;
        recorder = new AutoPacketRecorder(configDirectory);
        source = new DeucalionCapture(pluginDirectory);
        source.Received += Record;
        source.Rejected += Reject;
        framework.Update += Update;
    }

    private void Update(IFramework _)
    {
        if (disposed) return;
        bool active = enabled();
        network.RefreshProfile();
        source.SetEnabled(active || network.HasAdmittedBuild);
        network.TransportReady = source.IsEnabled;
        network.TransportStatus = source.Status;
        // Arm pre-roll while connecting so the first received packet is retained.
        bool captureEnabled = active && !source.Status.StartsWith("Deucalion unavailable",StringComparison.Ordinal);
        bool visible = present();
        if (captureEnabled != recordingEnabled || visible != tablePresent)
        {
            recorder.Update(captureEnabled, visible, environment());
            recordingEnabled = captureEnabled;
            tablePresent = visible;
        }
        if (!active) { warnedHookError = null; return; }
        if (source.Status is { } error && error.StartsWith("Deucalion unavailable",StringComparison.Ordinal) && error != warnedHookError)
        {
            warnedHookError = error;
            log?.Warning($"[PacketDebug] {error}");
        }
        if (recorder.IsRecording && recorder.Latest is { } session
            && session.Written == 0 && session.Progress.StartsWith("Capture", StringComparison.Ordinal)
            && warnedPath != session.Path)
        {
            warnedPath = session.Path;
            log?.Warning($"[PacketDebug] {session.Progress}. Saved=0; this capture cannot validate the protocol. File: {session.Path}");
        }
    }

    private void Record(RawReceivedPacket packet) { recorder.Record(packet); network.Record(packet); }
    private void Reject(PacketReadFailure failure) { recorder.Reject(failure); network.MarkTransportGap(); }

    public void Dispose()
    {
        if (disposed) return;
        disposed=true;
        framework.Update -= Update;
        source.SetEnabled(false);
        source.Received -= Record;
        source.Rejected -= Reject;
        recorder.Dispose();
        source.Dispose();
    }
}
