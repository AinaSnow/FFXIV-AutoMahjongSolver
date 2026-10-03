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

    [Fact]
    public async Task Model_identity_is_bound_to_validated_process_session()
    {
        var fake = new FakeProcess();
        using var client = new MortalProcessClient(new StubPluginLog(), processFactory: _ => fake);
        client.Start(new("test", "test", "python"));
        client.Send(new MjaiReach(0));
        string sent = await fake.Writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var response = System.Text.Json.Nodes.JsonNode.Parse(Response(sent))!;
        response["model"] = new System.Text.Json.Nodes.JsonObject { ["checkpoint_sha256"] = new string('a', 64) };
        await fake.Stdout.Lines.Writer.WriteAsync(response.ToJsonString());
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (client.ModelIdentityJson is null && DateTime.UtcNow < deadline) await Task.Delay(1);
        using var identity = JsonDocument.Parse(Assert.IsType<string>(client.ModelIdentityJson));
        using var request = JsonDocument.Parse(sent);
        Assert.Equal(request.RootElement.GetProperty("session").GetString(), identity.RootElement.GetProperty("session").GetString());
        Assert.Equal(new string('a',64), identity.RootElement.GetProperty("model").GetProperty("checkpoint_sha256").GetString());
        client.Dispose(); await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(1, "pon")] [InlineData(2, "pon")] [InlineData(3, "pon")] [InlineData(2, "none")]
    public async Task Riichi_discard_response_survives_its_acceptance_announcement(int actor, string reactionType)
    {
        var fake = new FakeProcess();
        using var client = new MortalProcessClient(new StubPluginLog(), processFactory: _ => fake);
        var reactions = new List<string>(); client.ReactionReceived += reactions.Add;
        client.Start(new("test", "test", "python"));
        // Same batch/order as the three captured timeouts; no polling between sends.
        client.Send(new MjaiReach(actor));
        client.Send(new MjaiDahai(actor, "5p", false));
        client.Send(new MjaiReachAccepted(actor));
        await Acknowledge(fake, reaction: null);
        await Acknowledge(fake, reaction: reactionType == "none" ? """{"type":"none"}"""
            : JsonSerializer.Serialize(new { type = "pon", actor = 0, target = actor, pai = "5p", consumed = new[] { "5p", "5p" } }));
        await Acknowledge(fake, reaction: null, barrier: true);
        await AwaitBarrier(client);
        client.Poll(); client.Poll();
        Assert.Single(reactions);
        Assert.Equal(reactionType, JsonDocument.Parse(reactions[0]).RootElement.GetProperty("type").GetString());
        client.Dispose(); await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("tsumo")] [InlineData("dahai")] [InlineData("dora")]
    [InlineData("end_kyoku")] [InlineData("start_kyoku")] [InlineData("wrong_actor")]
    [InlineData("replay")] [InlineData("dispose")] [InlineData("announcement_reaction")]
    public async Task Riichi_response_still_rejects_superseding_events_replay_and_disposal(string next)
    {
        var fake = new FakeProcess();
        using var client = new MortalProcessClient(new StubPluginLog(), processFactory: _ => fake);
        int reactions = 0; client.ReactionReceived += _ => reactions++;
        client.Start(new("test", "test", "python"));
        client.Send(new MjaiReach(1));
        client.Send(new MjaiDahai(1, "5p", false));
        client.Send(new MjaiReachAccepted(next == "wrong_actor" ? 2 : 1));
        await Acknowledge(fake, null);
        await Acknowledge(fake, next == "announcement_reaction" ? null : """{"type":"pon"}""");
        bool extra = next is not ("wrong_actor" or "dispose" or "announcement_reaction");
        await Acknowledge(fake, next == "announcement_reaction" ? """{"type":"none"}""" : null, !extra);
        if (extra)
        {
            switch (next)
            {
                case "tsumo": client.Send(new MjaiTsumo(2, "?")); break;
                case "dahai": client.Send(new MjaiDahai(2, "1m", true)); break;
                case "dora": client.Send(new MjaiDora("3s")); break;
                case "end_kyoku": client.Send(new MjaiEndKyoku()); break;
                case "start_kyoku": client.Send(new MjaiStartKyoku("E", "1m", 2, 0, 0, 1, [25000,25000,25000,25000], [[],[],[],[]])); break;
                case "replay": client.SendReplay("""{"type":"dahai","actor":2,"pai":"1m","tsumogiri":true}"""); break;
            }
            await Acknowledge(fake, next == "replay" ? """{"type":"pon"}""" : null, true);
        }
        await AwaitBarrier(client);
        if (next == "dispose") client.Dispose();
        client.Poll(); Assert.Equal(0, reactions);
        client.Dispose(); await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Wrong_session_cannot_publish_response_or_model_identity()
    {
        var fake = new FakeProcess();
        using var client = new MortalProcessClient(new StubPluginLog(), processFactory: _ => fake);
        int reactions = 0; client.ReactionReceived += _ => reactions++;
        client.Start(new("test", "test", "python")); client.Send(new MjaiReach(0));
        string line = await fake.Writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var response = System.Text.Json.Nodes.JsonNode.Parse(Response(line))!;
        response["session"] = "old-process";
        response["model"] = new System.Text.Json.Nodes.JsonObject();
        await fake.Stdout.Lines.Writer.WriteAsync(response.ToJsonString());
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5)); client.Poll();
        Assert.Equal(0, reactions); Assert.Null(client.ModelIdentityJson);
    }

    private static async Task Acknowledge(FakeProcess fake, string? reaction, bool barrier = false)
    {
        string line = await fake.Writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var response = System.Text.Json.Nodes.JsonNode.Parse(Response(line))!;
        response["reaction"] = reaction is null ? null : System.Text.Json.Nodes.JsonNode.Parse(reaction);
        if (barrier) response["model"] = new System.Text.Json.Nodes.JsonObject { ["test_barrier"] = true };
        await fake.Stdout.Lines.Writer.WriteAsync(response.ToJsonString());
    }

    [Fact]
    public async Task Ready_arrives_without_any_game_inputs_and_dispose_revokes_readiness()
    {
        var fake = new FakeProcess();
        using var client = new MortalProcessClient(new StubPluginLog(), processFactory: _ => fake);
        int reactions = 0;
        client.ReactionReceived += _ => reactions++;
        client.Start(new("test", "test", "python"));
        Assert.False(client.IsReady);
        await ((QueueReader)fake.Error).Lines.Writer.WriteAsync("{\"type\":\"mortal_startup\",\"ready\":true}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!client.IsReady) await Task.Delay(1, timeout.Token);
        client.Poll();
        Assert.False(fake.Writes.Reader.TryRead(out _));
        Assert.Equal(0, reactions);
        client.Dispose();
        Assert.False(client.IsReady);
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fake.Terminated);
    }

    private static async Task AwaitBarrier(MortalProcessClient client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (client.ModelIdentityJson is null) await Task.Delay(1, timeout.Token);
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
