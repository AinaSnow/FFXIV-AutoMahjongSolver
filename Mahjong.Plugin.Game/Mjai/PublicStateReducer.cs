using System.Text.Json;

namespace Mahjong.Plugin.Game.Mjai;

/// <summary>Public event history, always relative to player zero. No opponent concealed information is exposed.</summary>
public sealed class PublicStateReducer
{
    private readonly List<Tile>[] rivers = [[], [], [], []];
    private readonly List<bool>[] redRivers = [[], [], [], []];
    private readonly List<bool>[] called = [[], [], [], []];
    private readonly int[] meldRed = new int[4];
    private readonly bool[] ippatsu = new bool[4];
    private readonly List<bool>[] tedashi = [[], [], [], []];
    private readonly List<Meld>[] melds = [[], [], [], []];
    private readonly bool[] riichi = new bool[4];
    private readonly int[] reachIndex = [-1, -1, -1, -1];
    private readonly List<Tile> dora = [];
    private readonly List<(Tile Tile, bool Red)> hand = [];
    private int[] scores = [25000, 25000, 25000, 25000];
    private int dealer, round, kyoku, honba, sticks, wall;
    private bool countersKnown, handKnown;
    public long HandId { get; private set; }
    public long Revision { get; private set; }
    public bool Active { get; private set; }
    public bool Complete { get; private set; }
    public string? Failure { get; private set; }
    public int? ScheduledRounds { get; set; }

    public void Invalidate(string reason) { Complete = false; Failure = reason; Revision++; }
    public void Reset() { Active = false; Complete = false; hand.Clear(); Revision++; }

    public void Apply(IMjaiEvent evt, bool knownCounters = true) =>
        ApplyJson(JsonSerializer.Serialize(evt, evt.GetType()), knownCounters);

    public void ApplyJson(string json, bool knownCounters = true)
    {
        using var document = JsonDocument.Parse(json);
        var e = document.RootElement;
        string type = e.GetProperty("type").GetString()!;
        if (type == "start_game") { Reset(); return; }
        if (type == "start_kyoku")
        {
            foreach (var x in rivers) x.Clear();
            foreach (var x in redRivers) x.Clear();
            foreach (var x in tedashi) x.Clear();
            foreach (var x in called) x.Clear();
            Array.Clear(meldRed); Array.Clear(ippatsu);
            foreach (var x in melds) x.Clear();
            Array.Clear(riichi); Array.Fill(reachIndex, -1);
            dora.Clear(); hand.Clear();
            dealer = e.GetProperty("oya").GetInt32();
            if (dealer is < 0 or > 3) throw new InvalidDataException("Invalid dealer");
            round = e.GetProperty("bakaze").GetString() switch { "E" => 0, "S" => 1, "W" => 2, "N" => 3, _ => -1 };
            kyoku = e.GetProperty("kyoku").GetInt32();
            honba = e.GetProperty("honba").GetInt32();
            sticks = e.GetProperty("kyotaku").GetInt32();
            scores = e.GetProperty("scores").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            if (scores.Length != 4 || round < 0 || kyoku is < 1 or > 4) throw new InvalidDataException("Invalid round context");
            countersKnown = knownCounters;
            Complete = true; Failure = null; Active = true; wall = 70; HandId++; Revision++;
            AddDora(e.GetProperty("dora_marker").GetString());
            handKnown = true;
            foreach (var value in e.GetProperty("tehais")[0].EnumerateArray())
                if (TryTile(value.GetString(), out var tile)) hand.Add(tile); else handKnown = false;
            if (!handKnown) Invalidate("unknown starting hand");
            return;
        }
        if (!Active) return;
        Revision++;
        int actor = e.TryGetProperty("actor", out var actorValue) ? actorValue.GetInt32() : -1;
        if (actor is < -1 or > 3) throw new InvalidDataException("Invalid actor");
        switch (type)
        {
            case "tsumo":
                wall = Math.Max(0, wall - 1);
                if (actor == 0)
                {
                    if (TryTile(e.GetProperty("pai").GetString(), out var tile)) hand.Add(tile);
                    else Invalidate("unknown self draw");
                }
                break;
            case "dahai":
                RequireActor(actor);
                if (!TryTile(e.GetProperty("pai").GetString(), out var discard)) { Invalidate("unknown discard"); break; }
                rivers[actor].Add(discard.Tile); redRivers[actor].Add(discard.Red);
                called[actor].Add(false);
                if (riichi[actor]) ippatsu[actor] = false;
                tedashi[actor].Add(!e.GetProperty("tsumogiri").GetBoolean());
                if (actor == 0) Remove(discard);
                break;
            case "reach":
                RequireActor(actor); reachIndex[actor] = rivers[actor].Count; break;
            case "reach_accepted":
                RequireActor(actor);
                if (!riichi[actor]) { riichi[actor] = true; ippatsu[actor] = true; scores[actor] -= 1000; sticks++; }
                break;
            case "chi": case "pon": case "daiminkan": case "ankan": case "kakan":
                RequireActor(actor);
                Array.Clear(ippatsu);
                var consumed = new List<(Tile Tile, bool Red)>();
                foreach (var value in e.GetProperty("consumed").EnumerateArray())
                    if (TryTile(value.GetString(), out var tile)) consumed.Add(tile);
                    else { Invalidate("unknown meld tile"); return; }
                int target = e.TryGetProperty("target", out var targetValue) ? targetValue.GetInt32() : -1;
                (Tile Tile, bool Red) claimed = default;
                if (type != "ankan" && !TryTile(e.GetProperty("pai").GetString(), out claimed)) { Invalidate("unknown claimed tile"); break; }
                meldRed[actor] += type == "kakan" ? (claimed.Red ? 1 : 0) : consumed.Count(t => t.Red) + (claimed.Red ? 1 : 0);
                if (type is "chi" or "pon" or "daiminkan")
                {
                    RequireActor(target);
                    int index = rivers[target].Count - 1;
                    if (index < 0 || rivers[target][index] != claimed.Tile || called[target][index])
                        Invalidate("claimed discard does not match history");
                    else called[target][index] = true;
                }
                if (actor == 0)
                {
                    if (type == "kakan") Remove(claimed);
                    else foreach (var tile in consumed) Remove(tile);
                }
                if (type == "kakan")
                {
                    int index = melds[actor].FindIndex(m => m.Kind == MeldKind.Pon && m.Tiles[0] == claimed.Tile);
                    if (index < 0) { Invalidate("kakan without pon"); break; }
                    var previous = melds[actor][index];
                    melds[actor][index] = Meld.ShouMinKan(claimed.Tile, claimed.Tile, previous.ClaimedFromSeat);
                }
                else if (type == "ankan")
                {
                    if (consumed.Count != 4) { Invalidate("invalid ankan"); break; }
                    melds[actor].Add(Meld.AnKan(consumed[0].Tile));
                }
                else
                {
                    RequireActor(target);
                    var tiles = consumed.Select(x => x.Tile).Append(claimed.Tile).OrderBy(x => x.Id).ToArray();
                    if (tiles.Length != (type == "daiminkan" ? 4 : 3)) { Invalidate("invalid open meld"); break; }
                    melds[actor].Add(new Meld(type == "chi" ? MeldKind.Chi : type == "pon" ? MeldKind.Pon : MeldKind.MinKan, tiles, claimed.Tile, target));
                }
                break;
            case "dora": AddDora(e.GetProperty("dora_marker").GetString()); break;
            case "end_kyoku": case "end_game": Active = false; break;
        }
    }

    public StateSnapshot Snapshot(LegalActions legal) => Merge(StateSnapshot.Empty with
    {
        Hand = hand.Select(x => x.Tile).ToArray(), HandIsRed = hand.Select(x => x.Red).ToArray(),
        Observations = SnapshotObservationFlags.HandRedIdentity, Legal = legal,
    });

    public StateSnapshot Merge(StateSnapshot ui)
    {
        if (!Active) return ui;
        bool redAgrees = !ui.Observations.HasFlag(SnapshotObservationFlags.HandRedIdentity) ||
            hand.Select(x => (x.Tile.Id, x.Red)).Order().SequenceEqual(ui.Hand.Select((t, i) => (t.Id, i < ui.HandIsRed.Count && ui.HandIsRed[i])).Order());
        bool agrees = redAgrees && (!handKnown || hand.Select(x => x.Tile.Id).Order().SequenceEqual(ui.Hand.Select(x => x.Id).Order()));
        var flags = ui.Observations | SnapshotObservationFlags.SeatInfo | SnapshotObservationFlags.RoundContext
            | SnapshotObservationFlags.PublicDiscardTiles | SnapshotObservationFlags.PublicDiscardRedIdentity
            | SnapshotObservationFlags.PublicTedashi | SnapshotObservationFlags.OpponentRiichi
            | SnapshotObservationFlags.OpponentMelds | SnapshotObservationFlags.Dora | SnapshotObservationFlags.Wall;
        if (countersKnown) flags |= SnapshotObservationFlags.Honba | SnapshotObservationFlags.RiichiSticks;
        if (!Complete) flags = ui.Observations;
        return ui with
        {
            OurSeat = 0, SeatWind = (4 - dealer) % 4, DealerSeat = dealer, RoundWind = round,
            SeatInfoKnown = Complete, Kyoku = kyoku, ScheduledRounds = ScheduledRounds,
            // Dealer advances exactly once per kyoku; repeats do not advance it.
            InitialDealerSeat = Complete ? (dealer - (kyoku - 1) + 4) % 4 : null,
            Honba = countersKnown ? honba : ui.Honba, RiichiSticks = countersKnown ? sticks : ui.RiichiSticks,
            Scores = (int[])scores.Clone(), OurRiichi = riichi[0], OurIppatsu = ippatsu[0], AkaDora = hand.Count(t => t.Red) + meldRed[0], TurnIndex = rivers[0].Count, OurMelds = melds[0].ToArray(),
            Seats = Enumerable.Range(0, 4).Select(i => new SeatView(rivers[i], tedashi[i], melds[i], riichi[i], reachIndex[i], ippatsu[i], false, rivers[i].Count, redRivers[i], called[i])).ToArray(),
            DoraIndicators = dora.ToArray(), WallRemaining = wall, HandId = HandId, Revision = Revision,
            PublicStateConsistent = Complete && agrees, Observations = flags,
        };
    }

    private static void RequireActor(int actor) { if (actor is < 0 or > 3) throw new InvalidDataException("Missing actor"); }
    private static bool TryTile(string? text, out (Tile Tile, bool Red) tile)
    {
        bool ok = MjaiTile.TryParse(text, out var kind, out var red); tile = (kind, red); return ok;
    }
    private void Remove((Tile Tile, bool Red) tile)
    {
        int index = hand.FindIndex(x => x == tile);
        if (index < 0) Invalidate("self tile missing from history"); else hand.RemoveAt(index);
    }
    private void AddDora(string? text)
    {
        if (TryTile(text, out var tile)) dora.Add(tile.Tile); else Invalidate("unknown dora");
    }
}
