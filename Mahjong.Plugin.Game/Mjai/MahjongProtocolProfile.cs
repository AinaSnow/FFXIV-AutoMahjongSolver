using System.Globalization;
using System.Text.Json;

namespace Mahjong.Plugin.Game.Mjai;

public sealed record MahjongPacketSpec(int MessageId, int PayloadLength);
public sealed record MahjongProtocolProfile(string GameVersion, string Variant, bool Verified,
    string Evidence, Dictionary<string, MahjongPacketSpec> Packets)
{
    public bool Matches(string? version, string? variant) => Verified && !string.IsNullOrWhiteSpace(Evidence)
        && !string.IsNullOrWhiteSpace(version) && GameVersion == version && Variant == variant;
    public bool TryGet(ushort opcode, out MahjongPacketSpec? spec) =>
        Packets.TryGetValue($"0x{opcode:X4}", out spec);
    public static MahjongProtocolProfile[] LoadDirectory(string path)
    {
        if (!Directory.Exists(path)) return [];
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var result = new List<MahjongProtocolProfile>();
        foreach (var file in Directory.EnumerateFiles(path, "*.json"))
        {
            var profile = JsonSerializer.Deserialize<MahjongProtocolProfile>(File.ReadAllText(file), options)
                ?? throw new InvalidDataException($"Empty protocol profile {file}");
            if (profile.Packets is null) throw new InvalidDataException("Missing packet map");
            var normalized = new Dictionary<string, MahjongPacketSpec>();
            foreach (var (key, spec) in profile.Packets)
            {
                if (!key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    || !ushort.TryParse(key.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var opcode)
                    || spec.PayloadLength is < 1 or > 4096)
                    throw new InvalidDataException($"Invalid packet spec {key}");
                normalized.Add($"0x{opcode:X4}", spec);
            }
            result.Add(profile with { Packets = normalized });
        }
        return result.ToArray();
    }
}
