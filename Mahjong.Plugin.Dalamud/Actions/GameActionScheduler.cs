using Dalamud.Plugin.Services;
namespace Mahjong.Plugin.Dalamud.Actions;

public interface IGameActionScheduler
{
    void Schedule(Action callback, TimeSpan delay);
}

internal sealed class FrameworkActionScheduler(IFramework framework) : IGameActionScheduler
{
    public void Schedule(Action callback, TimeSpan delay) => _ = framework.RunOnTick(callback, delay);
}
