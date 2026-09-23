using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Dalamud.Mortal;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game.Mjai;
namespace Mahjong.Plugin.Dalamud.Tests;
public class BackgroundReliabilityTests
{
    [Fact]
    public async Task Disk_failure_does_not_drop_subsequent_barrier_and_queue_overflow_is_visible()
    {
        using var io = new BackgroundIoWorker(1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        io.TryEnqueue(() => { started.SetResult(); release.Wait(); });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(io.TryEnqueue(() => throw new IOException("disk full")));
        Assert.False(io.TryEnqueue(() => { }));
        release.Set();
        await io.FlushAsync();
        Assert.Equal(2,io.Failures);
    }
    [Fact]
    public async Task Superseded_and_duplicate_sequence_cannot_publish_reactions()
    {
        var fake = new FakeProcess();
        using var client = new MortalProcessClient(new StubPluginLog(), processFactory: _ => fake);
        int reactions = 0; client.ReactionReceived += _ => reactions++;
        client.Start(new("test","test","python"));
        client.Send(new MjaiReach(0));
        string first = await fake.Writes.Reader.ReadAsync();
        client.Send(new MjaiReach(0));
        string second = await fake.Writes.Reader.ReadAsync();
        await fake.Stdout.Lines.Writer.WriteAsync(Response(first));
        await fake.Stdout.Lines.Writer.WriteAsync(Response(second));
        // An invalid repeated acknowledgement must close the session, not dispatch twice.
        await fake.Stdout.Lines.Writer.WriteAsync(Response(second));
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        client.Poll();
        Assert.Equal(0,reactions);
        Assert.False(client.IsRunning);
    }
    [Fact]
    public async Task Process_not_reading_input_is_bounded_and_dispose_never_waits_for_it()
    {
        var fake = new FakeProcess { BlockInput = true };
        using var client = new MortalProcessClient(new StubPluginLog(), processFactory: _ => fake);
        client.Start(new("test","test","python"));
        Assert.Throws<IOException>(() => { for (int i=0;i<1024;i++) client.Send(new MjaiReach(0)); });
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fake.Terminated);
    }
    [Fact]
    public async Task Closing_a_full_queue_preserves_the_pending_end_marker()
    {
        var io=new BackgroundIoWorker(1);
        using var gate=new ManualResetEventSlim();
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        io.TryEnqueue(()=>{started.SetResult();gate.Wait();});
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        io.TryEnqueue(()=>{});
        var marker=io.RunAfterWritesAsync(()=>42);
        io.Dispose();gate.Set();
        Assert.Equal(42,await marker.WaitAsync(TimeSpan.FromSeconds(5)));
        await io.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Healthy_correlated_response_is_published_once_on_poll()
    {
        var fake = new FakeProcess();
        using var client = new MortalProcessClient(new StubPluginLog(), processFactory: _ => fake);
        int reactions = 0; client.ReactionReceived += _ => reactions++;
        client.Start(new("test","test","python"));
        client.Send(new MjaiReach(0));
        string sent = await fake.Writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await fake.Stdout.Lines.Writer.WriteAsync(Response(sent));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (reactions == 0 && DateTime.UtcNow < deadline) { client.Poll(); await Task.Delay(1); }
        client.Poll();
        Assert.Equal(1, reactions);
        client.Dispose();
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static string Response(string input)
    {
        using var doc=JsonDocument.Parse(input); var r=doc.RootElement;
        return JsonSerializer.Serialize(new { session=r.GetProperty("session").GetString(), hand=r.GetProperty("hand").GetInt64(),
            sequence=r.GetProperty("sequence").GetInt64(), reaction=new { type="none" } });
    }
    private sealed class QueueReader : TextReader
    {
        public Channel<string> Lines { get; } = Channel.CreateUnbounded<string>();
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            await Lines.Reader.ReadAsync(cancellationToken);
    }
    private sealed class FakeWriter(FakeProcess owner) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer,CancellationToken cancellationToken=default)
        {
            if (owner.BlockInput) await Task.Delay(Timeout.Infinite,cancellationToken);
            else await owner.Writes.Writer.WriteAsync(buffer.ToString(),cancellationToken);
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class FakeProcess : IManagedJsonProcess
    {
        public Channel<string> Writes { get; } = Channel.CreateUnbounded<string>();
        public QueueReader Stdout { get; } = new();
        public TextReader Output => Stdout;
        public TextReader Error { get; } = new QueueReader();
        public TextWriter Input => new FakeWriter(this);
        public bool BlockInput { get; init; }
        public bool Terminated { get; private set; }
        public void Start() { }
        public async Task<int?> WaitForExitAsync(CancellationToken token) { await Task.Delay(Timeout.Infinite,token); return null; }
        public void Terminate() => Terminated=true;
        public void Dispose() { }
    }
}
