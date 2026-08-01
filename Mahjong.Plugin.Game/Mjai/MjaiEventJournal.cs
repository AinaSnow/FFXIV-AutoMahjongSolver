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
        string json = Serialize(evt);
        lock (sync)
            lines.Add(json);
        return json;
    }

    /// <summary>
    /// Replaces the most recent event when a provisional live observation is
    /// superseded by its authoritative network packet.
    /// </summary>
    public bool TryReplaceLast(IMjaiEvent expected, IEnumerable<IMjaiEvent> replacements)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(replacements);
        string expectedJson = Serialize(expected);
        string[] replacementJson = replacements.Select(Serialize).ToArray();
        lock (sync)
        {
            if (lines.Count == 0
                || !string.Equals(lines[^1], expectedJson, StringComparison.Ordinal))
            {
                return false;
            }

            lines.RemoveAt(lines.Count - 1);
            lines.AddRange(replacementJson);
            return true;
        }
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

    private static string Serialize(IMjaiEvent evt) =>
        JsonSerializer.Serialize(evt, evt.GetType(), JsonOptions);
}
