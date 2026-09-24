namespace Mahjong.Plugin.Game.Tests;

public class DomanTurnTrackerTests
{
    private static StateSnapshot State(long hand = 1, int discards = 2) => StateSnapshot.Empty with
    {
        HandId = hand, Hand = Tiles.Parse("123456789m55p"),
        Seats = [new SeatView([], [], [], false, -1, false, false, discards),
            StateSnapshot.Empty.Seats[1], StateSnapshot.Empty.Seats[2], StateSnapshot.Empty.Seats[3]],
        Legal = new(ActionFlags.Discard | ActionFlags.Riichi, [], [], [], []),
    };

    [Fact]
    public void Cancel_drops_an_unconfirmed_intent_but_preserves_committed_riichi()
    {
        var tracker = new DomanTurnTracker();
        tracker.DeclarationDispatched(State()); tracker.CancelUnconfirmedDeclaration();
        Assert.False(tracker.Observe(State(discards: 3)).OurRiichi);
        tracker.DeclarationDispatched(State(discards: 3));
        Assert.True(tracker.Observe(State(discards: 4)).OurRiichi);
        tracker.CancelUnconfirmedDeclaration();
        Assert.True(tracker.Observe(State(discards: 4)).OurRiichi);
    }

    [Fact]
    public void Declaration_discard_must_land_before_auto_discard_takes_over()
    {
        var tracker = new DomanTurnTracker(); var start = State();
        tracker.Observe(start); tracker.DeclarationDispatched(start);
        Assert.False(tracker.Observe(start).OurRiichi);
        Assert.True(tracker.Observe(start).Legal.Can(ActionFlags.Discard));
        var committed = tracker.Observe(State(discards: 3));
        Assert.True(committed.OurRiichi);
        Assert.False(committed.Legal.Can(ActionFlags.Discard | ActionFlags.Riichi));
        // Re-applying a raw UI frame or toggling automation must not lose table history.
        Assert.True(tracker.Apply(State(discards: 3)).OurRiichi);
        Assert.False(tracker.Observe(State(hand: 2)).OurRiichi);
    }

    [Theory]
    [InlineData(ActionFlags.Tsumo)] [InlineData(ActionFlags.Ron)] [InlineData(ActionFlags.AnKan)]
    public void Riichi_preserves_visible_win_and_legal_concealed_kan_prompts(ActionFlags prompt)
    {
        var tracker = new DomanTurnTracker(); var start = State();
        tracker.DeclarationDispatched(start);
        var result = tracker.Observe(State(discards: 3) with
        { Legal = start.Legal with { Flags = start.Legal.Flags | prompt } });
        Assert.True(result.Legal.Can(prompt));
        Assert.False(result.Legal.Can(ActionFlags.Discard));
    }

    [Fact]
    public void Chi_restriction_expires_on_the_first_discard_and_survives_transient_frames()
    {
        var tracker = new DomanTurnTracker(); tracker.Observe(State());
        var called = State() with { OurMelds = [Meld.Chi(Tile.FromId(0), Tile.FromId(0), 3)] };
        var result = tracker.Observe(called);
        Assert.True(result.Legal.DiscardRestrictionKnown);
        Assert.False(result.Legal.AllowsDiscard(Tile.FromId(0)));
        Assert.False(result.Legal.AllowsDiscard(Tile.FromId(3)));
        Assert.True(result.Legal.AllowsDiscard(Tile.FromId(8)));
        Assert.False(tracker.Observe(called).Legal.AllowsDiscard(Tile.FromId(3)));
        var discarded = called with { Seats = State(discards: 3).Seats };
        Assert.True(tracker.Observe(discarded).Legal.AllowsDiscard(Tile.FromId(3)));
    }

    [Fact]
    public void Attach_mid_hand_does_not_treat_old_meld_as_a_new_call()
    {
        var tracker = new DomanTurnTracker();
        var state = State() with { OurMelds = [Meld.Pon(Tile.FromId(0), Tile.FromId(0), 1)] };
        Assert.False(tracker.Observe(state).Legal.DiscardRestrictionKnown);
        tracker.Reset(); Assert.False(tracker.Observe(state).OurRiichi);
    }

    [Fact]
    public void Known_empty_discard_set_remains_empty()
    {
        var tracker = new DomanTurnTracker(); tracker.Observe(State());
        var called = State() with { Hand = [Tile.FromId(0)], OurMelds = [Meld.Pon(Tile.FromId(0), Tile.FromId(0), 1)] };
        var result = tracker.Observe(called);
        Assert.Empty(result.Legal.DiscardableTiles);
        Assert.False(result.Legal.AllowsDiscard(Tile.FromId(0)));
    }
}
