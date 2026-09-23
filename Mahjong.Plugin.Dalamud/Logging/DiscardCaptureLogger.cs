using System;
using System.IO;
using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Logging;

public sealed class DiscardCaptureLogger : IDisposable
{
    private readonly IDiscardCapture capture;
    private readonly string logPath;
    private bool disposed;
    private readonly BackgroundIoWorker io = new();
    public Task FlushAsync() => io.FlushAsync();

    public string LogPath => logPath;

    public DiscardCaptureLogger(IDiscardCapture capture, string pluginConfigDirectory)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentException.ThrowIfNullOrEmpty(pluginConfigDirectory);
        this.capture = capture;

        Directory.CreateDirectory(pluginConfigDirectory);
        logPath = Path.Combine(pluginConfigDirectory, "emj-discards.log");

        capture.DiscardObserved += OnDiscard;
    }

    private void OnDiscard(DiscardEvent evt)
    {
        if (disposed)
            return;
        string seat = evt.Seat >= 0 ? evt.Seat.ToString() : "?";
        string line = $"{evt.ObservedAtUtc:o}  seq={evt.SequenceNumber}  strategy={capture.StrategyName}  seat={seat}  tile_id={evt.Tile.Id} ({evt.Tile})";
        io.TryEnqueue(() => File.AppendAllText(logPath, line + Environment.NewLine));
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        capture.DiscardObserved -= OnDiscard;
        io.Dispose();
    }
}
