using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Mahjong.Plugin.Dalamud.Logging;

/// <summary>One bounded producer queue and one file owner. Close drains accepted packets before the footer.</summary>
public sealed class DebugPacketSession
{
    private readonly Channel<RawReceivedPacket> queue;
    private readonly object gate = new();
    private readonly long maxBytes;
    private readonly long startedAt = Stopwatch.GetTimestamp();
    public string Progress => DescribeProgress(Written, Rejected, Stopwatch.GetElapsedTime(startedAt));

    internal static string DescribeProgress(long written, long rejected, TimeSpan elapsed) =>
        written > 0 ? (rejected > 0 ? "Recording with rejected packets" : "Recording") :
        rejected > 0 ? "Capture failed: packet headers rejected" :
        elapsed >= TimeSpan.FromSeconds(5) ? "Capture stalled: no packets observed" : "Waiting for first packet";
    private bool accepting = true;
    private string reason = "recording";
    private long written, dropped, rejected, bytes;
    private string? error, lastRejection;
    public string Path { get; }
    public Task Completion { get; }
    public long Written => Interlocked.Read(ref written);
    public long Dropped => Interlocked.Read(ref dropped);
    public long Rejected => Interlocked.Read(ref rejected);
    public long Bytes => Interlocked.Read(ref bytes);
    public string? Error => Volatile.Read(ref error);
    public string Status { get { lock (gate) return error is not null ? "disk-error" : reason; } }
    public bool Accepting { get { lock (gate) return accepting; } }

    public DebugPacketSession(string path, MatchArchiveEnvironment environment, int capacity = 512,
        long maxBytes = 64L << 20, Func<string, TextWriter>? writerFactory = null)
    {
        if (maxBytes < 8192) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        Path = path; this.maxBytes = maxBytes;
        queue = Channel.CreateBounded<RawReceivedPacket>(new BoundedChannelOptions(capacity)
            { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        Completion = Task.Run(() => WriteAsync(environment, writerFactory ?? OpenFile));
    }

    public bool TryRecord(RawReceivedPacket packet)
    {
        lock (gate)
        {
            if (!accepting) return false;
            if (queue.Writer.TryWrite(packet)) return true;
            Interlocked.Increment(ref dropped);
            return false;
        }
    }

    public void Reject(string reason = "invalid-segment-header")
    {
        lock (gate) if (accepting) { Interlocked.Increment(ref rejected); lastRejection = reason; }
    }

    public void Close(string endReason)
    {
        lock (gate)
        {
            if (!accepting) return;
            accepting = false; reason = endReason;
            queue.Writer.TryComplete();
        }
    }

    private static TextWriter OpenFile(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        return new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
    }

    private async Task WriteAsync(MatchArchiveEnvironment environment, Func<string, TextWriter> factory)
    {
        bool inFlight = false;
        try
        {
            using var writer = factory(Path);
            await WriteLine(writer, JsonSerializer.Serialize(new { e="capture-start", schema_version=1,
                t=DateTimeOffset.UtcNow, environment, capture="raw-zone-receive", hook_mode="function-entry", protocol_inference=false,
                pre_roll_seconds=2, pre_roll_max_packets=256, max_file_bytes=maxBytes,
                opening_boundary_verified=false }));
            bool limited = false;
            int sinceFlush = 0;
            await foreach (var packet in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (limited) { Interlocked.Increment(ref dropped); continue; }
                string line = JsonSerializer.Serialize(new { e="raw-packet", t=packet.Time, sequence=Written+1,
                    opcode=$"0x{packet.Opcode:X4}", segment_length=packet.SegmentLength,
                    payload_length=packet.Payload.Length, payload_hex=Convert.ToHexString(packet.Payload) });
                if (Bytes + Encoding.UTF8.GetByteCount(line) + 4096 > maxBytes)
                {
                    limited = true; Interlocked.Increment(ref dropped); Close("size-limit");
                    lock (gate) reason = "size-limit";
                    continue;
                }
                inFlight = true;
                await WriteLine(writer,line);
                Interlocked.Increment(ref written);
                inFlight = false;
                // Keep sparse captures visible and useful even if the game terminates unexpectedly.
                if (++sinceFlush >= 32 || queue.Reader.Count == 0) { await writer.FlushAsync(); sinceFlush = 0; }
            }
            await WriteLine(writer,JsonSerializer.Serialize(new { e="capture-end", t=DateTimeOffset.UtcNow,
                reason, packets=Written, dropped=Dropped, rejected=Rejected, last_rejection=lastRejection,
                no_packets=Written==0, stream_complete=Written>0 && Dropped==0 && Rejected==0 && !limited }));
            await writer.FlushAsync();
        }
        catch (Exception ex)
        {
            Volatile.Write(ref error, $"{ex.GetType().Name}: {ex.Message}");
            Close("disk-error");
            if (inFlight) Interlocked.Increment(ref dropped);
            while (queue.Reader.TryRead(out _)) Interlocked.Increment(ref dropped);
        }
    }

    private async Task WriteLine(TextWriter writer,string line)
    {
        await writer.WriteLineAsync(line);
        Interlocked.Add(ref bytes, Encoding.UTF8.GetByteCount(line)+Environment.NewLine.Length);
    }
}
