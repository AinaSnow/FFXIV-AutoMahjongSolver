namespace Mahjong.Plugin.Dalamud.Actions;

/// <summary>Only synchronous callbacks inside our scheduled action can be attributed to it.</summary>
internal sealed class AutomationInputScope : IDisposable
{
    [ThreadStatic] private static AutomationInputScope? current;
    private readonly AutomationInputScope? previous;
    private bool observed, disposed;
    public static bool IsActive => current is not null;
    public static bool CallbackObserved => current?.observed == true;
    private AutomationInputScope() { previous = current; current = this; }
    public static AutomationInputScope Enter() => new();
    public static void ObserveCallback() { if (current is { } scope) scope.observed = true; }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        current = previous;
        if (observed && previous is not null) previous.observed = true;
    }
}
