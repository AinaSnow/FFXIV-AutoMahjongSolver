using Mahjong.Policy.Efficiency;
namespace Mahjong.Replay.Tests;
public class SequenceReplayTests
{
    [Fact]
    public void Every_sequence_is_nonempty_and_all_hands_are_replayed()
    {
        string path=RepoPathResolver.Resolve("data","replays","sequences");
        var files=Directory.GetFiles(path,"*.sequence.jsonl");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var decisions=SequenceReplay.Run(file,new EfficiencyPolicy());
            Assert.NotEmpty(decisions);
            Assert.True(decisions.Max(d=>d.HandId)>=2);
            Assert.Equal(decisions.OrderBy(d=>d.AtMilliseconds),decisions);
        }
    }
}
