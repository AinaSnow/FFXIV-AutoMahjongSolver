using Mahjong.Engine;
using Mahjong.Policy.Placement;
namespace Mahjong.Policy.Efficiency;

/// <summary>Experimental comparison using legal ron values, public waits and the remaining draw horizon.</summary>
public sealed class EnhancedRiichiPolicy(IRuleSet rules) : IRiichiPolicy
{
    public Decision<bool> Evaluate(StateSnapshot state,ScoredDiscard discard)
    {
        if(state.OurRiichi || discard.ShantenAfter!=0 || state.OurMelds.Any(m=>m.Kind!=MeldKind.AnKan) || state.Scores[state.OurSeat]<1000 || state.WallRemaining<4)
            return new(false,false,new Reason("enhanced-riichi-illegal","closed tenpai, deposit and remaining draws required"));
        var hand=state.Hand.ToList();hand.Remove(discard.Discard);
        var wall=DiscardScorer.BuildVisibleWall(state);var scorer=new Scorer(rules);
        var waitsAndValues = new List<(int Live, double DamaRon, double RiichiRon, double DamaTsumo, double RiichiTsumo)>();
        bool discardFuriten = false;
        bool riverKnown = state.Observations.HasFlag(SnapshotObservationFlags.PublicDiscardTiles);
        for(int id=0;id<34;id++)
        {
            var tile=Tile.FromId(id);
            if (hand.Count(t => t == tile) + state.OurMelds.Sum(m => m.Tiles.Count(t => t == tile)) >= Tile.CopiesPerKind)
                continue; // A fifth copy cannot be a structural wait, even when testing exhausted waits.
            hand.Add(tile);
            var complete=Hand.FromTiles(hand,state.OurMelds);
            var context=new WinContext(tile,WinKind.Ron,RoundWindTileId:27+state.RoundWind,SeatWindTileId:27+state.EffectiveSeatWind,
                DoraIndicators:state.DoraIndicators,IsDealer:state.DealerSeat==state.OurSeat,AkaDora:Math.Max(0,state.AkaDora-(discard.IsRed==true?1:0)));
            var r=scorer.Evaluate(complete,context with {IsRiichi=true});
            if(r is not null)
            {
                // The planned discard itself also becomes part of our river.
                discardFuriten |= tile == discard.Discard || riverKnown && state.Us.Discards.Contains(tile);
                int live = wall.LiveOf(id);
                if (live > 0)
                {
                    var tsumo = context with { Kind = WinKind.Tsumo };
                    waitsAndValues.Add((live, scorer.Evaluate(complete,context)?.Payments.Total ?? 0, r.Payments.Total,
                        scorer.Evaluate(complete,tsumo)?.Payments.Total ?? 0,
                        scorer.Evaluate(complete,tsumo with { IsRiichi = true })?.Payments.Total ?? 0));
                }
            }
            hand.RemoveAt(hand.Count-1);
        }
        int waits = waitsAndValues.Sum(w => w.Live);
        double dama = waitsAndValues.Sum(w => w.Live * (discardFuriten ? w.DamaTsumo : w.DamaRon));
        double reach = waitsAndValues.Sum(w => w.Live * (discardFuriten ? w.RiichiTsumo : w.RiichiRon));
        if(waits==0) return new(false,false,new Reason("enhanced-riichi-dead","no live legal waits"));
        dama/=waits;reach/=waits;
        // This is a transparent draw-horizon estimate, not a trained probability model.
        double chance=1-Math.Pow(1-Math.Min(.95,(double)waits/Math.Max(waits,state.WallRemaining)),Math.Max(1,state.WallRemaining/4));
        double threatPenalty=state.Seats.Where((_,i)=>i!=state.OurSeat).Count(s=>s.Riichi)*500;
        double reachUtility=chance*reach-1000-threatPenalty;
        double damaUtility=chance*dama;
        if(PlacementAdjuster.IsLastHand(state) && PlacementAdjuster.RankOf(state,state.OurSeat)==1 && dama>0)
            reachUtility-=1000; // avoid risking a secured lead merely to add value
        bool accept=dama==0 || reachUtility>damaUtility;
        return new(accept,accept,new Reason(accept?"enhanced-riichi":"enhanced-dama",
            $"riichi EV={reachUtility:F0}, dama/improvement EV={damaUtility:F0}, waits={waits}; legal values {reach:F0}/{dama:F0}; {(discardFuriten ? "self-discard furiten: tsumo-only" : "temporary furiten unobserved")}"));
    }
}
