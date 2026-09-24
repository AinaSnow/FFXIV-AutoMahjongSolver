using Mahjong.Policy.Abstractions;
using Mahjong.Core;
using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Mahjong.Plugin.Dalamud.Composition;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Dalamud.Mortal;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Dalamud.Tests;

public sealed class MortalLimitedTrialTests
{
    private static MahjongProtocolProfile Profile => new("2026.09.15.0000.0000", "Emj", false, "partial",
        new() { ["0x0133"] = new(637, 104), ["0x02BB"] = new(638, 24),
            ["0x0214"] = new(641, 32), ["0x018D"] = new(643, 264),
            ["0x023F"] = new(636, 48), ["0x0371"] = new(639, 256), ["0x016D"] = new(640, 504) },
        DomanMortalTrialGuard.Scope, "independent opening and public history evidence");

    [Fact]
    public void Trial_requires_explicit_opt_in_exact_build_variant_scope_and_evidence()
    {
        Assert.False(new Configuration().MortalLimitedTrial);
        bool enabled = false;
        string? variant = "Emj";
        using var capture = new MahjongNetworkCapture(Profile.GameVersion, () => variant, [Profile], () => enabled);
        Assert.False(capture.ProtocolAdmitted);
        enabled = true; capture.RefreshProfile();
        Assert.True(capture.ProtocolAdmitted); Assert.False(capture.ProtocolVerified);
        variant = "EmjL"; capture.RefreshProfile(); Assert.False(capture.ProtocolAdmitted);
        Assert.False(Profile.MatchesLimitedTrial("2026.09.16.0000.0000", "Emj"));
        Assert.False((Profile with { LimitedTrialScope = "future" }).MatchesLimitedTrial(Profile.GameVersion, "Emj"));
        Assert.False((Profile with { LimitedTrialEvidence = "" }).MatchesLimitedTrial(Profile.GameVersion, "Emj"));
    }

    [Fact]
    public void Opening_before_addon_appears_reaches_each_consumer_once()
    {
        string? variant = null;
        using var capture = new MahjongNetworkCapture(Profile.GameVersion, () => variant, [Profile], () => true);
        capture.Record(Packet(0x0133, Opening()));
        Assert.False(capture.TryDequeue(out _));
        variant = "Emj"; capture.RefreshProfile();
        capture.CaptureEnabled = true; capture.PublicCaptureEnabled = true;
        Assert.Equal(637, Drain(capture).Single().MessageId);
        Assert.True(capture.TryDequeuePublic(out _)); Assert.False(capture.TryDequeuePublic(out _));
        capture.RefreshProfile(); capture.CaptureEnabled = true;
        Assert.Empty(Drain(capture));
    }

    [Fact]
    public void Old_opening_is_not_replayed_and_bad_lengths_signal_a_gap()
    {
        using var capture = new MahjongNetworkCapture(Profile.GameVersion, () => "Emj", [Profile], () => true);
        capture.Record(Packet(0x0133, Opening()) with { Time = DateTimeOffset.UtcNow.AddSeconds(-10) });
        capture.CaptureEnabled = true; Assert.Empty(Drain(capture));
        capture.Record(Packet(0x0133, new byte[103]));
        Assert.True(capture.DroppedPackets > 0); Assert.Empty(Drain(capture));
    }

    [Fact]
    public void Actual_riichi_discard_is_sent_once_after_virtual_reach_and_replay_is_authoritative()
    {
        using var h = new Harness(); h.Open(); h.Send(0x02BB, Draw());
        var old = h.Client;
        old.Emit("{\"type\":\"reach\",\"actor\":0}");
        old.Emit("{\"type\":\"dahai\",\"actor\":0,\"pai\":\"5sr\",\"tsumogiri\":false}");
        Assert.True(h.Bridge.TryChoose(h.Snapshot, out var choice));
        Assert.Equal(ActionKind.Riichi, choice.Kind); Assert.True(choice.DiscardIsRed);
        h.Send(0x0214, Discard(0x111));
        Assert.Single(old.Sent.OfType<MjaiReach>());
        Assert.Equal(new MjaiDahai(0, "5sr", false), Assert.Single(old.Sent.OfType<MjaiDahai>()));
        Assert.Single(old.Sent.OfType<MjaiReachAccepted>());
        h.Bridge.RecoverUnresponsiveDecision("test timeout"); h.Bridge.Update();
        var replay = h.Client.Replayed.Select(x => JsonDocument.Parse(x).RootElement.GetProperty("type").GetString()!).ToArray();
        Assert.Equal(["start_game", "start_kyoku", "tsumo", "reach", "dahai", "reach_accepted"], replay);
        Assert.Empty(h.Client.Sent); // The pre-roll must not duplicate the replay journal.
    }

    [Theory]
    [InlineData(0x140)]
    [InlineData(0x130)]
    [InlineData(0x113)]
    public void Unsupported_action_quarantines_then_resumes_at_observed_next_opening(int action)
    {
        using var h = new Harness(); h.Open();
        var old = h.Client; var late = old.ReactionReceivedSnapshot;
        h.Send(0x0214, Discard((uint)action));
        Assert.True(h.Bridge.CurrentHandQuarantined); Assert.False(old.IsRunning);
        long count = h.Bridge.ReactionsReceived;
        late?.Invoke("{\"type\":\"dahai\",\"actor\":0,\"pai\":\"1m\"}");
        Assert.Equal(count, h.Bridge.ReactionsReceived);
        h.Send(0x018D, new byte[264]); h.Open();
        Assert.False(h.Bridge.CurrentHandQuarantined);
        Assert.Single(h.Client.Sent.OfType<MjaiStartKyoku>());
        Assert.False(h.Bridge.TryChoose(h.Snapshot, out _));
    }

    [Fact]
    public void Nonzero_opening_pool_is_skipped_and_next_zero_pool_opening_can_resume()
    {
        using var h = new Harness(); h.Open(990);
        Assert.True(h.Bridge.CurrentHandQuarantined);
        Assert.DoesNotContain(h.Client.Sent, x => x is MjaiStartKyoku);
        h.Open(); Assert.False(h.Bridge.CurrentHandQuarantined);
    }

    [Fact]
    public void Missing_opening_and_red_identity_conflict_cannot_expose_recommendations()
    {
        using var h = new Harness(); h.Send(0x02BB, Draw());
        Assert.True(h.Bridge.CurrentHandQuarantined);
        h.Open(); h.Send(0x02BB, Draw());
        h.Client.Emit("{\"type\":\"dahai\",\"actor\":0,\"pai\":\"5sr\"}");
        Assert.True(h.Bridge.TryChoose(h.Snapshot, out _));
        var wrong = h.Snapshot with { HandIsRed = new bool[h.Snapshot.Hand.Count] };
        Assert.False(h.Bridge.TryGetRecommendation(wrong, out _, out _));
        Assert.True(h.Bridge.TryGetRecommendation(h.Snapshot, out _, out _));
        h.Config.Update(c => c with { MortalLimitedTrial = false }); h.Capture.RefreshProfile(); h.Bridge.Update();
        Assert.False(h.Bridge.Enabled); Assert.False(h.Client.IsRunning);
        Assert.False(h.Bridge.TryGetRecommendation(h.Snapshot, out _, out _));
    }

    [Fact]
    public void Queue_delivery_failure_isolates_current_hand()
    {
        using var h = new Harness(); h.Open(); h.Client.RejectInput = true;
        h.Send(0x02BB, Draw());
        Assert.True(h.Bridge.CurrentHandQuarantined); Assert.False(h.Client.IsRunning);
    }

    [Fact]
    public void New_hand_does_not_reuse_a_recommendation_for_an_identical_hand()
    {
        using var h = new Harness(); h.Open(); h.Send(0x02BB, Draw());
        h.Client.Emit("{\"type\":\"dahai\",\"actor\":0,\"pai\":\"5sr\"}");
        Assert.True(h.Bridge.TryChoose(h.Snapshot, out _));
        h.Send(0x018D, new byte[264]); h.Open(); h.Send(0x02BB, Draw());
        Assert.False(h.Bridge.TryChoose(h.Snapshot, out _));
    }

    [Fact]
    public void Manual_discard_that_declines_model_riichi_does_not_continue_in_a_false_reach_state()
    {
        using var h = new Harness(); h.Open(); h.Send(0x02BB, Draw());
        h.Client.Emit("{\"type\":\"reach\",\"actor\":0}");
        h.Client.Emit("{\"type\":\"dahai\",\"actor\":0,\"pai\":\"5sr\"}");
        h.Send(0x0214, Discard(0x110));
        Assert.True(h.Bridge.CurrentHandQuarantined);
        Assert.False(h.Client.IsRunning);
    }

    [Fact]
    public void Latest_complete_capture_runs_through_production_trial_bridge_including_both_draw_results()
    {
        using var h = new Harness();
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "RegressionFixtures", "20260924-draw-result-match.json")));
        var initialClient = h.Client;
        foreach (var row in data.RootElement.GetProperty("packets").EnumerateArray())
        {
            ushort opcode = Convert.ToUInt16(row.GetProperty("opcode").GetString()![2..], 16);
            h.Send(opcode, Convert.FromHexString(row.GetProperty("payload_hex").GetString()!));
            Assert.Same(initialClient, h.Client);
            Assert.True(h.Client.IsRunning, $"Quarantined at packet {row.GetProperty("sequence")}: {h.Bridge.Status}");
        }
        Assert.Equal(8, h.Client.Sent.OfType<MjaiStartKyoku>().Count());
        Assert.Equal(8, h.Client.Sent.OfType<MjaiEndKyoku>().Count());
        Assert.Equal(103, h.Client.Sent.OfType<MjaiTsumo>().Count(e => e.Actor == 0));
        Assert.Equal(104, h.Client.Sent.OfType<MjaiDahai>().Count(e => e.Actor == 0));
        Assert.True(h.Bridge.CurrentHandQuarantined); // Last actual result closes the hand.
    }

    private static List<CapturedMahjongPacket> Drain(MahjongNetworkCapture capture)
    {
        List<CapturedMahjongPacket> result = [];
        while (capture.TryDequeue(out var packet)) result.Add(packet);
        return result;
    }
    private static RawReceivedPacket Packet(ushort opcode, byte[] bytes) =>
        new(DateTimeOffset.UtcNow, 0, opcode, bytes.Length, bytes, "deucalion");
    private static byte[] Opening(int scoreTotal = 1000)
    {
        var bytes = new byte[104];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 22);
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32 + i * 4), i == 0 ? scoreTotal - 750 : 250);
        int[] hand = [8, 18, 22, 9, 10, 31, 15, 24, 0, 6, 6, 19, 30];
        for (int i = 0; i < 13; i++) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(48 + i * 4), hand[i]);
        return bytes;
    }
    private static byte[] Draw()
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 0x100);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 89);
        return bytes;
    }
    private static byte[] Discard(uint action)
    {
        var bytes = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), action);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 89);
        return bytes;
    }
    private sealed class Harness : IDisposable
    {
        public readonly DalamudConfigService Config = new(_ => { }, new Configuration
        { MortalEnabled = true, MortalLimitedTrial = true, MortalWslDistribution = "test", MortalWorkingDirectory = "/test" });
        public readonly MahjongNetworkCapture Capture;
        public readonly LiveMortalBridge Bridge;
        private readonly MahjongPacketMjaiDecoder decoder = new();
        private readonly PublicStateReducer state = new();
        public FakeClient Client = null!;
        public StateSnapshot Snapshot => state.Snapshot(new LegalActions(ActionFlags.Discard | ActionFlags.Riichi, [], [], [], []));
        public Harness()
        {
            Capture = new(Profile.GameVersion, () => "Emj", [Profile], () => Config.Current.MortalLimitedTrial) { TransportReady = true };
            Bridge = new(Capture, DispatchProxy.Create<IFramework, FrameworkStub>(), Config, () => null, new StubPluginLog())
                { ClientFactory = () => Client = new FakeClient() };
            Bridge.Update();
        }
        public void Open(int scoreTotal = 1000) => Send(0x0133, Opening(scoreTotal));
        public void Send(ushort opcode, byte[] bytes)
        {
            try { foreach (var evt in decoder.Process(Profile.Packets[$"0x{opcode:X4}"].MessageId, bytes)) state.Apply(evt); }
            catch (InvalidDataException) { }
            Capture.Record(Packet(opcode, bytes)); Bridge.Update();
        }
        public void Dispose() { Bridge.Dispose(); Capture.Dispose(); }
    }
    public class FrameworkStub : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
    }
    private sealed class FakeClient : IMortalProcessClient
    {
        public event Action<string>? ReactionReceived;
        public event Action<int?>? Exited { add { } remove { } }
        public Action<string>? ReactionReceivedSnapshot => ReactionReceived;
        public bool IsRunning { get; private set; }
        public bool RejectInput;
        public List<IMjaiEvent> Sent = [];
        public List<string> Replayed = [];
        public void Start(MortalProcessSettings settings) => IsRunning = true;
        public void Send(IMjaiEvent evt) { if (RejectInput) throw new IOException("full"); Sent.Add(evt); }
        public void SendReplay(string evt) => Replayed.Add(evt);
        public void Poll() { }
        public void Emit(string json) => ReactionReceived?.Invoke(json);
        public void Dispose() => IsRunning = false;
    }
}
