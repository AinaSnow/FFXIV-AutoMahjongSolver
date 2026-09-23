using System.Text.Json;
using Mahjong.Plugin.Game.Mjai;
namespace Mahjong.Replay;

public sealed record SequenceDecision(long AtMilliseconds, long HandId, long Revision, string Kind, string? Tile, bool Consistent);

/// <summary>Replays every event in timestamp order, including UI transition frames and hand boundaries.</summary>
public static class SequenceReplay
{
    public static IReadOnlyList<SequenceDecision> Run(string path, IPolicy policy)
    {
        var reducer = new PublicStateReducer();
        var result = new List<SequenceDecision>();
        long time = -1;
        bool hasEvents = false;
        (long Hand, long Version, ActionChoice Choice)? pending = null;
        foreach (string line in File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
            long next = root.GetProperty("at_ms").GetInt64();
            if (next < time) throw new InvalidDataException("Replay timestamps must be ordered");
            time = next; hasEvents = true;
            if (root.TryGetProperty("event", out var evt)) reducer.ApplyJson(evt.GetRawText());
            if (root.TryGetProperty("cancel",out _)) pending = null;
            if (root.TryGetProperty("dispatch",out var dispatch))
            {
                bool accepted = pending is {} p && p.Hand == reducer.HandId && p.Version == reducer.Revision
                    && reducer.Active && reducer.Complete && p.Choice.Kind != ActionKind.Pass;
                if (accepted != dispatch.GetBoolean()) throw new InvalidDataException($"Incorrect dispatch outcome at {time}");
                pending = null; // at most once, including rejected stale callbacks
            }
            if (root.TryGetProperty("loss",out _)) reducer.Invalidate("recorded packet loss");
            if (root.TryGetProperty("ui",out var ui))
            {
                var tiles = ui.GetProperty("hand").EnumerateArray().Select(t => {
                    if (!MjaiTile.TryParse(t.GetString(),out var kind,out var red)) throw new InvalidDataException("Unknown UI tile");
                    return (kind,red);
                }).ToArray();
                var state = reducer.Merge(StateSnapshot.Empty with { Hand=tiles.Select(t=>t.kind).ToArray(),
                    HandIsRed=tiles.Select(t=>t.red).ToArray(), Observations=SnapshotObservationFlags.HandRedIdentity,
                    Legal=new LegalActions(Enum.Parse<ActionFlags>(ui.GetProperty("legal").GetString()!),[],[],[],[]) });
                var choice=policy.Choose(state);
                pending = (state.HandId,state.Revision,choice);
                result.Add(new(time,state.HandId,state.Revision,choice.Kind.ToString(),
                    choice.DiscardTile is {} tile ? MjaiTile.Format(tile,choice.DiscardIsRed==true) : null,state.PublicStateConsistent));
                if (ui.TryGetProperty("expect_kind",out var expected) && choice.Kind.ToString()!=expected.GetString())
                    throw new InvalidDataException($"Unexpected decision at {time}: {choice.Kind}, expected {expected.GetString()}");
                if (ui.TryGetProperty("expect_consistent",out var complete) && state.PublicStateConsistent!=complete.GetBoolean())
                    throw new InvalidDataException($"Incorrect completeness at {time}");
            }
        }
        if (!hasEvents || result.Count==0) throw new InvalidDataException("Empty sequence replay");
        return result;
    }
}
