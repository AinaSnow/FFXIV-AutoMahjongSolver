using Mahjong.Engine;
using Mahjong.Policy.Placement;
namespace Mahjong.Policy.Efficiency;

/// <summary>Experimental comparison using legal ron values, public waits and the remaining draw horizon.</summary>
public sealed class EnhancedRiichiPolicy(IRuleSet rules) : IRiichiPolicy
{
    public Decision<bool> Evaluate(StateSnapshot state,ScoredDiscard discard)
    {
        if(discard.ShantenAfter!=0 || state.OurMelds.Any(m=>m.Kind!=MeldKind.AnKan) || state.Scores[state.OurSeat]<1000 || state.WallRemaining<4)
            return new(false,false,new Reason("enhanced-riichi-illegal","closed tenpai, deposit and remaining draws required"));
        var hand=state.Hand.ToList();hand.Remove(discard.Discard);
        var wall=DiscardScorer.BuildVisibleWall(state);var scorer=new Scorer(rules);
        double dama=0,reach=0;int waits=0;
        for(int id=0;id<34;id++)
        {
            int live=wall.LiveOf(id);if(live==0)continue;
            var tile=Tile.FromId(id);hand.Add(tile);
            var complete=Hand.FromTiles(hand,state.OurMelds);
            var context=new WinContext(tile,WinKind.Ron,RoundWindTileId:27+state.RoundWind,SeatWindTileId:27+state.EffectiveSeatWind,
                DoraIndicators:state.DoraIndicators,IsDealer:state.DealerSeat==state.OurSeat,AkaDora:Math.Max(0,state.AkaDora-(discard.IsRed==true?1:0)));
            var r=scorer.Evaluate(complete,context with {IsRiichi=true});
            if(r is not null) { waits+=live;reach+=live*r.Payments.Total;dama+=live*(scorer.Evaluate(complete,context)?.Payments.Total??0); }
            hand.RemoveAt(hand.Count-1);
        }
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
            $"riichi EV={reachUtility:F0}, dama/improvement EV={damaUtility:F0}, waits={waits}; legal values {reach:F0}/{dama:F0}"));
    }
}
