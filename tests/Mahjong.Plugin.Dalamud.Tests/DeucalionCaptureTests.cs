using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Dalamud.Mortal;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Dalamud.Tests;

public class DeucalionCaptureTests
{
    private const string Hello = "SERVER HELLO. VERSION: 1.5.0. HOOK STATUS: RECV ON. SEND ON. SEND_LOBBY ON. CREATE_TARGET ON.";
    private static byte[] Packet()
    {
        var frame = new byte[43];
        BinaryPrimitives.WriteUInt32LittleEndian(frame,43); frame[4]=3;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5),1);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(9),123);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(13),456);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(17),1700000000000);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(25),0x14);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(27),0xBEEF);
        frame[41]=0xCA;frame[42]=0xFE;
        return frame;
    }

    [Fact]
    public void Envelope_supplies_real_length_and_separates_ipc_header_from_payload()
    {
        var packet=Assert.IsType<RawReceivedPacket>(DeucalionWire.Decode(Packet()));
        Assert.Equal(new byte[]{0xCA,0xFE},packet.Payload);
        Assert.Equal((ushort)0xBEEF,packet.Opcode);
        Assert.Null(packet.SegmentLength); // Never invent an observed network segment header.
        Assert.Equal(43,packet.TransportLength);
        Assert.Equal((uint)123,packet.SourceActor);Assert.Equal((uint)456,packet.TargetActor);
        Assert.Equal((ulong)1700000000000,packet.ServerTimestampMilliseconds);
        Assert.Equal(16,packet.IpcHeader!.Length);
    }

    [Theory]
    [InlineData(0)] [InlineData(8)] [InlineData(65546)] [InlineData(-1)]
    public async Task Malicious_lengths_are_rejected_before_reading_or_allocating_body(int length)
    {
        var prefix=new byte[4];BinaryPrimitives.WriteUInt32LittleEndian(prefix,unchecked((uint)length));
        await Assert.ThrowsAsync<InvalidDataException>(()=>DeucalionWire.ReadFrameAsync(new MemoryStream(prefix),CancellationToken.None));
    }

    [Fact]
    public async Task Fragmented_pipe_reads_preserve_consecutive_packet_boundaries()
    {
        using var stream=new TestPipe(maxRead:3);
        stream.Send(Packet());stream.Send(Packet());
        var first=await DeucalionWire.ReadFrameAsync(stream,CancellationToken.None);
        var second=await DeucalionWire.ReadFrameAsync(stream,CancellationToken.None);
        Assert.Equal(Packet(),first);Assert.Equal(first,second);
    }

    [Fact]
    public async Task Truncated_body_is_not_a_partial_packet()
    {
        await Assert.ThrowsAsync<EndOfStreamException>(()=>DeucalionWire.ReadFrameAsync(new MemoryStream(Packet()[..40]),CancellationToken.None));
    }

    [Theory]
    [InlineData(4,1)] [InlineData(3,0)] [InlineData(3,2)] [InlineData(6,1)] [InlineData(1,0)]
    public void Only_received_zone_ipc_is_decoded(byte op,uint channel)
    {
        var frame=Packet();frame[4]=op;BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5),channel);
        Assert.Null(DeucalionWire.Decode(frame));
    }

    [Fact]
    public void Invalid_ipc_marker_is_not_forwarded()
    {
        var frame=Packet();frame[25]=0;Assert.Throws<InvalidDataException>(()=>DeucalionWire.Decode(frame));
    }

    [Fact]
    public async Task Handshake_packets_and_cancellation_are_background_and_never_send_global_exit()
    {
        using var pipe=new TestPipe();
        pipe.Send(DeucalionWire.Command(0,9000,Hello));
        var observed=new TaskCompletionSource<RawReceivedPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source=new DeucalionCapture("unused",_=>Task.FromResult<Stream>(pipe));
        source.Received+=packet=>observed.TrySetResult(packet);
        source.SetEnabled(true);pipe.Send(Packet());
        Assert.Equal((ushort)0xBEEF,(await observed.Task.WaitAsync(TimeSpan.FromSeconds(3))).Opcode);
        Assert.True(source.IsEnabled);
        source.SetEnabled(false);
        await source.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(source.IsEnabled);Assert.Equal("Off",source.Status);
        Assert.DoesNotContain(pipe.Writes,frame=>frame[4]==2 || frame[4]==3 || frame[4]==4);
        Assert.Contains(pipe.Writes,frame=>frame[4]==5 && BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(5))==2);
    }

    [Theory]
    [InlineData("RECV OFF", "RECV ON")]
    [InlineData("CREATE_TARGET OFF", "CREATE_TARGET ON")]
    [InlineData("VERSION: 1.4.0", "VERSION: 1.5.0")]
    public async Task Unready_or_incompatible_server_cannot_publish_packets(string replacement,string original)
    {
        using var pipe=new TestPipe();pipe.Send(DeucalionWire.Command(0,9000,Hello.Replace(original,replacement)));pipe.Send(Packet());
        using var source=new DeucalionCapture("unused",_=>Task.FromResult<Stream>(pipe));
        int packets=0;source.Received+=_=>packets++;
        source.SetEnabled(true);await source.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0,packets);Assert.False(source.IsEnabled);Assert.Contains("Deucalion unavailable",source.Status);
        Assert.Equal(1,source.RejectedPackets);
        source.SetEnabled(false); // Finished worker must not leave a disposed CTS behind.
    }

    [Fact]
    public async Task Old_subscription_is_cancelled_before_new_generation_can_publish()
    {
        using var oldPipe=new TestPipe();using var newPipe=new TestPipe();
        oldPipe.Send(DeucalionWire.Command(0,9000,Hello));newPipe.Send(DeucalionWire.Command(0,9000,Hello));
        int connections=0;
        using var source=new DeucalionCapture("unused",_=>Task.FromResult<Stream>(Interlocked.Increment(ref connections)==1?oldPipe:newPipe));
        var first=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int packets=0;source.Received+=_=> {if(Interlocked.Increment(ref packets)==1)first.TrySetResult();else second.TrySetResult();};
        source.SetEnabled(true);oldPipe.Send(Packet());await first.Task.WaitAsync(TimeSpan.FromSeconds(3));
        source.SetEnabled(false);source.SetEnabled(true);
        oldPipe.Send(Packet());newPipe.Send(Packet());
        await second.Task.WaitAsync(TimeSpan.FromSeconds(3));
        source.Dispose();await source.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2,connections);Assert.Equal(2,packets);
    }

    [Fact]
    public async Task Mid_frame_disconnect_reports_loss_and_stops_subscription()
    {
        using var pipe=new TestPipe();pipe.Send(DeucalionWire.Command(0,9000,Hello));pipe.Send(Packet()[..40]);pipe.End();
        using var source=new DeucalionCapture("unused",_=>Task.FromResult<Stream>(pipe));
        PacketReadFailure? failure=null;source.Rejected+=f=>failure=f;
        source.SetEnabled(true);await source.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("transport-disconnected",failure!.Reason);Assert.False(source.IsEnabled);
    }

    [Fact]
    public void Mortal_admission_requires_verified_variant_exact_length_and_real_transport()
    {
        string? variant="Emj";
        var profile=new MahjongProtocolProfile("2026.09.15.0000.0000","Emj",true,"test replay",
            new(){["0xBEEF"]=new MahjongPacketSpec(123,2)});
        using var capture=new MahjongNetworkCapture(profile.GameVersion,()=>variant,[profile]);
        capture.CaptureEnabled=true;capture.PublicCaptureEnabled=true;
        var packet=DeucalionWire.Decode(Packet())!;
        capture.Record(packet);
        Assert.True(capture.TryDequeue(out var mortal));Assert.True(capture.TryDequeuePublic(out var publicPacket));
        Assert.Equal(mortal,publicPacket);Assert.Equal(123,mortal.MessageId);
        capture.Record(packet with {Payload=[0]});Assert.Equal(1,capture.DroppedPackets);Assert.Equal(0,capture.QueuedPackets);
        capture.Record(packet with {Transport="function-entry"});Assert.Equal(2,capture.DroppedPackets);
        variant="EmjL";capture.RefreshProfile();capture.Record(packet);Assert.False(capture.ProtocolVerified);Assert.Equal(0,capture.QueuedPackets);
        using var unverified=new MahjongNetworkCapture(profile.GameVersion,()=>"Emj",[profile with{Verified=false}]);
        unverified.CaptureEnabled=true;unverified.Record(packet);Assert.Equal(0,unverified.QueuedPackets);
    }

    [Fact]
    public void Mortal_queue_is_bounded_and_reports_overflow()
    {
        var profile=new MahjongProtocolProfile("test","Emj",true,"test",new(){["0xBEEF"]=new MahjongPacketSpec(1,2)});
        using var capture=new MahjongNetworkCapture("test",()=>"Emj",[profile]);capture.CaptureEnabled=true;
        var packet=DeucalionWire.Decode(Packet())!;
        for(int i=0;i<2050;i++)capture.Record(packet);
        Assert.Equal(2048,capture.QueuedPackets);Assert.Equal(2,capture.DroppedPackets);
    }

    private sealed class TestPipe(int maxRead=int.MaxValue):Stream
    {
        private readonly Channel<byte[]> incoming=Channel.CreateUnbounded<byte[]>();
        private byte[]? current;private int offset;
        internal List<byte[]> Writes {get;}=[];
        internal void Send(byte[] bytes)=>incoming.Writer.TryWrite(bytes);
        internal void End()=>incoming.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(current is null || offset==current.Length)
            {
                if(!await incoming.Reader.WaitToReadAsync(cancellationToken))return 0;
                current=await incoming.Reader.ReadAsync(cancellationToken);offset=0;
            }
            int count=Math.Min(Math.Min(buffer.Length,current.Length-offset),maxRead);
            current.AsMemory(offset,count).CopyTo(buffer);offset+=count;return count;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default)
            { cancellationToken.ThrowIfCancellationRequested();lock(Writes)Writes.Add(buffer.ToArray());return ValueTask.CompletedTask; }
        public override bool CanRead=>true;public override bool CanWrite=>true;public override bool CanSeek=>false;
        public override long Length=>throw new NotSupportedException();public override long Position {get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush(){} public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();
        public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
        public override long Seek(long o,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();
    }
}
