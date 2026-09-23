using Mahjong.Plugin.Game;
namespace Mahjong.Plugin.Dalamud.Composition;
internal sealed class ConfigMigratorV2ToV3 : IConfigMigrator<Configuration>
{
    public int FromVersion => 2;
    public int ToVersion => 3;
    public Configuration Migrate(Configuration input) => input with { Version = ToVersion };
}
