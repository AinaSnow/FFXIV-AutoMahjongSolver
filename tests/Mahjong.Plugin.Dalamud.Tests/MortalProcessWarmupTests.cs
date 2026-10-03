using Mahjong.Plugin.Dalamud.Mortal;
using Mahjong.Plugin.Game;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Dalamud.Tests;

public sealed class MortalProcessWarmupTests
{
    private static readonly MortalProcessSettings Settings = new("test", "/mortal", "python");
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void One_empty_process_is_adopted_once_even_if_still_loading(bool ready)
    {
        var client = new Client { Ready = ready };
        using var warmup = new MortalProcessWarmup(() => client, new MonotonicClock());
        warmup.Update(true, true, Settings);
        warmup.Update(true, true, Settings);
        Assert.Equal(1, client.Starts);
        Assert.Equal(0, client.Inputs);
        warmup.Update(true, false, Settings); // Table visible: keep preparation for adoption.
        Assert.Same(client, warmup.Take(Settings));
        Assert.Null(warmup.Take(Settings));
        warmup.Dispose();
        Assert.True(client.IsRunning); // Ownership moved to the bridge.
        client.Dispose();
    }

    [Fact]
    public void Disabled_or_changed_configuration_disposes_only_the_old_preparation()
    {
        var clients = new List<Client>();
        using var warmup = new MortalProcessWarmup(() => { var c = new Client(); clients.Add(c); return c; }, new MonotonicClock());
        warmup.Update(true, true, Settings);
        warmup.Update(true, true, Settings with { WorkingDirectory = "/other" });
        Assert.False(clients[0].IsRunning);
        Assert.Null(warmup.Take(Settings));
        warmup.Update(false, true, Settings);
        Assert.False(clients[1].IsRunning);
        Assert.False(warmup.HasProcess);
        warmup.Dispose();
        warmup.Update(true, true, Settings);
        Assert.Equal(2, clients.Count);
        Assert.All(clients, c => Assert.Equal(1, c.Disposals));
    }

    [Fact]
    public void A_stuck_or_exited_process_is_bounded_and_retries_only_after_backoff()
    {
        var time = new Clock();
        var clients = new List<Client>();
        using var warmup = new MortalProcessWarmup(() => { var c = new Client(); clients.Add(c); return c; }, new MonotonicClock(time));
        warmup.Update(true, true, Settings);
        time.Advance(MortalProcessWarmup.LoadTimeout);
        warmup.Update(true, true, Settings);
        Assert.False(clients[0].IsRunning);
        for (int i = 0; i < 100; i++) warmup.Update(true, true, Settings);
        Assert.Single(clients);
        time.Advance(MortalProcessWarmup.RetryDelay);
        warmup.Update(true, true, Settings);
        Assert.Equal(2, clients.Count);
        clients[1].IsRunning = false;
        warmup.Update(true, true, Settings);
        Assert.False(warmup.HasProcess);
        Assert.Equal(1, clients[1].Disposals);
    }

    [Fact]
    public void Ready_process_has_no_load_deadline_and_start_failure_is_cleaned_up()
    {
        var time = new Clock();
        var client = new Client { FailStart = true };
        using var warmup = new MortalProcessWarmup(() => client, new MonotonicClock(time));
        warmup.Update(true, true, Settings);
        Assert.Equal(1, client.Disposals);
        Assert.False(warmup.HasProcess);
        time.Advance(MortalProcessWarmup.RetryDelay);
        client = new Client { Ready = true };
        warmup.Update(true, true, Settings);
        time.Advance(TimeSpan.FromHours(1));
        warmup.Update(true, true, Settings);
        Assert.Same(client, warmup.Take(Settings));
        client.Dispose();
    }

    [Theory]
    [InlineData("not json", false)]
    [InlineData("[]", false)]
    [InlineData("{\"type\":\"mortal_startup\",\"ready\":false}", false)]
    [InlineData("{\"type\":\"mortal_startup\",\"ready\":\"true\"}", false)]
    [InlineData("{\"type\":\"other\",\"ready\":true}", false)]
    [InlineData("{\"type\":\"mortal_startup\",\"ready\":true}", true)]
    public void Only_a_typed_startup_ready_diagnostic_marks_preparation_ready(string line, bool expected)
        => Assert.Equal(expected, MortalProcessClient.IsStartupReady(line));

    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
        public void Advance(TimeSpan delta) => timestamp += (long)delta.TotalMilliseconds;
    }

    private sealed class Client : IMortalProcessClient
    {
        public event Action<string>? ReactionReceived { add { } remove { } }
        public event Action<int?>? Exited { add { } remove { } }
        public bool IsRunning { get; set; }
        public bool Ready, FailStart;
        public bool IsReady => Ready && IsRunning;
        public int Starts, Disposals, Inputs;
        public void Start(MortalProcessSettings settings) { Starts++; IsRunning = true; if (FailStart) throw new IOException("start failed"); }
        public void Send(IMjaiEvent evt) => Inputs++;
        public void SendReplay(string evt) => Inputs++;
        public void Poll() { }
        public void Dispose() { Disposals++; IsRunning = false; }
    }
}
