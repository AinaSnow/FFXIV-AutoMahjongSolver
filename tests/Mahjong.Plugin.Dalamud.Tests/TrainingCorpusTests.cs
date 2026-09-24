using System.Text.Json;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Composition;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Dalamud.Mortal;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.Dalamud.Tests;

public class TrainingCorpusTests
{
    private static readonly MatchArchiveMortalStats Stats = new("test", 0, 0, 0, 0, 0, 0, 0, 0);
    private static readonly MatchArchiveEnvironment EnvironmentInfo = new("test", "Emj", false, "limited", "build");
    private static readonly TrainingProvenance Provenance = new("hash", "policy", "rules", "DomanRuleSet", "stable", false, false, 50, "not-used", null);
    private static string Archive(string root)
    {
        string dir = Path.Combine(root, "source"); Directory.CreateDirectory(Path.Combine(dir, "games"));
        File.WriteAllText(Path.Combine(dir, "summary.json"), """{"packet_write_failed":false,"environment":{"protocol_verified":false}}""");
        File.WriteAllText(Path.Combine(dir, "managed-complete.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "packets.ndjson"), """{"message_id":636}""");
        File.WriteAllText(Path.Combine(dir, "games", "game.ndjson"), """{"e":"state","state_code":27,"hand":[],"scores":[30000,25000,25000,20000]}""");
        return dir;
    }
    private static JsonElement Manifest(string root)
    {
        string path = Directory.GetFiles(root, "manifest.json", SearchOption.AllDirectories).Single();
        using var doc = JsonDocument.Parse(File.ReadAllText(path)); return doc.RootElement.Clone();
    }

    [Fact]
    public void Preserved_copy_has_hashes_stable_partition_and_never_claims_training_readiness()
    {
        using var tmp = new TempDir(); string archive = Archive(tmp.Path);
        var writer = new TrainingCorpusWriter(tmp.Path, () => true, new StubPluginLog());
        string raw = Path.Combine(tmp.Path, "raw.ndjson"); File.WriteAllText(raw, """{"e":"capture-end","stream_complete":true}""");
        var context = new TrainingArchiveContext(raw, Task.CompletedTask, Provenance, "{}");
        Assert.True(writer.Preserve(archive, context));
        var manifest = Manifest(writer.RootDir); string id = manifest.GetProperty("match_id").GetString()!;
        Assert.Equal(TrainingCorpusWriter.Split(id), manifest.GetProperty("split").GetString());
        Assert.False(manifest.GetProperty("training_ready").GetBoolean());
        Assert.Contains("protocol_not_fully_verified", manifest.GetProperty("quality_flags").EnumerateArray().Select(v => v.GetString()));
        foreach (var file in manifest.GetProperty("files").EnumerateArray())
            Assert.Equal(file.GetProperty("sha256").GetString(), TrainingCorpusWriter.HashFile(Path.Combine(writer.RootDir, "match-" + id, file.GetProperty("path").GetString()!)));
        Assert.True(writer.Preserve(archive, context));
        Assert.Single(Directory.GetDirectories(writer.RootDir, "match-*"));
        string unexpected = Path.Combine(writer.RootDir, "match-" + id, "unexpected.txt");
        File.WriteAllText(unexpected, "unexpected");
        Assert.False(writer.Preserve(archive, context));
        File.Delete(unexpected);
        Assert.True(writer.Preserve(archive, context));
        File.WriteAllText(Path.Combine(archive,"games","game.ndjson"),"changed");
        Assert.False(writer.Preserve(archive, context));
        Assert.NotNull(writer.LastError);
    }

    [Fact]
    public async Task Raw_footer_must_finish_before_copying()
    {
        using var tmp = new TempDir(); string archive = Archive(tmp.Path);
        var writer = new TrainingCorpusWriter(tmp.Path, () => true, new StubPluginLog());
        string raw = Path.Combine(tmp.Path, "raw.ndjson"); File.WriteAllText(raw, "partial");
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var save = Task.Run(() => writer.Preserve(archive, new(raw, complete.Task, Provenance, "{}")));
        await Task.Delay(30); Assert.False(save.IsCompleted);
        File.WriteAllText(raw, """{"e":"capture-end","stream_complete":true}"""); complete.SetResult();
        Assert.True(await save.WaitAsync(TimeSpan.FromSeconds(5)));
        string copy = Directory.GetFiles(writer.RootDir, "raw-capture.ndjson", SearchOption.AllDirectories).Single();
        Assert.Equal(File.ReadAllText(raw), File.ReadAllText(copy));
    }

    [Fact]
    public async Task Failed_preservation_protects_source_from_normal_retention()
    {
        using var tmp = new TempDir();
        var corpus = new TrainingCorpusWriter(tmp.Path, () => true, new StubPluginLog());
        File.WriteAllText(corpus.RootDir, "simulate unwritable destination");
        string game = Path.Combine(tmp.Path, "game.ndjson"); File.WriteAllText(game, """{"e":"state","scores":[25000,25000,25000,25000]}""");
        using var writer = new MatchArchiveWriter(tmp.Path, new StubPluginLog(), retention: () => (1, 1), trainingCorpus: corpus);
        string first = (await writer.FinalizeSessionAsync([game], Stats, EnvironmentInfo))!;
        Assert.True(File.Exists(Path.Combine(first, "training-pending.json")));
        File.SetLastWriteTimeUtc(Path.Combine(first,"managed-complete.json"), DateTime.UtcNow.AddDays(-50));
        await writer.FinalizeSessionAsync([game], Stats, EnvironmentInfo);
        Assert.True(Directory.Exists(first)); Assert.NotNull(corpus.LastError);
    }

    [Fact]
    public async Task Ordinary_retention_cannot_remove_independent_training_copy()
    {
        using var tmp = new TempDir();
        var corpus = new TrainingCorpusWriter(tmp.Path, () => true, new StubPluginLog());
        using var writer = new MatchArchiveWriter(tmp.Path, new StubPluginLog(), retention: () => (1, 1), trainingCorpus: corpus);
        string game = Path.Combine(tmp.Path, "game.ndjson"); File.WriteAllText(game, """{"e":"state","scores":[25000,25000,25000,25000]}""");
        string first = (await writer.FinalizeSessionAsync([game], Stats, EnvironmentInfo))!;
        string saved = Directory.GetDirectories(corpus.RootDir,"match-*").Single();
        File.SetLastWriteTimeUtc(Path.Combine(first,"managed-complete.json"), DateTime.UtcNow.AddDays(-50));
        File.AppendAllText(game, "\n" + """{"e":"state","scores":[30000,20000,25000,25000]}""");
        await writer.FinalizeSessionAsync([game], Stats, EnvironmentInfo);
        Assert.False(Directory.Exists(first)); Assert.True(File.Exists(Path.Combine(saved,"manifest.json")));
    }

    [Fact]
    public void Absent_opening_or_final_never_enters_a_training_partition()
    {
        using var tmp = new TempDir(); string archive = Archive(tmp.Path);
        File.WriteAllText(Path.Combine(archive,"packets.ndjson"), """{"message_id":635}""");
        File.WriteAllText(Path.Combine(archive,"games","game.ndjson"), """{"e":"state","scores":[25000,25000,25000,25000]}""");
        var writer = new TrainingCorpusWriter(tmp.Path, () => true, new StubPluginLog());
        Assert.True(writer.Preserve(archive, null)); var manifest = Manifest(writer.RootDir);
        Assert.Equal("unassigned", manifest.GetProperty("split").GetString());
        Assert.False(manifest.GetProperty("final_result_observed").GetBoolean());
    }

    [Fact]
    public async Task Closing_capture_for_archive_is_once_only_and_preserves_footer()
    {
        using var tmp = new TempDir(); using var recorder = new AutoPacketRecorder(tmp.Path);
        recorder.Update(true, true, EnvironmentInfo);
        var capture = Assert.IsType<DebugPacketSession>(recorder.CloseForArchive());
        Assert.Null(recorder.CloseForArchive()); Assert.False(recorder.IsRecording);
        await capture.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("capture-end",File.ReadAllText(capture.Path));
        recorder.Update(true,true,EnvironmentInfo);
        var next = Assert.IsType<DebugPacketSession>(recorder.CloseForArchive());
        Assert.NotEqual(capture.Path,next.Path); await next.Completion;
    }

    [Fact]
    public async Task Logger_keeps_exact_revision_unknown_context_and_external_input()
    {
        using var tmp = new TempDir(); var cfg = new DalamudConfigService(_ => {}, new Configuration());
        using var logger = new GameLogger(cfg,new StubPluginLog(),tmp.Path);
        var state = StateSnapshot.Empty with { Hand = Enumerable.Range(0,14).Select(i => Tile.FromId(i)).ToArray(), HandId = 4, Revision = 8 };
        logger.OnStateChanged(state); logger.OnStateChanged(state with { Revision = 9 });
        logger.RecordInput(new(DateTime.UtcNow, "Emj", 1, false, true, [7], false));
        logger.RecordAction(ActionKind.Discard,Tile.FromId(4),0,"Ok","test",true);
        await logger.FlushAsync();
        var rows = Directory.GetFiles(logger.GamesDir).SelectMany(File.ReadLines).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        var states = rows.Where(r=>r.GetProperty("e").GetString()=="state").ToArray();
        Assert.Equal(2,states.Length); Assert.Equal(9,states[1].GetProperty("revision").GetInt64());
        Assert.Equal(4,states[0].GetProperty("hand_id").GetInt64()); Assert.False(states[0].TryGetProperty("seat_wind",out _));
        Assert.False(rows.Single(r=>r.GetProperty("e").GetString()=="input-callback").GetProperty("automated").GetBoolean());
        Assert.True(rows.Single(r=>r.GetProperty("e").GetString()=="action").GetProperty("is_red").GetBoolean());
    }

    [Fact]
    public void Weight_identity_matches_retained_bytes_and_model_unknown_is_explicit()
    {
        var provider = new TrainingProvenanceProvider(new DefaultWeightProvider(), () => new Configuration { MortalEnabled = true }, () => null);
        var provenance = provider.Snapshot();
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(provider.WeightsJson))), provenance.PolicyWeightsSha256);
        Assert.Null(provenance.MortalModel); Assert.Equal("not-used",provenance.CalibrationIdentity);
    }
    [Fact]
    public async Task Unparsed_raw_only_match_is_still_preserved_for_future_decoders()
    {
        using var tmp = new TempDir();
        var corpus = new TrainingCorpusWriter(tmp.Path, () => true, new StubPluginLog());
        using var writer = new MatchArchiveWriter(tmp.Path, new StubPluginLog(), trainingCorpus: corpus);
        string raw = Path.Combine(tmp.Path,"raw.ndjson");
        File.WriteAllText(raw, """{"e":"capture-end","stream_complete":false}""");
        await writer.FinalizeSessionAsync([],Stats,EnvironmentInfo,new(raw,Task.CompletedTask,Provenance,"{}"));
        Assert.Equal("unassigned",Manifest(corpus.RootDir).GetProperty("split").GetString());
        Assert.Single(Directory.GetFiles(corpus.RootDir,"raw-capture.ndjson",SearchOption.AllDirectories));
    }

    [Fact]
    public void Training_retention_defaults_on_but_explicit_opt_out_round_trips()
    {
        var cfg = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>("""{"Version":3,"MortalEnabled":true}""")!;
        Assert.True(cfg.RetainTrainingData); Assert.True(cfg.MortalEnabled);
        var off = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(Newtonsoft.Json.JsonConvert.SerializeObject(cfg with { RetainTrainingData = false }))!;
        Assert.False(off.RetainTrainingData);
    }

}
