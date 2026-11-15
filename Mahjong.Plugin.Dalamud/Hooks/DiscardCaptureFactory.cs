using System;
using Dalamud.Plugin.Services;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.Hooks.Strategies;
using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Hooks;

/// <summary>Creates the active discard-capture strategy (addon state polling).</summary>
public static class DiscardCaptureFactory
{
    public static IDiscardCapture Create(
        IPluginLog log,
        IFramework framework,
        StateAggregator aggregator)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(framework);
        ArgumentNullException.ThrowIfNull(aggregator);

        log.Info("[DiscardCapture] using addon-poll strategy.");
        var capture = new AddonPollDiscardCapture(log);
        aggregator.Changed += capture.OnSnapshotChanged;
        return capture;
    }
}
