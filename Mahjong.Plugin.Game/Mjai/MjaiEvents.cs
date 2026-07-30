using System.Text.Json.Serialization;

namespace Mahjong.Plugin.Game.Mjai;

public interface IMjaiEvent
{
    string Type { get; }
}

public sealed record MjaiStartGame : IMjaiEvent
{
    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public string Type => "start_game";
}

public sealed record MjaiStartKyoku(
    [property: JsonPropertyName("bakaze")] string Bakaze,
    [property: JsonPropertyName("dora_marker")] string DoraMarker,
    [property: JsonPropertyName("kyoku")] int Kyoku,
    [property: JsonPropertyName("honba")] int Honba,
    [property: JsonPropertyName("kyotaku")] int Kyotaku,
    [property: JsonPropertyName("oya")] int Oya,
    [property: JsonPropertyName("scores")] int[] Scores,
    [property: JsonPropertyName("tehais")] string[][] Tehais) : IMjaiEvent
{
    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public string Type => "start_kyoku";
}

public sealed record MjaiTsumo(
    [property: JsonPropertyName("actor")] int Actor,
    [property: JsonPropertyName("pai")] string Pai) : IMjaiEvent
{
    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public string Type => "tsumo";
}

public sealed record MjaiDahai(
    [property: JsonPropertyName("actor")] int Actor,
    [property: JsonPropertyName("pai")] string Pai,
    [property: JsonPropertyName("tsumogiri")] bool Tsumogiri) : IMjaiEvent
{
    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public string Type => "dahai";
}

public sealed record MjaiReach(
    [property: JsonPropertyName("actor")] int Actor) : IMjaiEvent
{
    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public string Type => "reach";
}

public sealed record MjaiReachAccepted(
    [property: JsonPropertyName("actor")] int Actor) : IMjaiEvent
{
    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public string Type => "reach_accepted";
}

public sealed record MjaiDora(
    [property: JsonPropertyName("dora_marker")] string DoraMarker) : IMjaiEvent
{
    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public string Type => "dora";
}

public sealed record MjaiOpenCall : IMjaiEvent
{
    private static readonly HashSet<string> AllowedTypes = ["chi", "pon", "daiminkan"];

    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public string Type { get; }

    [JsonPropertyName("actor")]
    public int Actor { get; }

    [JsonPropertyName("target")]
    public int Target { get; }

    [JsonPropertyName("pai")]
    public string Pai { get; }

    [JsonPropertyName("consumed")]
    public string[] Consumed { get; }

    public MjaiOpenCall(string type, int actor, int target, string pai, string[] consumed)
    {
        if (!AllowedTypes.Contains(type))
            throw new ArgumentOutOfRangeException(nameof(type), type, "unsupported open-call type");

        Type = type;
        Actor = actor;
        Target = target;
        Pai = pai;
        Consumed = consumed;
    }
}
