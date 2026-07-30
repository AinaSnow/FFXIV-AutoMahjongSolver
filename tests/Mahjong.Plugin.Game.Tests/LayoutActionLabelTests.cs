namespace Mahjong.Plugin.Game.Tests;

public class LayoutActionLabelTests
{
    [Theory]
    [InlineData("Pon")]
    [InlineData("pon")]
    [InlineData(" ポン ")]
    [InlineData("碰")]
    public void Default_pon_aliases_are_recognized(string label)
    {
        Assert.True(LayoutActionLabelMatcher.IsPon(label, LayoutActionLabels.Default));
    }

    [Theory]
    [InlineData("リーチ")]
    [InlineData("立直")]
    [InlineData("Riichi")]
    public void Default_riichi_aliases_are_recognized(string label)
    {
        Assert.True(LayoutActionLabelMatcher.IsRiichi(label, LayoutActionLabels.Default));
    }

    [Theory]
    [InlineData("ポン!")]
    [InlineData("チー!")]
    [InlineData("カン!")]
    [InlineData("ロン!!")]
    [InlineData("リーチ!!")]
    [InlineData("ツモ!!")]
    public void Japanese_dump_action_labels_are_recognized_by_jp_profile(string label)
    {
        var profile = LoadJapaneseProfile();

        Assert.True(LayoutActionLabelMatcher.IsAcceptAction(label, profile.ActionLabels!));
    }

    [Fact]
    public void Japanese_cancel_label_is_recognized_as_pass()
    {
        var profile = LoadJapaneseProfile();

        Assert.True(LayoutActionLabelMatcher.IsPass("キャンセル", profile.ActionLabels!));
    }

    [Fact]
    public void Profile_aliases_can_override_defaults()
    {
        var labels = new LayoutActionLabels { Pon = ["CustomPon"] };

        Assert.True(LayoutActionLabelMatcher.IsPon("CustomPon", labels));
        Assert.False(LayoutActionLabelMatcher.IsPon("Pon", labels));
    }

    private static LayoutProfile LoadJapaneseProfile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "data", "layouts", "jp.json");
            if (File.Exists(path))
                return JsonLayoutProfileLoader.Load(path);
            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate data/layouts/jp.json from the test output directory.");
    }
}
