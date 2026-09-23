using System.Diagnostics;

namespace Mahjong.Plugin.Dalamud.Logging;

/// <summary>Armed pre-roll in memory; one automatically sealed file per observed table session.</summary>
public sealed class AutoPacketRecorder : IDisposable
{
    private readonly object gate = new();
    private readonly Queue<RawReceivedPacket> preRoll = new();
    private readonly Func<string, MatchArchiveEnvironment, DebugPacketSession> factory;
    private readonly Func<long> timestamp;
    private readonly List<Task> closing = [];
    private DebugPacketSession? current;
    private DebugPacketSession? latest;
    private bool armed, disposed;
    private int preRollBytes;
    private long preRollDropped, preRollRejected, lastPreRollFailure;
    private const int MaxPreRollBytes = 1 << 20;
    public string DirectoryPath { get; }
    public DebugPacketSession? Latest => Volatile.Read(ref latest);
    public bool IsRecording { get { lock(gate) return current is {Accepting:true}; } }

    public AutoPacketRecorder(string pluginDirectory,
        Func<string, MatchArchiveEnvironment, DebugPacketSession>? factory = null, Func<long>? timestamp = null)
    {
        DirectoryPath = System.IO.Path.Combine(pluginDirectory,"debug-packets");
        this.factory = factory ?? ((path, environment) => new DebugPacketSession(path,environment));
        this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
    }

    public void Update(bool enabled, bool present, MatchArchiveEnvironment environment)
    {
        lock (gate)
        {
            if (disposed) return;
            armed = enabled;
            closing.RemoveAll(t=>t.IsCompleted);
            if (!enabled || !present)
            {
                Stop(enabled ? "left-table" : "disabled");
                if (!enabled) ClearPreRoll();
                return;
            }
            if (current is not null) return;
            string file = $"capture-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.ndjson";
            current = factory(System.IO.Path.Combine(DirectoryPath,file),environment);
            Volatile.Write(ref latest,current);
            TrimPreRoll();
            // A capped or rejected pre-roll means the opening boundary cannot be certified complete.
            if (preRollDropped > 0 || preRollRejected > 0) current.Reject("pre-roll-incomplete");
            while (preRoll.TryDequeue(out var packet)) current.TryRecord(packet);
            ClearPreRoll();
        }
    }

    public void Record(RawReceivedPacket packet)
    {
        lock (gate)
        {
            if (!armed || disposed) return;
            if (current is not null) { current.TryRecord(packet); return; }
            preRoll.Enqueue(packet); preRollBytes += packet.Payload.Length;
            TrimPreRoll();
        }
    }

    public void Reject(string reason) => Reject(new PacketReadFailure(reason));

    public void Reject(PacketReadFailure failure)
    {
        lock(gate)
        {
            if (!armed || disposed) return;
            if (current is not null) current.Reject(failure);
            else { preRollRejected++; lastPreRollFailure = timestamp(); }
        }
    }

    private void TrimPreRoll()
    {
        long now = timestamp();
        while (preRoll.TryPeek(out var packet))
        {
            bool expired = Stopwatch.GetElapsedTime(packet.Timestamp, now) > TimeSpan.FromSeconds(2);
            if (!expired && preRoll.Count <= 256 && preRollBytes <= MaxPreRollBytes) break;
            preRoll.Dequeue(); preRollBytes -= packet.Payload.Length;
            if (!expired) { preRollDropped++; lastPreRollFailure = now; }
        }
        if (Stopwatch.GetElapsedTime(lastPreRollFailure, now) > TimeSpan.FromSeconds(2))
            { preRollDropped = 0; preRollRejected = 0; }
    }

    private void ClearPreRoll() { preRoll.Clear(); preRollBytes=0; preRollDropped=0; preRollRejected=0; }
    private void Stop(string reason)
    {
        if (current is null) return;
        current.Close(reason); closing.Add(current.Completion); current = null;
    }
    public Task FlushClosedAsync() { lock(gate) return Task.WhenAll(closing); }
    public void Dispose()
    {
        lock(gate) { if (disposed) return; disposed=true; armed=false; Stop("unload"); ClearPreRoll(); }
    }
}
