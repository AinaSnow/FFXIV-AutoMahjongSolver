using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Dalamud.Tests.Stubs;

namespace Mahjong.Plugin.Dalamud.Tests;

public class DebugPacketLoggerTests
{
    private static readonly MatchArchiveEnvironment EnvironmentInfo = new("2026.09.15.0000.0000", "Emj", false, "unverified", "test-build");
    private static RawReceivedPacket Packet(ushort opcode = 0xBEEF, long? timestamp = null, int bytes = 4) =>
        new(DateTimeOffset.UtcNow, timestamp ?? Stopwatch.GetTimestamp(), opcode, bytes + 32, new byte[bytes]);
    private static JsonElement[] Read(string path) => File.ReadAllLines(path).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
    private static async Task Finish(DebugPacketSession session) => await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    private static byte[] Segment()
    {
        var data = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 123);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16), 0x14);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(18), 0xBEEF);
        data[32] = 0x42;
        return data;
    }

    [Fact]
    public void Copies_declared_payload_without_an_opcode_map_and_rejects_unreadable_memory()
    {
        var data = Segment();
        nint pointer = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, pointer, data.Length);
            Assert.True(RawPacketCapture.TryCopy(pointer + 16, 123, out var packet, out _));
            Assert.Equal((ushort)0xBEEF, packet!.Opcode);
            Assert.Equal(new byte[] { 0x42, 0, 0, 0 }, packet.Payload);
            Assert.False(RawPacketCapture.TryCopy(pointer + 16, 999, out _, out _));
        }
        finally { Marshal.FreeHGlobal(pointer); }
        Assert.False(RawPacketCapture.TryCopy(0, 123, out _, out _));
        Assert.False(RawPacketCapture.TryCopy(17, 123, out _, out _));
    }

    [Theory]
    [InlineData(0, 31)]
    [InlineData(0, 65537)]
    [InlineData(8, 456)]
    [InlineData(12, 7)]
    [InlineData(16, 0)]
    public void Changed_transport_headers_fail_closed(int offset, uint value)
    {
        var data = Segment();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
        Assert.False(RawPacketCapture.TryReadHeader(data, 123, out _, out _));
        Assert.False(RawPacketCapture.TryReadHeader(data.AsSpan(0, 31), 123, out _, out _));
    }

    [Fact]
    public async Task Close_drains_in_order_before_footer_and_preserves_unverified_environment()
    {
        using var temp = new TempDir();
        var session = new DebugPacketSession(Path.Combine(temp.Path, "capture.ndjson"), EnvironmentInfo);
        Assert.True(session.TryRecord(Packet(1)));
        Assert.True(session.TryRecord(Packet(2)));
        session.Close("left-table");
        Assert.False(session.TryRecord(Packet(3)));
        await Finish(session);
        var lines = Read(session.Path);
        Assert.Equal(4, lines.Length);
        Assert.False(lines[0].GetProperty("protocol_inference").GetBoolean());
        Assert.False(lines[0].GetProperty("opening_boundary_verified").GetBoolean());
        Assert.Equal(EnvironmentInfo.GameVersion, lines[0].GetProperty("environment").GetProperty("game_version").GetString());
        Assert.Equal("0x0001", lines[1].GetProperty("opcode").GetString());
        Assert.Equal(2, lines[2].GetProperty("sequence").GetInt32());
        Assert.Equal("capture-end", lines[3].GetProperty("e").GetString());
        Assert.True(lines[3].GetProperty("stream_complete").GetBoolean());
    }

    [Fact]
    public async Task Blocked_disk_has_bounded_nonblocking_admission_and_visible_loss()
    {
        using var temp = new TempDir();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringWriter();
        var session = new DebugPacketSession(Path.Combine(temp.Path,"capture.ndjson"), EnvironmentInfo, capacity: 1,
            writerFactory: _ => { started.SetResult(); release.Wait(); return output; });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(session.TryRecord(Packet()));
            Assert.False(session.TryRecord(Packet()));
            session.Reject("unreadable-segment");
            session.Close("disabled");
            Assert.False(session.Completion.IsCompleted);
        }
        finally { release.Set(); }
        await Finish(session);
        var footer = JsonSerializer.Deserialize<JsonElement>(output.ToString().Trim().Split('\n')[^1]);
        Assert.Equal(1, footer.GetProperty("packets").GetInt32());
        Assert.Equal(1, footer.GetProperty("dropped").GetInt32());
        Assert.Equal(1, footer.GetProperty("rejected").GetInt32());
        Assert.Equal("unreadable-segment", footer.GetProperty("last_rejection").GetString());
        Assert.False(footer.GetProperty("stream_complete").GetBoolean());
    }

    [Fact]
    public async Task Disk_failure_closes_session_and_never_escapes_to_producer()
    {
        var session = new DebugPacketSession("ignored", EnvironmentInfo, writerFactory: _ => throw new IOException("disk unavailable"));
        session.TryRecord(Packet());
        await Finish(session);
        Assert.Equal("disk-error", session.Status);
        Assert.Contains("disk unavailable", session.Error);
        Assert.False(session.TryRecord(Packet()));
    }

    private sealed class InterruptedWriter : StringWriter
    {
        public override Task WriteLineAsync(string? value) => value?.Contains("raw-packet") == true
            ? Task.FromException(new IOException("write interrupted")) : base.WriteLineAsync(value);
    }

    [Fact]
    public async Task Interrupted_write_counts_in_flight_packet_and_cannot_claim_a_complete_footer()
    {
        var writer = new InterruptedWriter();
        var session = new DebugPacketSession("ignored", EnvironmentInfo, writerFactory: _ => writer);
        Assert.True(session.TryRecord(Packet()));
        session.Close("left-table");
        await Finish(session);
        Assert.Equal("disk-error", session.Status);
        Assert.Equal(1, session.Dropped);
        Assert.Equal(0, session.Written);
        Assert.DoesNotContain("capture-end", writer.ToString());
    }

    [Fact]
    public async Task Existing_evidence_is_never_overwritten()
    {
        using var temp = new TempDir();
        string path = Path.Combine(temp.Path, "capture.ndjson");
        await File.WriteAllTextAsync(path, "original evidence");
        var session = new DebugPacketSession(path, EnvironmentInfo);
        await Finish(session);
        Assert.Equal("disk-error", session.Status);
        Assert.Equal("original evidence", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task File_limit_stops_capture_with_incomplete_footer_even_if_already_closing()
    {
        using var temp = new TempDir();
        var session = new DebugPacketSession(Path.Combine(temp.Path,"capture.ndjson"), EnvironmentInfo, maxBytes: 8192);
        session.TryRecord(Packet(bytes: 5000));
        session.Close("left-table");
        await Finish(session);
        Assert.True(new FileInfo(session.Path).Length <= 8192);
        var footer = Read(session.Path)[^1];
        Assert.Equal("size-limit", footer.GetProperty("reason").GetString());
        Assert.False(footer.GetProperty("stream_complete").GetBoolean());
        Assert.Equal(1, session.Dropped);
    }

    [Fact]
    public async Task Table_lifecycle_includes_recent_pre_roll_and_separates_sessions()
    {
        using var temp = new TempDir();
        long now = 10 * Stopwatch.Frequency;
        using var recorder = new AutoPacketRecorder(temp.Path, timestamp: () => now);
        recorder.Update(false, false, EnvironmentInfo);
        recorder.Record(Packet(99, now));
        recorder.Update(true, false, EnvironmentInfo);
        recorder.Record(Packet(1, now));
        now += 3 * Stopwatch.Frequency;
        recorder.Record(Packet(2, now));
        Assert.False(Directory.Exists(recorder.DirectoryPath));
        recorder.Update(true, true, EnvironmentInfo);
        recorder.Record(Packet(3, now));
        var first = recorder.Latest!;
        recorder.Update(true, false, EnvironmentInfo);
        await recorder.FlushClosedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var lines = Read(first.Path);
        Assert.Equal(4, lines.Length);
        Assert.Equal("0x0002", lines[1].GetProperty("opcode").GetString());
        Assert.Equal("0x0003", lines[2].GetProperty("opcode").GetString());
        recorder.Update(true, true, EnvironmentInfo);
        var second = recorder.Latest!;
        Assert.NotEqual(first.Path, second.Path);
        recorder.Dispose();
        recorder.Record(Packet(4, now));
        await recorder.FlushClosedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("unload", Read(second.Path)[^1].GetProperty("reason").GetString());
        Assert.Equal(0, second.Written);
    }

    [Fact]
    public async Task Rejected_pre_roll_is_incomplete_even_without_a_successfully_read_packet()
    {
        using var temp = new TempDir();
        using var recorder = new AutoPacketRecorder(temp.Path);
        recorder.Update(true, false, EnvironmentInfo);
        recorder.Reject("invalid-segment-header");
        recorder.Update(true, true, EnvironmentInfo);
        var session = recorder.Latest!;
        recorder.Update(false, true, EnvironmentInfo);
        await Finish(session);
        Assert.False(Read(session.Path)[^1].GetProperty("stream_complete").GetBoolean());
        Assert.Equal("disabled", session.Status);
    }

    [Fact]
    public async Task Pre_roll_capacity_loss_is_visible_and_terminal_file_does_not_restart_every_frame()
    {
        using var temp = new TempDir();
        long now = Stopwatch.GetTimestamp();
        using var recorder = new AutoPacketRecorder(temp.Path, timestamp: () => now);
        recorder.Update(true, false, EnvironmentInfo);
        for (int i = 0; i < 300; i++) recorder.Record(Packet(timestamp: now));
        recorder.Update(true, true, EnvironmentInfo);
        var session = recorder.Latest!;
        session.Close("size-limit");
        recorder.Update(true, true, EnvironmentInfo);
        Assert.Same(session, recorder.Latest);
        recorder.Update(true, false, EnvironmentInfo);
        await Finish(session);
        Assert.Equal(256, session.Written);
        Assert.False(Read(session.Path)[^1].GetProperty("stream_complete").GetBoolean());
    }

    [Fact]
    public void Debug_capture_is_opt_in_for_existing_configuration()
    {
        var configuration = JsonSerializer.Deserialize<Configuration>("{\"Version\":3}");
        Assert.False(configuration!.DebugAutoPacketLogging);
        string json = JsonSerializer.Serialize(configuration with { DebugAutoPacketLogging = true });
        Assert.True(JsonSerializer.Deserialize<Configuration>(json)!.DebugAutoPacketLogging);
    }
}
