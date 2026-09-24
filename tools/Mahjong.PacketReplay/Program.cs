using System.Security.Cryptography;
using System.Text.Json;
using Mahjong.Plugin.Game.Mjai;
using Mahjong.Replay;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: PacketReplay <candidate-profile.json> <capture.ndjson|fixture.json> <new-output.json>");
    return 2;
}
try
{
    var profile = JsonSerializer.Deserialize<MahjongProtocolProfile>(File.ReadAllText(args[0]),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("Empty candidate profile.");
    if (profile.Packets is null) throw new InvalidDataException("Missing candidate packet map.");
    string input = File.ReadAllText(args[1]);
    var replay = CapturedPacketReplay.Read(input, profile,
        Path.GetExtension(args[1]).Equals(".ndjson", StringComparison.OrdinalIgnoreCase));
    var result = new
    {
        source_sha256 = Hash(args[1]), profile_sha256 = Hash(args[0]),
        decoder_sha256 = Hash(typeof(MahjongPacketMjaiDecoder).Assembly.Location),
        game_version = profile.GameVersion, variant = profile.Variant,
        profile_verified = profile.Verified, replay,
    };
    using var output = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write);
    JsonSerializer.Serialize(output, result, new JsonSerializerOptions
    { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
    Console.WriteLine($"Exported {replay.Hands.Count} hands, {replay.Hands.Count(h => h.OfflineModelEligible)} eligible for offline model diagnostics. This export does not enable runtime inference.");
    return 0;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException)
{
    Console.Error.WriteLine($"Replay export failed: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
