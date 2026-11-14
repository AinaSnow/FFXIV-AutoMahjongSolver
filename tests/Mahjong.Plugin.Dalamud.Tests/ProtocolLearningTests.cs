using System.Text.Json;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Dalamud.Mortal;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Dalamud.Tests;

public sealed class ProtocolLearningTests : IDisposable
{
    private const string NewBuild = "2026.11.03.0000.0000";
    private readonly string dir = Path.Combine(Path.GetTempPath(), "mj-learn-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    // The 2026-09-15 profile, as a build that now ships with no profile for the patched client.
    private static readonly MahjongProtocolProfile OldBuild = new("2026.09.15.0000.0000", "Emj", false, "old",
        new() { ["0x023F"] = new(636, 48) }, DomanMortalTrialGuard.Scope, "old");

    private static IEnumerable<RawReceivedPacket> PatchedMatch()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RegressionFixtures", "20260924-draw-result-match.json")));
        var time = DateTimeOffset.UtcNow;
        foreach (var row in doc.RootElement.GetProperty("packets").EnumerateArray())
        {
            ushort opcode = (ushort)(0x0300 + row.GetProperty("message_id").GetInt32() - 635);
            yield return new RawReceivedPacket(time, 0, opcode, null, Convert.FromHexString(row.GetProperty("payload_hex").GetString()!), "deucalion");
            // Unrelated same-length traffic.
            yield return new RawReceivedPacket(time, 0, 0x0152, null, new byte[32], "deucalion");
        }
    }

    [Fact]
    public async Task Unknown_build_learns_once_saves_and_then_admits_only_with_the_trial_switch()
    {
        bool trial = false;
        using var capture = new MahjongNetworkCapture(NewBuild, () => "Emj", [OldBuild], () => trial, () => true, dir);
        Assert.True(capture.LearningWanted);
        Assert.StartsWith("Waiting for a mahjong table", capture.LearningStatus);
        Assert.False(capture.HasProfileForVersion);
        capture.BeginLearningSession(DateTimeOffset.UtcNow);
        foreach (var packet in PatchedMatch()) capture.Record(packet);
        await capture.FinishLearningSession();

        Assert.True(capture.HasProfileForVersion, capture.LearningStatus);
        Assert.False(capture.LearningWanted);
        Assert.StartsWith("Learned protocol", capture.LearningStatus);
        var saved = MahjongProtocolProfile.LoadDirectory(dir).Single();
        Assert.Equal(NewBuild, saved.GameVersion);
        Assert.False(saved.Verified);
        Assert.True(saved.TryGet(0x0301, out var start) && start!.MessageId == 636);
        Assert.False(saved.TryGet(0x0152, out _));

        capture.RefreshProfile();
        Assert.False(capture.ProtocolAdmitted); // learned profiles never bypass the explicit trial switch
        trial = true;
        capture.RefreshProfile();
        Assert.True(capture.ProtocolAdmitted);
    }

    [Fact]
    public async Task Variant_seen_while_seated_is_used_after_the_addon_closes()
    {
        string? variant = "Emj";
        using var capture = new MahjongNetworkCapture(NewBuild, () => variant, [OldBuild], () => true, () => true, dir);
        capture.BeginLearningSession(DateTimeOffset.UtcNow);
        foreach (var packet in PatchedMatch()) capture.Record(packet);
        capture.ObserveTableScores(null);
        variant = null; // the mahjong addon is gone when the table closes
        await capture.FinishLearningSession();
        Assert.True(capture.HasProfileForVersion, capture.LearningStatus);
        Assert.Equal("Emj", MahjongProtocolProfile.LoadDirectory(dir).Single().Variant);
    }

    [Fact]
    public async Task Disagreeing_table_scores_prevent_learning()
    {
        using var capture = new MahjongNetworkCapture(NewBuild, () => "Emj", [OldBuild], () => true, () => true, dir);
        capture.BeginLearningSession(DateTimeOffset.UtcNow);
        foreach (var packet in PatchedMatch()) capture.Record(packet);
        capture.ObserveTableScores([1, 2, 3, 4]);
        await capture.FinishLearningSession();
        Assert.False(capture.HasProfileForVersion);
        Assert.Contains("table scores", capture.LearningStatus);
        Assert.False(Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any());
    }

    [Fact]
    public async Task Packets_seen_away_from_the_table_are_not_learned()
    {
        using var capture = new MahjongNetworkCapture(NewBuild, () => "Emj", [OldBuild], () => true, () => true, dir);
        var old = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
        foreach (var packet in PatchedMatch()) capture.Record(packet with { Time = old });
        await capture.FinishLearningSession(); // never seated: nothing to analyse
        Assert.False(capture.HasProfileForVersion);
        capture.BeginLearningSession(DateTimeOffset.UtcNow);
        await capture.FinishLearningSession(); // seated, but the old packets fell outside the pre-roll
        Assert.False(capture.HasProfileForVersion);
        Assert.False(Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any());
    }

    [Fact]
    public void Learning_is_off_when_disabled_or_when_a_profile_exists()
    {
        using var disabled = new MahjongNetworkCapture(NewBuild, () => "Emj", [OldBuild], () => true, () => false, dir);
        Assert.False(disabled.LearningWanted);
        using var known = new MahjongNetworkCapture(OldBuild.GameVersion, () => "Emj", [OldBuild], () => true, () => true, dir);
        Assert.False(known.LearningWanted);
    }
}
