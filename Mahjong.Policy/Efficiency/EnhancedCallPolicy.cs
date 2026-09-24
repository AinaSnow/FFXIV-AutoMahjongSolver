using Mahjong.Engine;
namespace Mahjong.Policy.Efficiency;

/// <summary>Compares skipping with the best post-call discard; kan kinds have distinct structural effects.</summary>
public sealed class EnhancedCallPolicy(IRuleSet rules) : ICallPolicy
{
    public Decision<MeldCandidate?> Evaluate(StateSnapshot state)
    {
        int before = Shanten(state.Hand,state.OurMelds.Count);
        if ((state.Hand.Count + 3*state.OurMelds.Count)==14)
            before = UkeireEnumerator.Enumerate(Hand.FromTiles(state.Hand,state.OurMelds)).Min(c=>c.ShantenAfter);
        var options = new List<(MeldCandidate Call,int Shanten,int Waits)>();
        foreach (var call in state.Legal.PonCandidates.Concat(state.Legal.ChiCandidates).Concat(state.Legal.KanCandidates))
        {
            var hand = state.Hand.ToList(); bool valid=true;
            foreach(var tile in call.HandTiles) if (!hand.Remove(tile)) { valid=false; break; }
            if (!valid) continue;
            var melds=state.OurMelds.ToList();
            if(call.Kind==MeldKind.ShouMinKan)
            {
                int index=melds.FindIndex(m=>m.Kind==MeldKind.Pon && m.Tiles[0]==call.ClaimedTile);
                if(index<0) continue;
                melds[index]=Meld.ShouMinKan(call.ClaimedTile,call.ClaimedTile,melds[index].ClaimedFromSeat);
            }
            else
            {
                var tiles=call.Kind==MeldKind.AnKan ? call.HandTiles : call.HandTiles.Append(call.ClaimedTile).ToArray();
                melds.Add(new Meld(call.Kind,tiles,call.Kind==MeldKind.AnKan?null:call.ClaimedTile,call.FromSeat));
            }
            var counts=new int[34]; foreach(var tile in hand) counts[tile.Id]++;
            bool stillClosed=melds.All(m=>m.Kind==MeldKind.AnKan);
            if(!stillClosed && HeuristicCallPolicy.EstimateReachableHan(counts,melds.Count,state,call,rules.DoraRule,rules.AllowsKuitan)<rules.MinHan) continue;
            int shanten; int waits=0;
            if(hand.Count + 3*melds.Count==14)
            {
                var forbidden = Kuikae.ForbiddenDiscards(call);
                var choices=UkeireEnumerator.Enumerate(Hand.FromTiles(hand,melds),DiscardScorer.BuildVisibleWall(state))
                    .Where(c=>!forbidden.Contains(c.Discard))
                    .OrderBy(c=>c.ShantenAfter).ThenByDescending(c=>c.WeightedCount).ToArray();
                if (choices.Length == 0) continue;
                var best = choices[0];
                shanten=best.ShantenAfter;waits=best.WeightedCount;
            }
            else shanten=Shanten(hand,melds.Count);
            bool kan=call.Kind is MeldKind.AnKan or MeldKind.MinKan or MeldKind.ShouMinKan;
            // Kan adds variance and exposes another dora. Never add it against a confirmed threat.
            if(kan && (state.Seats.Where((_,i)=>i!=state.OurSeat).Any(s=>s.Riichi) || state.WallRemaining<8)) continue;
            if(shanten<before || (kan && stillClosed && shanten==before)) options.Add((call,shanten,waits));
        }
        if(options.Count==0) return new(false,null,new Reason("enhanced-call-skip","skip has no worse efficiency or lower kan risk"));
        var bestCall=options.OrderBy(c=>c.Shanten).ThenByDescending(c=>c.Waits).First();
        return new(true,bestCall.Call,new Reason("enhanced-call",$"skip shanten={before}; {bestCall.Call.Kind} then best discard shanten={bestCall.Shanten}, waits={bestCall.Waits}"));
    }
    private static int Shanten(IEnumerable<Tile> hand,int melds)
    {
        var c=new int[34];foreach(var tile in hand)c[tile.Id]++;
        return Math.Min(ShantenCalculator.Standard(c,melds),melds==0?Math.Min(ShantenCalculator.Chiitoitsu(c),ShantenCalculator.Kokushi(c)):8);
    }
}
