# 持续玩家东风战采集

开启后循环：排队 → 确认进场 → 现有自动打牌与录包 → 最终结算后离场 → 再排。
没有一天或其他时长限制。开关保存到配置；重新加载插件、重启游戏并登录同一角色后继续。
旧配置默认关闭，不更改 Mortal 模型或试用范围。

## 使用

- 热重载后 `/mjauto` 打开主窗口，打开 **Continuous collection · Player East-only** 下的开关。
- 也可 `/mjauto collect on` 开启，`/mjauto collect off` 停止，`/mjauto collect` 查看状态。
- 开启会启用 Auto-play、每手自动下一局、自动录包和游戏日志。首次使用的用户须先完成原有 Auto-play 确认。
- 停止撤回此模式的待匹配队列；已确认进场或正在打的比赛继续打完并离场，不再排下一场。
- 主窗口 **Off / Hints** 或 `/mjauto off` 停止自动打牌并关闭持续采集。不会自动中途退出正在进行的比赛。
- 主界面显示等待原因及本次插件加载以来已确认完成并离桌的场数。无需打开开发工具。

## 适配与保护

需要当前版本至少有一个内置或自动学习的协议 profile。下表数值从本机国际服 `2026.09.15.0000.0000` 英文游戏表读取，插件加载时再核对：

| 项目 | 值 |
| --- | --- |
| ContentFinderCondition | 766（英文名 Novice Mahjong (Quick Ranked Match)） |
| TerritoryType / ContentType | 831 / 19 |
| Content | 61005 |

核对只比较行号 766 与它链接的 TerritoryType、Content、ContentType 三个数字列，用客户端自身语言加载表，
不再比较英文名称，也不再强制加载英文表。名称只写入日志 `[Collection] Duty sheet (...)` 作辅助信息。
补丁后行号挪位或被复用时，三个链接值任一不符即判定未验证、拒绝开启（按原有提示“匹配流程尚未验证”）。

使用普通段位玩家东风排位；高级段位东风 767、半庄 643/644、四人组队 768/769 不会被当作目标。
任务身份读 `GameMain.CurrentContentFinderConditionId`，不能仅凭所有麻将模式共用的 831 场地判断。
队列必须只有一个目标任务且不是随机任务，进场还必须核对弹出的任务 ID。
点击当前 Commence 按钮登记的 ButtonClick 事件，绝不自动确认通用 Yes/No 对话框。

离场同时要求目标任务的 Dalamud `DutyCompleted` 完成通知、可采集的麻将窗口、空手牌、状态 27 和有效最终分数，稳定 5 秒且游戏允许离开。
“可采集”指：窗口名（`Emj`、`EmjL` 等）与本桌已解析布局（`data/layouts/*.json`）的 `addonName` 一致，
且当前游戏版本有该窗口名的协议 profile（已验证，或带当前试用范围；内置与 `learned-protocols/` 都算，与 Mortal 试用开关无关）。
不满足时不自动离场并记一次警告日志，其余行为与以前非 `Emj` 窗口相同；Mortal 准入（`DomanMortalTrialGuard`）不受影响。
当前 `2026.11.03` 只有 `Emj` profile，因此北美 `EmjL` 仍不可采集；自动学习只在该版本完全没有 profile 时运行，
所以需要先为 `EmjL` 提供 profile（见路线图 B2）。
状态 27 也会短暂出现在局间，因此不再单独将它当成整场结束；完成后由游戏或玩家提前离桌，同样只计一次，不要求插件发出 Leave。
完成通知按任务、场地和角色核对，注销与换角色清除通知。没有收到确认时不把中途离开计为完成；它不是归档文件数。
状态 29/32 的小局结算不能触发离场。离场后至少等待 10 秒再排；原有后台归档流程照常封口。
卸载立即停用本组件的自动动作。若卸载发生在游戏线程之外，不安排延迟游戏调用；不重新加载时，已有排队需手动取消。
每个动作都在游戏更新线程重新检查配置和状态；排队、进场、离场、撤队各有间隔与有限重试。
换角色、未知任务、未适配版本、关闭自动操作/录制或确认失败会停下并显示原因。
忙碌、组队或排队惩罚期间等待，不自动更改玩家的组队或副本设置。

每分钟后台检查录制磁盘，剩余少于 1 GiB 时停止新队列；录包磁盘故障或单场 64 MiB 上限也会停止。
修复原因后可重新打开开关。不会删除旧抓包；抓包仍在 `debug-packets`，比赛归档仍在 `match-archives`。
比赛归档保留策略与原配置相同，原始抓包不自动纳入归档容量清理。
默认同时启用[独立训练素材留存](training-data.md)，不受普通归档清理影响；保存失败会停止续排。

## 验证范围

自动测试覆盖完整循环、超过一天继续运行、停止时的排队状态、热重载接管、角色切换、错误任务、
晚到的排队确认、最终结算稳定性、有限重试、录制故障以及旧配置兼容。
2026-09-24 六场实机日志已确认连续排队、确认进场和归档循环。此次新增的完成通知计数和离场条件已通过状态机回归，仍需后续正常对局核对 `[Collection] Observed player East-only duty completion`；旧记录没有这一信号，无法离线证明新通知在麻将中到达。无需为此专门随机补采。

2026-11-16 去英文硬编码只经过离线单元测试（`CollectionGate` 与离场门槛）。尚无人在国服、日服、北美或其他非英文客户端实机运行过持续采集；
各语言客户端的 766/831/61005/19 是否一致、国服版本行号是否相同均未实测，首次使用请查看 `[Collection] Duty sheet` 日志。
不一致时插件会拒绝开启而不会排错任务。

接口依据为本机 Dalamud/FFXIVClientStructs 绑定与上游声明：
[ContentsFinder](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/UI/ContentsFinder.cs)、
[AtkEvent](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Component/GUI/AtkEvent.cs)、
[EventFramework](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/Event/EventFramework.cs)。

回归测试位于 `tests/Mahjong.Plugin.Dalamud.Tests/ContinuousCollectionTests.cs`；最近修复的验证记录见 [六场日志复核](reviews/20260924-six-match-log-review.md)。
