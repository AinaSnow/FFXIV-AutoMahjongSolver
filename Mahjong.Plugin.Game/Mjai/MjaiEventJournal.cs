using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mahjong.Plugin.Game.Mjai;

/// <summary>Append-only newline-delimited mjai history suitable for process replay.</summary>
public sealed class MjaiEventJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object sync = new();
    private readonly List<string> lines = [];

    public int Count
    {
        get
        {
            lock (sync)
                return lines.Count;
        }
    }

    public string Append(IMjaiEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        string json = JsonSerializer.Serialize(evt, evt.GetType(), JsonOptions);
        lock (sync)
            lines.Add(json);
        return json;
    }

    public string[] Snapshot()
    {
        lock (sync)
            return lines.ToArray();
    }

    public string ToReplayText()
    {
        lock (sync)
            return lines.Count == 0
                ? string.Empty
                : string.Join("\n", lines) + "\n";
    }

    public void Clear()
    {
        lock (sync)
            lines.Clear();
    }
}
