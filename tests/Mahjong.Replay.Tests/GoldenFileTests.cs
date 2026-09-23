using System.IO;
using Mahjong.Policy.Efficiency;

namespace Mahjong.Replay.Tests;

public class GoldenFileTests
{
    public static IEnumerable<object[]> ReplayFixtures()
    {
        var dir = RepoPathResolver.Resolve("data", "replays");
        if (!Directory.Exists(dir) || !Directory.EnumerateFiles(dir, "*.tenhou.json").Any())
            throw new InvalidDataException("Replay fixtures must not be empty");

        foreach (var path in Directory.EnumerateFiles(dir, "*.tenhou.json"))
            yield return new object[] { Path.GetFileName(path) };
    }

    [Fact]
    public void Missing_golden_is_an_error_and_does_not_create_a_file()
    {
        if (Environment.GetEnvironmentVariable("UPDATE_REPLAY_SNAPSHOTS") == "1") return;
        var source = Path.Combine(RepoPathResolver.Resolve("data", "replays"), "synthetic-east-1.tenhou.json");
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        Assert.Throws<FileNotFoundException>(() => GoldenFileReplayHarness.VerifyOrUpdate(source, missing, new EfficiencyPolicy()));
        Assert.False(File.Exists(missing));
    }

    [Theory]
    [MemberData(nameof(ReplayFixtures))]
    public void Replay_matches_golden_file(string fixtureName)
    {
        var dir = RepoPathResolver.Resolve("data", "replays");
        var tenhouPath = Path.Combine(dir, fixtureName);
        var snapshotPath = Path.Combine(
            dir, Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(fixtureName)) + ".snapshot.json");

        var policy = new EfficiencyPolicy();
        var result = GoldenFileReplayHarness.VerifyOrUpdate(tenhouPath, snapshotPath, policy, seat: 0);

        switch (result.Status)
        {
            case GoldenFileStatus.Match:
                break;
            case GoldenFileStatus.Created:
                Assert.NotNull(result.Actual);
                break;
            case GoldenFileStatus.Updated:
                Assert.NotNull(result.Actual);
                break;
            case GoldenFileStatus.Mismatch:
                Assert.Fail(
                    $"Replay drift detected for {fixtureName}.\n" +
                    $"  expected: matches={result.Expected!.Matches}/{result.Expected.TotalDecisions} " +
                    $"acc={result.Expected.Accuracy:F4}\n" +
                    $"  actual:   matches={result.Actual.Matches}/{result.Actual.TotalDecisions} " +
                    $"acc={result.Actual.Accuracy:F4}\n" +
                    $"Set UPDATE_REPLAY_SNAPSHOTS=1 and re-run to accept the new baseline.");
                break;
        }
    }
}
