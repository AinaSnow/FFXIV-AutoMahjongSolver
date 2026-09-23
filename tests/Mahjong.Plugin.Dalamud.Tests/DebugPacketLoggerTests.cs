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
    private static RawReceivedPacket Packet(ushort opcode = 0xBEEF, long? timestamp = null, int bytes = 4)
    {
        var ipc = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(ipc,0x14);
        BinaryPrimitives.WriteUInt16LittleEndian(ipc.AsSpan(2),opcode);
        return new(DateTimeOffset.UtcNow,timestamp ?? Stopwatch.GetTimestamp(),opcode,null,new byte[bytes],
            "deucalion",bytes+41,1,2,0,ipc);
    }
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
        Assert.Equal(1,footer.GetProperty("diagnostic_dropped").GetInt32());
        Assert.Equal(0,footer.GetProperty("diagnostic_samples").GetInt32());
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
    public async Task Zero_packet_capture_cannot_claim_completeness()
    {
        using var temp = new TempDir();
        var session = new DebugPacketSession(Path.Combine(temp.Path, "empty.ndjson"), EnvironmentInfo);
        session.Close("left-table");
        await Finish(session);
        var footer = Read(session.Path)[^1];
        Assert.True(footer.GetProperty("no_packets").GetBoolean());
        Assert.False(footer.GetProperty("stream_complete").GetBoolean());
    }

    [Theory]
    [InlineData(0, 0, 0, "Waiting for first packet")]
    [InlineData(0, 0, 5, "Capture stalled: no packets observed")]
    [InlineData(0, 4, 5, "Capture failed: packet headers rejected")]
    [InlineData(1, 0, 6, "Recording")]
    [InlineData(1, 2, 6, "Recording with rejected packets")]
    public void Capture_health_requires_observed_packets(long packets, long rejects, int seconds, string expected) =>
        Assert.Equal(expected, DebugPacketSession.DescribeProgress(packets, rejects, TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(0x1000, 0x1000, 0x100, true)]
    [InlineData(0x10ff, 0x1000, 0x100, true)]
    [InlineData(0x1100, 0x1000, 0x100, false)]
    [InlineData(0x0fff, 0x1000, 0x100, false)]
    [InlineData(0x80000, 0x1000, 0x100, false)]
    [InlineData(0x1000, 0x1000, 0, false)]
    public void Old_forwarding_thunks_outside_the_game_are_not_used_as_receive_entries(int entry, int moduleBase, int size, bool expected) =>
        Assert.Equal(expected, RawPacketCapture.IsGameCodeAddress(entry, moduleBase, size));

    [Theory]
    [InlineData(0, 31, "invalid-segment-length")]
    [InlineData(0, 65537, "invalid-segment-length")]
    [InlineData(8, 456, "target-mismatch")]
    [InlineData(12, 7, "segment-type-mismatch")]
    [InlineData(16, 0, "ipc-marker-mismatch")]
    public void Failed_header_has_specific_reason_and_never_reads_a_payload(int offset, uint value, string reason)
    {
        var data = Segment();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
        int reads = 0;
        bool Reader(nint address, Span<byte> buffer, out int error)
        {
            reads++; Assert.Equal(32,buffer.Length); Assert.Equal((nint)0x1000,address);
            data.AsSpan(0,32).CopyTo(buffer); error=0; return true;
        }
        Assert.False(RawPacketCapture.TryCopyDetailed(0x1010,123,Reader,out var packet,out var failure));
        Assert.Null(packet); Assert.Equal(1,reads);
        Assert.Equal(reason,failure!.Reason);
        Assert.Contains(reason,failure.FailedChecks!);
        Assert.Equal(data[..32],failure.Header);
    }

    [Fact]
    public void Failed_read_never_serializes_partial_buffer_and_preserves_os_error()
    {
        bool Reader(nint address, Span<byte> buffer, out int error)
            { buffer.Fill(0xAA); error=299; return false; }
        Assert.False(RawPacketCapture.TryCopyDetailed(0x1010,123,Reader,out _,out var failure));
        Assert.Equal("unreadable-header",failure!.Reason);
        Assert.Equal(299,failure.Win32Error); Assert.Null(failure.Header);
        var record = JsonSerializer.SerializeToElement(failure.ToDiagnosticRecord());
        Assert.Equal(JsonValueKind.Null,record.GetProperty("header_hex").ValueKind);
        Assert.Equal(JsonValueKind.Null,record.GetProperty("candidate_length").ValueKind);
    }

    [Fact]
    public void Simultaneous_header_mismatches_are_preserved_without_treating_opcode_as_verified()
    {
        var checks = RawPacketCapture.HeaderFailures(new byte[32],123);
        Assert.Equal(new[]{"invalid-segment-length","segment-type-mismatch","target-mismatch","ipc-marker-mismatch"},checks);
        var data = Segment()[..32];
        var record = JsonSerializer.SerializeToElement(new PacketReadFailure("target-mismatch",999,data,checks).ToDiagnosticRecord());
        Assert.False(record.GetProperty("layout_verified").GetBoolean());
        Assert.Equal(36,record.GetProperty("candidate_length").GetInt32());
        Assert.Equal(123,record.GetProperty("candidate_target").GetInt32());
        Assert.Equal(999,record.GetProperty("expected_target").GetInt32());
        Assert.Equal("0xBEEF",record.GetProperty("candidate_opcode").GetString());
        Assert.Equal(64,record.GetProperty("header_hex").GetString()!.Length);
    }

    [Theory]
    [InlineData(false, "unreadable-segment")]
    [InlineData(true, "segment-changed-during-copy")]
    public void Second_read_failures_keep_only_the_original_header(bool changed, string reason)
    {
        var data = Segment(); int reads=0;
        bool Reader(nint address, Span<byte> buffer, out int error)
        {
            reads++; data.AsSpan(0,buffer.Length).CopyTo(buffer); error=0;
            if (reads==1) return true;
            if (changed) { buffer[0]++; return true; }
            error=299; return false;
        }
        Assert.False(RawPacketCapture.TryCopyDetailed(0x1010,123,Reader,out var packet,out var failure));
        Assert.Null(packet); Assert.Equal(2,reads); Assert.Equal(reason,failure!.Reason);
        Assert.Equal(data[..32],failure.Header);
    }

    [Fact]
    public async Task Rejection_storm_has_two_samples_per_reason_and_eight_total_without_packet_inflation()
    {
        using var temp = new TempDir();
        var session = new DebugPacketSession(Path.Combine(temp.Path,"diagnostics.ndjson"),EnvironmentInfo);
        for(int i=0;i<1262;i++) session.Reject(new PacketReadFailure("target-mismatch",999,Segment()[..32],["target-mismatch"]));
        foreach(string reason in new[]{"invalid-segment-length","ipc-marker-mismatch","segment-type-mismatch","unreadable-header"})
            for(int i=0;i<3;i++) session.Reject(new PacketReadFailure(reason));
        session.Close("left-table");
        session.Reject(new PacketReadFailure("capture-exception"));
        await Finish(session);
        var records=Read(session.Path);
        var diagnostics=records.Where(r=>r.GetProperty("e").GetString()=="capture-diagnostic").ToArray();
        Assert.Equal(8,diagnostics.Length);
        Assert.All(diagnostics.GroupBy(r=>r.GetProperty("reason").GetString()),g=>Assert.Equal(2,g.Count()));
        var footer=records[^1];
        Assert.Equal(1274,footer.GetProperty("rejected").GetInt32());
        Assert.Equal(1262,footer.GetProperty("rejection_counts").GetProperty("target-mismatch").GetInt32());
        Assert.Equal(1266,footer.GetProperty("diagnostic_unsampled").GetInt32());
        Assert.Equal(0,footer.GetProperty("packets").GetInt32());
        Assert.Equal(0,footer.GetProperty("diagnostic_dropped").GetInt32());
        Assert.False(footer.GetProperty("stream_complete").GetBoolean());
    }

    [Fact]
    public async Task Diagnostic_sample_budget_resets_at_each_table_and_disabled_capture_writes_nothing()
    {
        using var temp = new TempDir();
        using var recorder = new AutoPacketRecorder(temp.Path);
        recorder.Reject(new PacketReadFailure("unreadable-header"));
        Assert.Null(recorder.Latest);
        for(int table=0;table<2;table++)
        {
            recorder.Update(true,true,EnvironmentInfo);
            var session=recorder.Latest!;
            for(int i=0;i<10;i++) recorder.Reject(new PacketReadFailure("unreadable-header"));
            recorder.Update(true,false,EnvironmentInfo);
            await Finish(session);
            Assert.Equal(2,Read(session.Path)[^1].GetProperty("diagnostic_samples").GetInt32());
        }
    }

    private sealed class InterruptedDiagnosticWriter : StringWriter
    {
        public override Task WriteLineAsync(string? value) => value?.Contains("capture-diagnostic") == true
            ? Task.FromException(new IOException("diagnostic write interrupted")) : base.WriteLineAsync(value);
    }

    [Fact]
    public async Task Interrupted_diagnostic_write_does_not_claim_packet_loss_or_success()
    {
        var writer = new InterruptedDiagnosticWriter();
        var session = new DebugPacketSession("ignored",EnvironmentInfo,writerFactory:_=>writer);
        session.Reject(new PacketReadFailure("unreadable-header",Win32Error:299));
        session.Close("left-table");
        await Finish(session);
        Assert.Equal("disk-error",session.Status);
        Assert.Equal(0,session.Written);
        Assert.Equal(0,session.Dropped);
        Assert.Contains("diagnostic write interrupted",session.Error);
        Assert.DoesNotContain("capture-end",writer.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(15)]
    public void Invalid_pointer_is_reported_without_any_memory_read(int pointer)
    {
        bool Reader(nint address, Span<byte> buffer, out int error) => throw new InvalidOperationException("unexpected read");
        Assert.False(RawPacketCapture.TryCopyDetailed(pointer,123,Reader,out _,out var failure));
        Assert.Equal("invalid-ipc-pointer",failure!.Reason);
        Assert.Null(failure.Header);
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
