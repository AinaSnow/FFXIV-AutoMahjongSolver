using Dalamud.Plugin.Services;

namespace Mahjong.Plugin.Dalamud.Logging;

/// <summary>Framework owner of the optional raw receive hook and table-triggered recorder.</summary>
public sealed class DebugPacketLogger : IDisposable
{
    private readonly RawPacketCapture source;
    private readonly AutoPacketRecorder recorder;
    private readonly IFramework framework;
    private readonly Func<bool> enabled, present;
    private readonly Func<MatchArchiveEnvironment> environment;
    public string DirectoryPath => recorder.DirectoryPath;
    public string? CurrentPath => recorder.Latest?.Path;
    public long Packets => recorder.Latest?.Written ?? 0;
    public long Dropped => recorder.Latest?.Dropped ?? 0;
    public long Rejected => source.RejectedPackets;
    public string Status => !enabled() ? "Off" : source.Error ?? recorder.Latest?.Error ??
        (!source.IsEnabled ? "Waiting for capture hook" :
        recorder.IsRecording ? "Recording" : present() && recorder.Latest is {} last ? last.Status : "Armed; waiting for mahjong table");
    private bool disposed, recordingEnabled, tablePresent;

    public DebugPacketLogger(IGameInteropProvider interop, IFramework framework, string configDirectory,
        Func<bool> enabled, Func<bool> present, Func<MatchArchiveEnvironment> environment)
    {
        this.framework=framework; this.enabled=enabled; this.present=present; this.environment=environment;
        recorder = new AutoPacketRecorder(configDirectory);
        source = new RawPacketCapture(interop);
        source.Received += recorder.Record;
        source.Rejected += recorder.Reject;
        framework.Update += Update;
    }

    private void Update(IFramework _)
    {
        if (disposed) return;
        bool active = enabled();
        source.SetEnabled(active);
        bool captureEnabled = active && source.IsEnabled;
        bool visible = present();
        if (captureEnabled == recordingEnabled && visible == tablePresent) return;
        recorder.Update(captureEnabled, visible, environment());
        recordingEnabled = captureEnabled;
        tablePresent = visible;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed=true;
        framework.Update -= Update;
        source.SetEnabled(false);
        source.Received -= recorder.Record;
        source.Rejected -= recorder.Reject;
        recorder.Dispose();
        source.Dispose();
    }
}
