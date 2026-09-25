using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Dalamud.Actions;

/// <summary>Keep server semantics separate from UI transitions and callback return values.</summary>
internal sealed class ServerCallOutcomeTracker
{
    internal sealed record Result(long ActionId, string Expected, string Actual, string Status, string Path);
    private (long Id, string Label, string Path, DateTime At)? pending;
    internal void Reset() => pending = null;
    internal void Track(long id, string label, string path, DateTime at)
    {
        if (label is "ron" or "tsumo" or "pon" or "chi" or "chi-variant" or "ankan" or "minkan" or "shouminkan" or "pass")
            pending = (id, label == "chi-variant" ? "chi" : label, path, at);
    }
    internal Result? Observe(IMjaiEvent evt, DateTime now)
    {
        if (evt is MjaiStartGame or MjaiStartKyoku or MjaiEndKyoku or MjaiEndGame) { Reset(); return null; }
        if (pending is not { } expected) return null;
        if (now - expected.At > TimeSpan.FromSeconds(5)) { Reset(); return null; }
        string? actual = evt switch
        {
            MjaiOpenCall { Actor: 0 } call => call.Type,
            MjaiAnkan { Actor: 0 } => "ankan",
            MjaiKakan { Actor: 0 } => "shouminkan",
            MjaiDaiminkan { Actor: 0 } => "minkan",
            _ => null,
        };
        if (actual is null) return null;
        Reset();
        return new(expected.Id, expected.Label, actual,
            actual == expected.Label ? "server-confirmed" : "server-action-mismatch", expected.Path);
    }
}
