namespace Mahjong.Policy.Abstractions;

/// <summary>One evaluation shared by hints, dispatch and diagnostics.</summary>
public sealed record PolicyEvaluation(ActionChoice Choice, IReadOnlyList<ScoredDiscard> Candidates)
{
    public long HandId { get; init; }
    public long Revision { get; init; }
    public string Source { get; init; } = "stable";
    public double ElapsedMilliseconds { get; init; }
    public IReadOnlyList<ScoredDiscard> Candidates { get; init; } = [.. Candidates];
}

public interface IAnalyzablePolicy : IPolicy
{
    PolicyEvaluation Analyze(StateSnapshot state);
}
