using Mahjong.Plugin.Dalamud.UI;

namespace Mahjong.Plugin.Dalamud.Tests;

[Collection("Loc")]
public sealed class LocTests : IDisposable
{
    public void Dispose() => Loc.SetLanguage("en");

    [Fact]
    public void Follows_dalamud_language_and_keeps_english_otherwise()
    {
        Loc.SetLanguage("en");
        Assert.Equal("Settings", Loc.T("Settings", "设置"));
        Assert.Equal("Final standings; waiting to leave", Loc.Status("Final standings; waiting to leave"));
        Loc.SetLanguage("zh");
        Assert.Equal("设置", Loc.T("Settings", "设置"));
    }

    [Theory]
    [InlineData("Final standings; waiting to leave", "最终排名，等待离场")]
    [InlineData("Running (model ready)", "运行中（模型已就绪）")]
    [InlineData("Learning protocol for 2026.11.03.0000.0000: 42 candidate packets this match",
        "正在学习协议，版本 2026.11.03.0000.0000: 42 个本场候选数据包")]
    [InlineData("Not learned yet (Only 1 clean hands; need 2); play another match",
        "暂未学会（只有 1 手完整解出，至少需要 2），请再打一场")]
    [InlineData("Deucalion unavailable: IOException: pipe. Toggle capture to retry.",
        "Deucalion 不可用: IOException: pipe。切换一次录包开关即可重试。")]
    public void Status_translates_known_phrases_and_keeps_dynamic_parts(string english, string chinese)
    {
        Loc.SetLanguage("zh");
        Assert.Equal(chinese, Loc.Status(english));
    }

    [Fact]
    public void Unknown_status_text_passes_through()
    {
        Loc.SetLanguage("zh");
        Assert.Equal("0x02BB=638", Loc.Status("0x02BB=638"));
        Assert.Equal("", Loc.Status(null));
    }
}
