using System.Text.Json;
using Mahjong.Core;
using Mahjong.Policy.Efficiency;
using Mahjong.Plugin.Game.Mjai;

// One request/response per JSON line. Only censored, relative-player MJAI history is accepted.
if (args.Contains("--describe"))
{
    var hashes=Directory.GetFiles(AppContext.BaseDirectory,"Mahjong.*.dll").ToDictionary(path=>Path.GetFileName(path)!,
        path=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
    Console.WriteLine(JsonSerializer.Serialize(new { schema=StateSnapshot.CurrentSchemaVersion,weights=Mahjong.Policy.Abstractions.Weights.WeightBundle.CurrentSchemaVersion,hashes }));
    return;
}
var policy = new EfficiencyPolicy();
while (Console.ReadLine() is { } line)
{
    try
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var reducer = new PublicStateReducer { ScheduledRounds = root.GetProperty("scheduled_rounds").GetInt32() };
        foreach (var evt in root.GetProperty("events").EnumerateArray()) reducer.ApplyJson(evt.GetRawText());
        var legal = root.GetProperty("legal");
        var discards = legal.GetProperty("discards").EnumerateArray().Select(v => Parse(v.GetString()!)).ToArray();
        var calls = legal.GetProperty("calls").EnumerateArray().Select(c => new MeldCandidate(
            Enum.Parse<MeldKind>(c.GetProperty("kind").GetString()!), Parse(c.GetProperty("pai").GetString()!),
            c.GetProperty("consumed").EnumerateArray().Select(t => Parse(t.GetString()!)).ToArray(), c.GetProperty("target").GetInt32())).ToArray();
        var actions = new LegalActions((ActionFlags)legal.GetProperty("flags").GetInt32(), discards,
            calls.Where(c=>c.Kind==MeldKind.Pon).ToArray(),calls.Where(c=>c.Kind==MeldKind.Chi).ToArray(),
            calls.Where(c=>c.Kind is MeldKind.AnKan or MeldKind.MinKan or MeldKind.ShouMinKan).ToArray(), DiscardRestrictionKnown: true);
        var state = reducer.Snapshot(actions);
        if (!state.PublicStateConsistent) throw new InvalidDataException(reducer.Failure ?? "inconsistent state");
        var result = policy.Analyze(state);
        if (root.TryGetProperty("enhanced",out var enhanced) && enhanced.GetBoolean())
            result = EnhancedSearch.Analyze(state,result,TimeSpan.FromMilliseconds(50), calibration:
                root.TryGetProperty("calibration_path",out var cp) && cp.ValueKind==JsonValueKind.String ? RankCalibration.Load(cp.GetString()!) : null);
        Console.WriteLine(JsonSerializer.Serialize(new { kind=result.Choice.Kind.ToString(),
            tile=result.Choice.DiscardTile is {} t ? MjaiTile.Format(t,result.Choice.DiscardIsRed==true) : null,
            call=result.Choice.Call is {} c ? new { kind=c.Kind.ToString(), pai=MjaiTile.Format(c.ClaimedTile),
                consumed=c.HandTiles.Select(t=>MjaiTile.Format(t)).ToArray(),target=c.FromSeat } : null,
            calibration_key=result.Choice.DiscardTile is {} selected && result.Candidates.FirstOrDefault(c=>c.Discard==selected) is var candidate && candidate.Discard==selected
                ? RankCalibration.Key(state,candidate) : null,
            reason=result.Choice.Reasoning, hand=state.HandId, revision=state.Revision }));
    }
    catch (Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new { error=$"{ex.GetType().Name}: {ex.Message}" })); }
}
static Tile Parse(string value) => MjaiTile.TryParse(value,out var tile,out _) ? tile : throw new InvalidDataException($"Unknown tile: {value}");
