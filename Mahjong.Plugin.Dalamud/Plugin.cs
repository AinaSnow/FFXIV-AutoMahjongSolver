using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Plugin.Dalamud.Adapters;
using Mahjong.Plugin.Dalamud.Commands;
using Mahjong.Plugin.Dalamud.Composition;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.Hooks;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Dalamud.Mortal;
using Mahjong.Plugin.Dalamud.Telemetry;
using Mahjong.Plugin.Dalamud.UI;
using Mahjong.Plugin.Game;
using Mahjong.Policy;
using Mahjong.Policy.Efficiency;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Plugin.Dalamud;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    public readonly WindowSystem WindowSystem = new("Mahjong.Plugin.Dalamud");

    public ServiceProvider Services { get; }

    /// <summary>Use Update() to mutate — never reach into the underlying record directly.</summary>
    public IConfigService<Configuration> ConfigService { get; }

    /// <summary>Reference is replaced on every edit — read fresh each frame, don't cache.</summary>
    public Configuration Configuration => ConfigService.Current;

    public MainWindow MainWindow { get; }
    public AboutWindow AboutWindow { get; }
    public SettingsWindow SettingsWindow { get; }
    public DebugOverlay DebugOverlay { get; }
    public HandOverlay HandOverlay { get; }
    public AddonEmjReader AddonReader { get; }
    public MeldTracker MeldTracker { get; } = new();
    public StateAggregator Aggregator { get; }
    public IPolicy Policy { get; }
    public InputEventLogger EventLogger { get; }
    public InputDispatcher Dispatcher { get; }
    public GameLogger GameLogger { get; }
    public MatchArchiveWriter MatchArchive { get; }
    public DebugPacketLogger DebugPackets { get; }
    private readonly BackgroundIoWorker archiveIo = new();
    public AutoPlayLoop AutoPlay { get; }
    public MahjongNetworkCapture NetworkCapture { get; }
    public LiveMortalBridge MortalBridge { get; }
    public PublicStateTracker PublicState { get; }

    public IDiscardCapture DiscardCapture { get; }

    public DiscardCaptureLogger DiscardCaptureLogger { get; }

    public ErrorSink ErrorSink { get; }
    public IFindingsLog FindingsLog { get; }
    public ISigprobeLog SigprobeLog { get; }
    public SeatPoolRegistry SeatPoolRegistry { get; } = new();
    public MemoryDumpRecorder MemoryDumpRecorder { get; }
    public DiscardTracker DiscardTracker { get; }
    public StrategyDiagnostics StrategyDiagnostics { get; }
    public InputRecorder InputRecorder { get; }

    private readonly MirroredPluginLog mirroredLog = null!;

    public MjAutoCommand MjAutoCommand => command;

    private readonly MjAutoCommand command;

    public Plugin()
    {
        var loaded = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration { Version = 0 };
        var migrators = new IConfigMigrator<Configuration>[]
        {
            new ConfigMigratorV0ToV1(),
            new ConfigMigratorV1ToV2(),
            new ConfigMigratorV2ToV3(),
        };
        var migrated = ConfigMigrationRunner.Run(
            loaded,
            currentVersion: loaded.Version,
            targetVersion: Mahjong.Plugin.Dalamud.Configuration.CurrentSchemaVersion,
            migrators);

        // Dev tools never persist across launches — opt-in per session.
        migrated = migrated with { DevMode = false };

        // Persist migrated config so the next launch starts at the current schema version.
        if (!ReferenceEquals(loaded, migrated))
            PluginInterface.SavePluginConfig(migrated);

        // MirroredPluginLog forwards to Dalamud and additionally writes Warning+ to the ErrorSink (attached below).
        mirroredLog = new MirroredPluginLog(Log);
        var dalamud = new DalamudServices(
            Log: mirroredLog,
            Framework: Framework,
            PluginInterface: PluginInterface,
            CommandManager: CommandManager,
            ChatGui: ChatGui,
            ClientState: ClientState,
            DataManager: DataManager,
            Condition: Condition,
            GameGui: GameGui,
            AddonLifecycle: AddonLifecycle,
            SigScanner: SigScanner,
            GameInterop: GameInterop);

        Services = PluginServices.Build(dalamud, migrated);
        ConfigService = Services.GetRequiredService<IConfigService<Configuration>>();

        Policy = Services.GetRequiredService<IPolicy>();

        // Sinks must exist before any reader construction — readers emit findings on probe.
        var configDir = PluginInterface.GetPluginConfigDirectory();
        ErrorSink = new ErrorSink(configDir);
        FindingsLog = new FindingsLog(configDir, ErrorSink);
        SigprobeLog = new SigprobeLog(configDir);
        mirroredLog.AttachSink(ErrorSink);

        // Surface silent meld-tracker drops: the inference loop gives up after MaxDeferralTicks
        // and rebaselines, which is the failure mode behind the post-call out-of-sync stuck
        // state. Without this, the only signal is the downstream "hand state out of sync" pause.
        MeldTracker.DeferralTimedOut += OnMeldTrackerDeferralTimedOut;

        var mahjongAddon = Services.GetRequiredService<MahjongAddon>();

        // IDalamudPluginInterface.AssemblyLocation is the only reliable sibling-file lookup — Assembly.Location is empty inside Dalamud's ALC.
        var pluginAssemblyDir = PluginInterface.AssemblyLocation.DirectoryName ?? configDir;
        var layoutsDir = Path.Combine(pluginAssemblyDir, "layouts");

        AddonReader = new AddonEmjReader(
            AddonLifecycle, Log, mahjongAddon, MeldTracker, configDir, layoutsDir,
            FindingsLog, ClientState.ClientLanguage.ToString());
        // Accessor closes over AddonReader so state codes and the hand-array offset follow the active variant. Constructed after AddonReader so the closure resolves to the live profile by the time DispatchDiscard runs.
        Dispatcher = new InputDispatcher(mahjongAddon, () => AddonReader.ActiveLayout);
        MatchArchive = new MatchArchiveWriter(configDir, Log, archiveIo, () => (ConfigService.Current.ArchiveRetentionDays, ConfigService.Current.ArchiveMaxBytes));
        NetworkCapture = new MahjongNetworkCapture(GameInterop, Log,
            () => AddonReader.ActiveLayout?.Name, Path.Combine(pluginAssemblyDir, "protocols"));
        DebugPackets = new DebugPacketLogger(GameInterop, Framework, configDir,
            () => Configuration.DebugAutoPacketLogging, () => AddonReader.LastObservation.Present,
            () => new MatchArchiveEnvironment(NetworkCapture.GameVersion, AddonReader.ActiveLayout?.Name,
                NetworkCapture.ProtocolVerified, NetworkCapture.ProtocolStatus, typeof(Plugin).Module.ModuleVersionId.ToString()), Log);
        PublicState = new PublicStateTracker(NetworkCapture, Framework, AddonReader, Log, MatchArchive.RecordPacket);
        Aggregator = new StateAggregator(AddonReader, Framework, Policy, PublicState.Merge, () => Configuration);
        EventLogger = new InputEventLogger(
            AddonReader, AddonLifecycle, GameInterop, Log, mahjongAddon, configDir);
        AddonReader.EventLogger = EventLogger;
        InputRecorder = new InputRecorder(EventLogger, configDir);
        GameLogger = new GameLogger(
            Aggregator, ConfigService, Log, configDir,
            policyAccessor: () => Policy,
            eventLogger: EventLogger,
            meldTrackerAccessor: () => MeldTracker.SerializeState(), io: archiveIo, externalEnabled: () => MortalBridge?.Enabled == true);
        MortalBridge = new LiveMortalBridge(
            NetworkCapture,
            Framework,
            ConfigService,
            () => Aggregator.Latest,
            Log, runnerDirectory: pluginAssemblyDir);
        MortalBridge.DecisionPublished += Aggregator.PublishExternal;
        AddonLifecycle.RegisterListener(
            AddonEvent.PreFinalize,
            mahjongAddon.KnownAddonNames,
            OnMahjongAddonPreFinalize);
        AutoPlay = new AutoPlayLoop(this, Framework, Log, mahjongAddon);

        DiscardCapture = DiscardCaptureFactory.Create(
            Log, Framework, SigScanner, Aggregator, SeatPoolRegistry, SigprobeLog);
        DiscardCaptureLogger = new DiscardCaptureLogger(
            DiscardCapture, PluginInterface.GetPluginConfigDirectory());
        DiscardTracker = new DiscardTracker(DiscardCapture, configDir);
        StrategyDiagnostics = new StrategyDiagnostics(
            Aggregator,
            DiscardCapture,
            FindingsLog,
            usesExternalRecommendations: () => Configuration.MortalEnabled);

        MemoryDumpRecorder = new MemoryDumpRecorder(
            AddonReader, SeatPoolRegistry, ErrorSink, configDir);

        Aggregator.Changed += _ => MemoryDumpRecorder.Record("state-change");

        // Bracket every FireCallback with (pre, post) memdumps. Both labels bypass the atk_count gate that gates "state-change".
        EventLogger.BeforeFireCallback += _ => MemoryDumpRecorder.Record("input-pre");
        EventLogger.CallbackObserved += _ => MemoryDumpRecorder.Record("input-post");

        MainWindow = new MainWindow(this);
        AboutWindow = new AboutWindow(Log, PluginInterface, TextureProvider);
        SettingsWindow = new SettingsWindow(this);
        DebugOverlay = new DebugOverlay(this, Framework, CommandManager, mahjongAddon);
        HandOverlay = new HandOverlay(this, PluginInterface, mahjongAddon);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(AboutWindow);
        WindowSystem.AddWindow(SettingsWindow);
        WindowSystem.AddWindow(DebugOverlay);

        command = new MjAutoCommand(
            this, ChatGui, CommandManager, Framework, PluginInterface, SigScanner, ClientState, mahjongAddon);

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleSettingsWindow;

        var asmVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "(unknown)";
        Log.Information($"Doman Mahjong Solver v{asmVersion} loaded.");
    }

    private void OnMeldTrackerDeferralTimedOut(DeferralTimeoutEvent evt)
    {
        string removed = string.Join(",", evt.Removed.Select(t => t.ToString()));
        Log.Warning(
            $"[MeldTracker] deferral exhausted: dropping delta={evt.Delta} after {evt.DeferredTicks} ticks. " +
            $"Removed=[{removed}]. Subsequent meld inferences may be off until the next hand.");
        FindingsLog?.Record("meld_tracker_silent_drop", new Dictionary<string, object?>
        {
            ["delta"] = evt.Delta,
            ["deferred_ticks"] = evt.DeferredTicks,
            ["removed_tiles"] = removed,
        });
    }

    private void OnMahjongAddonPreFinalize(AddonEvent type, AddonArgs args)
    {
        ArchiveCurrentMatch();
        PublicState.Reset();
        MortalBridge.ResetSessionStatistics();
        GameLogger.ResetSession();
        StrategyDiagnostics.ResetSession();
        Log.Information("[Mortal] Reset session statistics and game-log state after leaving the mahjong table.");
    }

    private void ArchiveCurrentMatch()
    {
        AutoPlay?.CancelForTableExit();
        GameLogger.CompleteSession();
        _ = MatchArchive.FinalizeSessionAsync(
            GameLogger.SnapshotSessionPaths(),
            new MatchArchiveMortalStats(
                Status: MortalBridge.Status,
                PacketsProcessed: MortalBridge.PacketsProcessed,
                EventsSent: MortalBridge.EventsSent,
                ReactionsReceived: MortalBridge.ReactionsReceived,
                DecisionsMapped: MortalBridge.DecisionsMapped,
                DecisionTimeouts: MortalBridge.DecisionTimeouts,
                CandidateCorrections: MortalBridge.CandidateCorrections,
                RecoveredDiscards: MortalBridge.RecoveredDiscardEvents,
                LastModelEvalMilliseconds: MortalBridge.LastModelEvalMilliseconds),
            new MatchArchiveEnvironment(NetworkCapture.GameVersion, AddonReader.ActiveLayout?.Name,
                NetworkCapture.ProtocolVerified, NetworkCapture.ProtocolStatus, typeof(Plugin).Module.ModuleVersionId.ToString()));
    }

    public void Dispose()
    {
        MeldTracker.DeferralTimedOut -= OnMeldTrackerDeferralTimedOut;
        AddonLifecycle.UnregisterListener(OnMahjongAddonPreFinalize);
        ArchiveCurrentMatch();
        command.Dispose();
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleSettingsWindow;
        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        AboutWindow.Dispose();
        SettingsWindow.Dispose();
        DebugOverlay.Dispose();
        HandOverlay.Dispose();
        AutoPlay.Dispose();
        MortalBridge.DecisionPublished -= Aggregator.PublishExternal;
        MortalBridge.Dispose();
        PublicState.Dispose();
        DebugPackets.Dispose();
        NetworkCapture.Dispose();
        DiscardCaptureLogger.Dispose();
        DiscardTracker.Dispose();
        StrategyDiagnostics.Dispose();
        DiscardCapture.Dispose();
        GameLogger.Dispose();
        MatchArchive.Dispose();
        archiveIo.Dispose();
        InputRecorder.Dispose();
        EventLogger.Dispose();
        Aggregator.Dispose();
        AddonReader.Dispose();

        MemoryDumpRecorder.Dispose();
        (FindingsLog as IDisposable)?.Dispose();
        ErrorSink.Dispose();
        (SigprobeLog as IDisposable)?.Dispose();

        // Services last — container singletons may still be touched by components disposed above.
        Services.Dispose();
    }

    public void ToggleMainWindow() => MainWindow.Toggle();

    public void ToggleAboutWindow() => AboutWindow.Toggle();

    public void ToggleSettingsWindow() => SettingsWindow.Toggle();

    public void ToggleDebugOverlay() => DebugOverlay.Toggle();
}
