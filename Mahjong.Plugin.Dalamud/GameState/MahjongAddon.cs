using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Mahjong.Plugin.Dalamud.GameState;

public sealed class MahjongAddon
{
    /// <summary>Fallback names used before layout profiles have been loaded.</summary>
    public static readonly IReadOnlyList<string> CandidateNames = new[] { "Emj", "EmjL" };

    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private IReadOnlyList<string> knownAddonNames = CandidateNames;
    private string? lastResolved;

    public MahjongAddon(IGameGui gameGui, IPluginLog log)
    {
        ArgumentNullException.ThrowIfNull(gameGui);
        ArgumentNullException.ThrowIfNull(log);
        this.gameGui = gameGui;
        this.log = log;
    }

    /// <summary>Names discovered from the loaded layout profiles, with the legacy fallback retained when none load.</summary>
    public IReadOnlyList<string> KnownAddonNames => knownAddonNames;

    public void SetKnownAddonNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var resolved = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        knownAddonNames = resolved.Length == 0 ? CandidateNames : resolved;

        if (lastResolved is not null && !knownAddonNames.Contains(lastResolved, StringComparer.Ordinal))
            lastResolved = null;
    }

    /// <summary>Callers must still check unit-&gt;IsVisible if they care about modal/in-match state.</summary>
    public unsafe bool TryGet(out AtkUnitBase* unit, out string name)
    {
        if (lastResolved is not null)
        {
            var ptr = gameGui.GetAddonByName(lastResolved);
            if (ptr.Address != nint.Zero)
            {
                unit = (AtkUnitBase*)ptr.Address;
                name = lastResolved;
                return true;
            }
        }

        foreach (var candidate in knownAddonNames)
        {
            var ptr = gameGui.GetAddonByName(candidate);
            if (ptr.Address == nint.Zero)
                continue;

            if (lastResolved != candidate)
            {
                log.Info(
                    $"[MjAuto] Mahjong addon resolved as \"{candidate}\" " +
                    $"(candidates: {string.Join(", ", knownAddonNames)})");
                lastResolved = candidate;
            }
            unit = (AtkUnitBase*)ptr.Address;
            name = candidate;
            return true;
        }

        unit = null;
        name = string.Empty;
        return false;
    }

    /// <summary>Resolves an explicitly supplied addon name, including names not present in a layout profile yet.</summary>
    public unsafe bool TryGetByName(string addonName, out AtkUnitBase* unit)
    {
        if (string.IsNullOrWhiteSpace(addonName))
        {
            unit = null;
            return false;
        }

        var ptr = gameGui.GetAddonByName(addonName.Trim());
        unit = (AtkUnitBase*)ptr.Address;
        return unit != null;
    }

    public static bool IsMahjongAddon(string addonName)
    {
        foreach (var c in CandidateNames)
            if (addonName == c)
                return true;
        return false;
    }

    public bool IsKnownAddon(string addonName) =>
        knownAddonNames.Contains(addonName, StringComparer.Ordinal);
}
