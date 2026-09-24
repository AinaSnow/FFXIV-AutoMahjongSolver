using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.Dalamud.GameState;
using Lumina.Excel.Sheets;

namespace Mahjong.Plugin.Dalamud.Actions;

/// <summary>Framework-thread adapter for the opt-in continuous collection state machine.</summary>
public sealed class ContinuousCollectionLoop : IDisposable
{
    internal const uint DutyId = 766, TerritoryId = 831, ContentId = 61005;
    private const string SupportedBuild = "2026.09.15.0000.0000";
    private readonly Plugin plugin;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly ContinuousCollectionController controller = new();
    private bool disposed, wasEnabled, engaged;
    private string? lastStatus;
    private readonly bool dutyVerified;
    private Task<string?>? storageCheck;
    private string? storageBlock = "Checking capture storage";
    private long lastStorageCheck = -60000;
    public string Status => controller.Status;
    public int CompletedMatches => controller.CompletedMatches;

    public ContinuousCollectionLoop(Plugin plugin, IFramework framework, IPluginLog log)
    {
        this.plugin = plugin; this.framework = framework; this.log = log;
        try
        {
            var row = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>(ClientLanguage.English).GetRowOrDefault(DutyId);
            dutyVerified = row is { } duty && duty.Name.ToString() == "Novice Mahjong (Quick Ranked Match)"
                && duty.TerritoryType.RowId == TerritoryId && duty.Content.RowId == ContentId && duty.ContentType.RowId == 19;
        }
        catch (Exception e) { log.Warning($"[Collection] Duty sheet verification failed: {e.Message}"); }
        framework.Update += Update;
    }

    public string Start()
    {
        var cfg = plugin.Configuration;
        if (!cfg.TosAccepted || !cfg.AutoPlayConfirmed) return "Enable Auto-play in the main window first.";
        if (!Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded) return "Log in before starting collection.";
        if (!dutyVerified || plugin.NetworkCapture.GameVersion != SupportedBuild) return "This client build's matchmaking has not been verified.";
        controller.Restart();
        plugin.DebugPackets.AcknowledgeCollectionFailure();
        plugin.TrainingCorpus.Retry();
        storageBlock = "Checking capture storage";
        lastStorageCheck = -60000;
        plugin.ConfigService.Update(c => c with
        {
            ContinuousCollection = true, CollectionCharacterId = Plugin.PlayerState.ContentId,
            AutomationArmed = true, SuggestionOnly = false, AutoAdvanceAfterHand = true,
            DebugAutoPacketLogging = true, EnableGameLogging = true,
        });
        return "Continuous player East-only collection enabled. It runs until you stop it.";
    }

    public void Stop() => plugin.ConfigService.Update(c => c with { ContinuousCollection = false });

    private void RefreshStorage()
    {
        if (storageCheck is { IsCompleted: true })
        {
            storageBlock = storageCheck.GetAwaiter().GetResult();
            storageCheck = null;
        }
        if (storageCheck is not null || Environment.TickCount64 - lastStorageCheck < 60000) return;
        lastStorageCheck = Environment.TickCount64;
        var path = plugin.DebugPackets.DirectoryPath;
        storageCheck = Task.Run(() =>
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
                return drive.AvailableFreeSpace < (1L << 30) ? "Less than 1 GiB free for capture; collection stopped" : null;
            }
            catch (Exception e) { return "Cannot check capture storage: " + e.Message; }
        });
    }

    private void Update(IFramework _)
    {
        if (disposed) return;
        try
        {
            var cfg = plugin.Configuration;
            if (!cfg.ContinuousCollection && !engaged) return;
            if (cfg.ContinuousCollection) engaged = true;
            if (cfg.ContinuousCollection && !wasEnabled) controller.Restart();
            wasEnabled = cfg.ContinuousCollection;
            if (cfg.ContinuousCollection) RefreshStorage();
            var observation = Observe(cfg);
            var action = controller.Tick(cfg.ContinuousCollection, observation);
            if (controller.Fault is not null && cfg.ContinuousCollection) Stop();
            if (action != CollectionAction.None) Execute(action);
            if (!plugin.Configuration.ContinuousCollection && !controller.NeedsUpdate) engaged = false;
            if (Status != lastStatus)
            {
                log.Information($"[Collection] {Status}; action={action}; queue={observation.QueueState}; target={observation.InTargetDuty}; completed={CompletedMatches}");
                lastStatus = Status;
            }
        }
        catch (Exception e)
        {
            controller.Fail("Collection error: " + e.Message);
            log.Error(e, "[Collection] Stopped after matchmaking error");
            engaged = wasEnabled = false;
            if (plugin.Configuration.ContinuousCollection) Stop();
        }
    }

    private unsafe CollectionObservation Observe(Configuration cfg)
    {
        bool loggedIn = Plugin.ClientState.IsLoggedIn && Plugin.PlayerState.IsLoaded;
        if (!loggedIn) return new();
        bool sameCharacter = cfg.CollectionCharacterId != 0 && cfg.CollectionCharacterId == Plugin.PlayerState.ContentId;
        if (!sameCharacter || !dutyVerified || plugin.NetworkCapture.GameVersion != SupportedBuild)
            return new() { LoggedIn = true, SameCharacter = sameCharacter, FatalReason = "Unverified client build / duty; collection stopped" };
        var finder = ContentsFinder.Instance();
        if (finder == null) return new() { LoggedIn = true, SameCharacter = true, FatalReason = "Matchmaking unavailable" };
        var queue = &finder->QueueInfo;
        bool ownQueue = IsOnlyTargetQueue(queue);
        var game = GameMain.Instance();
        uint currentDuty = game == null ? 0u : game->CurrentContentFinderConditionId;
        var snap = plugin.Aggregator.Latest;
        bool present = plugin.AddonReader.LastObservation.Present;
        bool final = present && plugin.AddonReader.ProtocolVariant == "Emj"
            && snap is { AddonStateCode: 27 } && snap.Hand.Count == 0 && UiHandBoundaryTracker.ScoresKnown(snap.Scores);
        var confirm = GetConfirmation();
        var agent = AgentContentsFinder.Instance();
        bool transitioning = Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]
            || Plugin.Condition[ConditionFlag.LoggingOut];
        bool busy = Plugin.Condition[ConditionFlag.InCombat] || Plugin.Condition[ConditionFlag.Casting]
            || Plugin.Condition[ConditionFlag.Occupied] || Plugin.Condition[ConditionFlag.OccupiedInEvent]
            || Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] || Plugin.Condition[ConditionFlag.WatchingCutscene]
            || Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] || Plugin.Condition[ConditionFlag.Crafting]
            || Plugin.Condition[ConditionFlag.Gathering] || Plugin.Condition[ConditionFlag.BetweenAreas]
            || Plugin.Condition[ConditionFlag.BetweenAreas51] || Plugin.Condition[ConditionFlag.LoggingOut]
            || Plugin.Condition[ConditionFlag.Unconscious] || Plugin.Condition[ConditionFlag.TradeOpen];
        bool bound = Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.BoundByDuty56]
            || Plugin.Condition[ConditionFlag.BoundByDuty95];
        string? block = storageBlock ?? (!plugin.DebugPackets.ReadyForCollection ? "Waiting for packet capture" :
            Plugin.PartyList.Length > 1 ? "Leave your party before collecting solo ranked matches" :
            agent == null || agent->DutyPenaltyMinutes > 0 ? "Waiting for Duty Finder availability / penalty" :
            finder->IsUnrestrictedParty || finder->IsExplorerMode ? "Turn off unrestricted party / explorer mode" :
            busy || bound ? "Waiting until character is free to queue" : null);
        return new()
        {
            LoggedIn = true, SameCharacter = cfg.CollectionCharacterId != 0 && cfg.CollectionCharacterId == Plugin.PlayerState.ContentId,
            AutomationAllowed = cfg.TosAccepted && cfg.AutoPlayConfirmed && cfg.AutomationArmed && !cfg.SuggestionOnly
                && cfg.AutoAdvanceAfterHand,
            CanQueue = game != null && block is null && currentDuty == 0 && !present,
            CanAccept = !transitioning && confirm != null && confirm->IsVisible && ButtonReady(confirm->CommenceButton),
            QueueState = (int)queue->QueueState is >= 0 and <= 5 ? (CollectionQueueState)queue->QueueState : CollectionQueueState.Unknown,
            OwnQueue = ownQueue, OwnPop = IsTarget(queue->PoppedQueueEntry),
            InTargetDuty = currentDuty == DutyId && Plugin.ClientState.TerritoryType == TerritoryId,
            InOtherDuty = currentDuty != 0 && currentDuty != DutyId,
            AtTable = present, FinalResult = final,
            CanLeave = final && currentDuty == DutyId && EventFramework.CanLeaveCurrentContent(),
            QueueBlockReason = block,
            FatalReason = !dutyVerified || plugin.NetworkCapture.GameVersion != SupportedBuild ? "Unverified client build / duty; collection stopped" :
                storageBlock is not null && storageCheck is null && storageBlock != "Checking capture storage" ? storageBlock :
                !cfg.DebugAutoPacketLogging || !cfg.EnableGameLogging ? "Recording disabled; collection stopped" : plugin.DebugPackets.CollectionError ?? (cfg.RetainTrainingData ? plugin.TrainingCorpus.LastError : null),
        };
    }

    private unsafe void Execute(CollectionAction action)
    {
        if (disposed && action != CollectionAction.CancelQueue) return;
        // Re-read configuration and all native state immediately before dispatch. No pointers survive a frame.
        var s = Observe(plugin.Configuration);
        bool running = plugin.Configuration.ContinuousCollection && s.AutomationAllowed && s.SameCharacter && s.FatalReason is null;
        var finder = ContentsFinder.Instance();
        if (finder == null || !s.LoggedIn || !s.SameCharacter) return;
        switch (action)
        {
            case CollectionAction.Queue when running && s.CanQueue && s.QueueState == CollectionQueueState.None:
                uint id = DutyId;
                finder->QueueInfo.QueueDuties(&id, 1);
                break;
            case CollectionAction.Accept when running && s.QueueState == CollectionQueueState.Ready && s.OwnQueue && s.OwnPop && s.CanAccept:
                var confirm = GetConfirmation();
                if (confirm != null && !ClickRegisteredButton(&confirm->AtkUnitBase, confirm->CommenceButton))
                    log.Warning("[Collection] Commence has no supported registered button event; waiting for manual confirmation");
                break;
            case CollectionAction.Leave when s.AutomationAllowed && s.InTargetDuty && s.AtTable && s.FinalResult && s.CanLeave:
                EventFramework.LeaveCurrentContent(false);
                break;
            case CollectionAction.CancelQueue when s.OwnQueue && (s.QueueState != CollectionQueueState.Ready || s.OwnPop) && s.QueueState is CollectionQueueState.Pending or CollectionQueueState.Queued or CollectionQueueState.Ready:
                finder->QueueInfo.CancelQueue();
                break;
        }
    }

    private static bool IsTarget(ContentsId entry) => entry.ContentType == ContentsType.Regular && entry.Id == DutyId;
    internal static unsafe bool IsOnlyTargetQueue(ContentsFinderQueueInfo* queue)
    {
        int count = 0;
        if (queue->QueuedContentRouletteId != 0) return false;
        foreach (var entry in queue->QueuedEntries)
        {
            if (entry.ContentType == ContentsType.None && entry.Id == 0) continue;
            if (!IsTarget(entry)) return false;
            count++;
        }
        return count == 1;
    }
    private static unsafe AddonContentsFinderConfirm* GetConfirmation() =>
        (AddonContentsFinderConfirm*)Plugin.GameGui.GetAddonByName("ContentsFinderConfirm").Address;
    private static unsafe bool ButtonReady(AtkComponentButton* button) => button != null && button->OwnerNode != null
        && button->IsEnabled && button->OwnerNode->AtkResNode.IsVisible();

    internal static unsafe bool ClickRegisteredButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (addon == null || !addon->IsVisible || !ButtonReady(button)) return false;
        // Use the button's actual registration, not a guessed callback number or a global Yes/No click.
        var node = &button->OwnerNode->AtkResNode;
        var evt = node->AtkEventManager.Event;
        for (int i = 0; evt != null && i < 32; i++, evt = evt->NextEvent)
        {
            if (evt->State.EventType != AtkEventType.ButtonClick || evt->Listener != (AtkEventListener*)addon) continue;
            var copy = *evt;
            AtkEventData data = default;
            evt->Listener->ReceiveEvent(AtkEventType.ButtonClick, (int)evt->Param, &copy, &data);
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        framework.Update -= Update;
        // Keep the saved switch for hot reload, but withdraw an owned queue while no plugin can accept it.
        try
        {
            if (!engaged && !plugin.Configuration.ContinuousCollection) return;
            if (!framework.IsInFrameworkUpdateThread || framework.IsFrameworkUnloading)
            {
                log.Information("[Collection] Unloaded outside a live framework update; no game calls scheduled. Withdraw any pending queue manually if not reloading.");
                return;
            }
            var s = Observe(plugin.Configuration);
            if (s.SameCharacter && s.OwnQueue && (s.QueueState != CollectionQueueState.Ready || s.OwnPop) && s.QueueState is CollectionQueueState.Pending or CollectionQueueState.Queued or CollectionQueueState.Ready)
                Execute(CollectionAction.CancelQueue);
        }
        catch (Exception e) { log.Warning($"[Collection] Queue withdrawal on unload: {e.Message}"); }
    }
}
