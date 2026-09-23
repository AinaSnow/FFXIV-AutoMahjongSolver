using System.Text.Json;
namespace Mahjong.Policy.Efficiency;

public sealed record CalibratedOutcome(int Samples,double ExpectedRank,double WinProbability,double DealInProbability,double ExpectedPoints);

/// <summary>Versioned empirical estimates fitted exclusively on the training seed partition.</summary>
public sealed record RankCalibration(int SchemaVersion,string Split,string DatasetSha256,Dictionary<string,CalibratedOutcome> Buckets)
{
    public static RankCalibration Load(string path)
    {
        var result=JsonSerializer.Deserialize<RankCalibration>(File.ReadAllText(path),new JsonSerializerOptions {PropertyNameCaseInsensitive=true})
            ?? throw new InvalidDataException("Empty calibration artifact");
        if(result.SchemaVersion!=1 || result.Split!="train" || result.DatasetSha256.Length!=64)
            throw new InvalidDataException("Calibration requires schema 1 and an identified training-only dataset");
        foreach(var row in result.Buckets.Values)
            if(row.Samples<1 || !double.IsFinite(row.ExpectedRank) || row.ExpectedRank is <1 or >4 ||
                !double.IsFinite(row.WinProbability) || row.WinProbability is <0 or >1 ||
                !double.IsFinite(row.DealInProbability) || row.DealInProbability is <0 or >1 || !double.IsFinite(row.ExpectedPoints))
                throw new InvalidDataException("Invalid calibration row");
        return result;
    }
    public bool TryEstimate(StateSnapshot state,ScoredDiscard discard,out CalibratedOutcome estimate) =>
        Buckets.TryGetValue(Key(state,discard),out estimate!) && estimate.Samples>=50;
    public static string Key(StateSnapshot s,ScoredDiscard d)
    {
        int rank=Mahjong.Policy.Placement.PlacementAdjuster.RankOf(s,s.OurSeat);
        int threats=s.Seats.Where((_,i)=>i!=s.OurSeat).Count(x=>x.Riichi);
        string stage=s.Observations.HasFlag(SnapshotObservationFlags.RoundContext) && s.ScheduledRounds.HasValue
            ? $"{s.ScheduledRounds}:{s.RoundWind}:{s.Kyoku}" : "unknown";
        int gap=s.Scores.Where((_,i)=>i!=s.OurSeat).Select(x=>Math.Abs(x-s.Scores[s.OurSeat])).Min()/4000;
        return $"{d.ShantenAfter}|{Math.Min(4,d.UkeireWeighted/4)}|{s.TurnIndex/6}|{threats}|{rank}|{Math.Min(4,gap)}|{stage}|{(s.SeatInfoKnown ? (s.OurSeat==s.DealerSeat).ToString() : "unknown")}|{Math.Min(5,d.DoraRetained)}|{Math.Min(4,(int)(d.DealInCost/2000))}";
    }
    public PolicyEvaluation Select(StateSnapshot state,PolicyEvaluation fallback)
    {
        if(!state.PublicStateConsistent || fallback.Choice.Kind!=ActionKind.Discard || fallback.Candidates.Count==0) return fallback;
        var rows=new List<(ScoredDiscard Candidate,CalibratedOutcome Value)>();
        foreach(var candidate in fallback.Candidates.Take(4))
        {
            // Do not compare calibrated and uncalibrated candidates on different scales.
            if(!TryEstimate(state,candidate,out var value)) return fallback;
            rows.Add((candidate,value));
        }
        var best=rows.OrderBy(r=>r.Value.ExpectedRank).ThenBy(r=>r.Value.DealInProbability).ThenByDescending(r=>r.Value.ExpectedPoints).First();
        var reason=new Reason("calibrated-rank",$"expected rank={best.Value.ExpectedRank:F3}; deal-in={best.Value.DealInProbability:P1}; n={best.Value.Samples}");
        return fallback with { Source="enhanced-calibrated",Choice=ActionChoice.Discard(best.Candidate.Discard,reason.Display,
            (fallback.Choice.Steps??[]).Append(reason).ToArray()) with {DiscardIsRed=best.Candidate.IsRed} };
    }
}
