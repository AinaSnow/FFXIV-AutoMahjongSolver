using System;
using Dalamud.Plugin.Services;
using Mahjong.Policy.Efficiency;

namespace Mahjong.Plugin.Dalamud.GameState;

public sealed class StateAggregator : IDisposable
{
    private readonly AddonEmjReader reader;
    private readonly IFramework framework;
    private readonly IPolicy? policy;
    private readonly Func<StateSnapshot, StateSnapshot>? merge;
    private bool disposed;
    private CancellationTokenSource? searchCancellation;
    private Task<PolicyEvaluation>? search;
    private long searchRevision;
    private readonly Func<Configuration>? configuration;
    public PolicyEvaluation? ShadowEvaluation { get; private set; }
    private long revision;
    private long uiHandId = 1;
    private readonly UiHandBoundaryTracker uiBoundary = new();
    private readonly DomanTurnTracker domanTurn = new();
    public PolicyEvaluation? LastLocalEvaluation { get; private set; }
    private PolicyEvaluation? searchBaseline;
    public event Action<PolicyEvaluation, PolicyEvaluation>? ShadowCompared;
    public PolicyEvaluation? LastEvaluation { get; private set; }
    private long lastRebuildTicks;
    private int lastContentHash;
    private bool hasContentHash;
    private Configuration? analyzedConfiguration;
    private const long MinTickIntervalTicks = 160_000;

    public StateSnapshot? Latest { get; private set; }

    /// <summary>Scored discards for <see cref="Latest"/>; null off our turn or on scorer throw.</summary>
    public ScoredDiscard[]? LastScored { get; private set; }

    /// <summary>Policy verdict for <see cref="Latest"/>; null when Legal=None or on policy throw.</summary>
    public ActionChoice? LastChoice { get; private set; }

    /// <summary>Scorer exception message, paired with <see cref="LastScored"/>=null.</summary>
    public string? LastScorerError { get; private set; }

    public event Action<StateSnapshot>? Changed;
    public event Action<PolicyEvaluation>? DecisionPublished;

    public StateAggregator(AddonEmjReader reader, IFramework framework, IPolicy? policy = null, Func<StateSnapshot, StateSnapshot>? merge = null, Func<Configuration>? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(framework);
        this.reader = reader;
        this.framework = framework;
        this.policy = policy;
        this.merge = merge;
        this.configuration = configuration;

        this.reader.ObservationChanged += OnObservationChanged;
        framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        searchCancellation?.Cancel();

        framework.Update -= OnFrameworkUpdate;
        reader.ObservationChanged -= OnObservationChanged;
    }

    private void OnObservationChanged(AddonEmjObservation _) => Rebuild();

    private void OnFrameworkUpdate(IFramework _)
    {
        long now = DateTime.UtcNow.Ticks;
        if (now - lastRebuildTicks < MinTickIntervalTicks)
            return;
        lastRebuildTicks = now;
        Rebuild();
        if (search is { IsFaulted: true } failed)
        {
            LastScorerError = $"enhanced-policy-error:{failed.Exception?.GetBaseException().Message}";
            search = null;
        }
        if (search is { IsCompletedSuccessfully: true } done && Latest is { } snapshot && snapshot.Revision == searchRevision)
        {
            search = null;
            ShadowEvaluation = done.Result;
            if (searchBaseline is {} baseline) ShadowCompared?.Invoke(baseline,done.Result);
            if (configuration?.Invoke() is { EnhancedStrategy: true, EnhancedShadowOnly: false })
            {
                LastLocalEvaluation = done.Result;
                LastEvaluation = done.Result; LastChoice = done.Result.Choice; LastScored = done.Result.Candidates.ToArray();
                Changed?.Invoke(snapshot);
                DecisionPublished?.Invoke(LastEvaluation);
            }
        }
    }

    private void Rebuild()
    {
        // Always call TryBuildSnapshot: it observes MeldTracker and pins ActiveLayout.
        var next = reader.TryBuildSnapshot();
        if (next is null)
        {
            if (!reader.LastObservation.Present && uiBoundary.HandsObserved > 0)
            {
                uiBoundary.Reset();
                domanTurn.Reset();
                uiHandId++;
            }
            // Addon gone (player left the table) — drop cached state so the UI reverts to the "waiting" empty state.
            if (Latest is not null)
            {
                searchCancellation?.Cancel();
                LastLocalEvaluation = null;
                LastEvaluation = null;
                ShadowEvaluation = null;
                Latest = null;
                LastScored = null;
                LastChoice = null;
                LastScorerError = null;
                hasContentHash = false;
            }
            return;
        }
        if (next.SchemaVersion != StateSnapshot.CurrentSchemaVersion)
            return;

        if (uiBoundary.Observe(next) && uiBoundary.HandsObserved > 1 && next.HandId == 0)
            uiHandId++;
        next = domanTurn.Observe(MergeState(next));
        int hash = ComputeContentHash(next);
        var currentConfiguration = configuration?.Invoke();
        if (hasContentHash && hash == lastContentHash && Equals(currentConfiguration, analyzedConfiguration))
            return;
        analyzedConfiguration = currentConfiguration;

        lastContentHash = hash;
        hasContentHash = true;
        next = next with { Revision = ++revision };
        Latest = next;
        RefreshPolicyCache(next);
        Changed?.Invoke(next);
        if (LastEvaluation is not null) DecisionPublished?.Invoke(LastEvaluation);
    }

    private void RefreshPolicyCache(StateSnapshot snap)
    {
        searchCancellation?.Cancel();
        ShadowEvaluation = null;
        LastLocalEvaluation = null;
        LastEvaluation = null;
        LastScored = null;
        LastChoice = null;
        LastScorerError = null;

        if (policy is null)
            return;
        if (snap.Legal.Flags == ActionFlags.None)
            return;

        try
        {
            LastEvaluation = policy is IAnalyzablePolicy analyzable
                ? analyzable.Analyze(snap)
                : new PolicyEvaluation(policy.Choose(snap), []);
            LastScored = LastEvaluation.Candidates.ToArray();
            LastLocalEvaluation = LastEvaluation;
            LastChoice = LastEvaluation.Choice;
            if (configuration?.Invoke() is { EnhancedStrategy: true, MortalEnabled: false } cfg)
            {
                searchCancellation = new CancellationTokenSource();
                var token = searchCancellation.Token;
                var baseline = LastEvaluation;
                searchBaseline = baseline;
                searchRevision = snap.Revision;
                search = Task.Run(() => EnhancedSearch.Analyze(snap, baseline, TimeSpan.FromMilliseconds(Math.Clamp(cfg.SearchBudgetMs, 1, 500)), token, new Mahjong.Rules.Rulesets.DomanRuleSet(),
                    string.IsNullOrWhiteSpace(cfg.CalibrationPath) ? null : RankCalibration.Load(cfg.CalibrationPath)));
            }
        }
        catch (Exception ex)
        {
            LastScorerError = $"policy-error:{ex.GetType().Name}: {ex.Message}";
            LastEvaluation = null;
        }
    }

    public void PublishExternal(StateSnapshot snapshot, PolicyEvaluation evaluation)
    {
        if (Latest is not {} latest || ComputeContentHash(Enrich(snapshot)) != ComputeContentHash(latest)) return;
        LastEvaluation = evaluation with { HandId = latest.HandId, Revision = latest.Revision };
        LastChoice = LastEvaluation.Choice; LastScored = LastEvaluation.Candidates.ToArray(); LastScorerError = null;
        DecisionPublished?.Invoke(LastEvaluation);
    }

    public void PublishLocalExecution(StateSnapshot snapshot, ActionChoice choice, string? source = null)
    {
        var evaluation = (LastLocalEvaluation ?? new PolicyEvaluation(choice, [])) with { Choice = choice };
        if (source is not null) evaluation = evaluation with { Source = source };
        if (LastEvaluation?.Choice == choice && LastEvaluation.Source == evaluation.Source) return;
        PublishExternal(snapshot, evaluation);
    }

    public PolicyEvaluation? PresentedEvaluation(bool externalEnabled, bool externalRecommendationCurrent) =>
        SelectPresentedEvaluation(LastEvaluation, externalEnabled, externalRecommendationCurrent);

    internal static PolicyEvaluation? SelectPresentedEvaluation(PolicyEvaluation? current, bool externalEnabled, bool externalRecommendationCurrent)
    {
        if (!externalEnabled) return current;
        return current?.Source switch
        {
            "mortal" when externalRecommendationCurrent => current,
            "local-fallback" or "terminal-guard" => current,
            _ => null,
        };
    }

    public StateSnapshot Enrich(StateSnapshot snapshot) => domanTurn.Apply(MergeState(snapshot));

    private StateSnapshot MergeState(StateSnapshot snapshot)
    {
        var merged = merge?.Invoke(snapshot) ?? snapshot;
        return merged.HandId == 0 ? merged with { HandId = uiHandId } : merged;
    }

    public void CancelUnconfirmedRiichiDeclaration() => domanTurn.CancelUnconfirmedDeclaration();

    public void RiichiDeclarationDispatched(StateSnapshot snapshot) => domanTurn.DeclarationDispatched(Enrich(snapshot));

    public ActionChoice Choose(StateSnapshot snapshot)
    {
        snapshot = Enrich(snapshot);
        if (Latest is { } latest && LastLocalEvaluation?.Choice is { } choice
            && ComputeContentHash(latest) == ComputeContentHash(snapshot))
            return choice;
        return ActionChoice.Pass("state changed; waiting for shared analysis");
    }

    /// <summary>Content fingerprint; record equality reference-checks list fields and reports false on every fresh snapshot.</summary>
    internal static int ComputeContentHash(StateSnapshot snap)
    {
        var h = new HashCode();
        h.Add(snap.HandId);
        h.Add(snap.SeatWind);
        h.Add(snap.Kyoku);
        h.Add(snap.ScheduledRounds);
        h.Add(snap.InitialDealerSeat);
        h.Add(snap.PublicStateConsistent);
        h.Add(snap.SeatInfoKnown);
        h.Add(snap.OurDoubleRiichi);
        foreach (var t in snap.UraDoraIndicators) h.Add(t.Id);
        h.Add(snap.WallRemaining);
        h.Add(snap.TurnIndex);
        h.Add((int)snap.Legal.Flags);
        h.Add(snap.Legal.DiscardRestrictionKnown);
        foreach (var tile in snap.Legal.DiscardableTiles)
            h.Add(tile.Id);
        AddCandidates(ref h, snap.Legal.PonCandidates);
        AddCandidates(ref h, snap.Legal.ChiCandidates);
        AddCandidates(ref h, snap.Legal.KanCandidates);
        h.Add(snap.OurRiichi);
        h.Add(snap.OurIppatsu);
        h.Add(snap.OurSeat);
        h.Add(snap.RoundWind);
        h.Add(snap.DealerSeat);
        h.Add(snap.Honba);
        h.Add(snap.RiichiSticks);
        h.Add(snap.AkaDora);
        h.Add(snap.AddonStateCode);
        h.Add((int)snap.Observations);
        for (int i = 0; i < snap.Hand.Count; i++)
        {
            h.Add(snap.Hand[i].Id);
            h.Add(i < snap.HandIsRed.Count && snap.HandIsRed[i]);
        }
        foreach (var m in snap.OurMelds)
        {
            h.Add((int)m.Kind);
            foreach (var t in m.Tiles)
                h.Add(t.Id);
        }
        foreach (var t in snap.DoraIndicators)
            h.Add(t.Id);
        foreach (var s in snap.Scores)
            h.Add(s);
        foreach (var s in snap.Seats)
        {
            h.Add(s.DiscardCount);
            for (int i = 0; i < s.Discards.Count; i++)
            {
                h.Add(s.Discards[i].Id);
                h.Add(i < s.DiscardIsRed.Count && s.DiscardIsRed[i]);
                h.Add(i < s.DiscardIsTedashi.Count && s.DiscardIsTedashi[i]);
                h.Add(i < s.DiscardWasCalled.Count && s.DiscardWasCalled[i]);
            }
            foreach (var m in s.Melds)
            {
                h.Add((int)m.Kind);
                foreach (var t in m.Tiles)
                    h.Add(t.Id);
            }
            h.Add(s.Riichi);
            h.Add(s.RiichiDiscardIndex);
            h.Add(s.Ippatsu);
        }
        return h.ToHashCode();
    }

    private static void AddCandidates(ref HashCode hash, IReadOnlyList<MeldCandidate> candidates)
    {
        hash.Add(candidates.Count);
        foreach (var candidate in candidates)
        {
            hash.Add((int)candidate.Kind);
            hash.Add(candidate.ClaimedTile.Id);
            hash.Add(candidate.FromSeat);
            hash.Add(candidate.HandTiles.Length);
            foreach (var tile in candidate.HandTiles)
                hash.Add(tile.Id);
        }
    }
}
