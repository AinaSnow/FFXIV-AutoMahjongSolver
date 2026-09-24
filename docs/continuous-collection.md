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
- 主界面显示等待原因及本次插件加载以来自动完成的场数。无需打开开发工具。

## 适配与保护

限定国际服 `2026.09.15.0000.0000`。已从本机该版本游戏表读取并在启动时再核对：

| 项目 | 值 |
| --- | --- |
| ContentFinderCondition | 766 — Novice Mahjong (Quick Ranked Match) |
| TerritoryType / ContentType | 831 / 19 |
| Content | 61005 |

使用普通段位玩家东风排位；高级段位东风 767、半庄 643/644、四人组队 768/769 不会被当作目标。
任务身份读 `GameMain.CurrentContentFinderConditionId`，不能仅凭所有麻将模式共用的 831 场地判断。
队列必须只有一个目标任务且不是随机任务，进场还必须核对弹出的任务 ID。
点击当前 Commence 按钮登记的 ButtonClick 事件，绝不自动确认通用 Yes/No 对话框。

离场同时要求目标任务、实际 Emj 窗口、空手牌、状态 27 和有效最终分数，稳定 5 秒且游戏允许离开。
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
这些是状态机与本机接口测试；新排队和进场按钮调用仍需首次游戏循环确认，不能称为已完成实机验收。
第一次使用观察一次“排队 → 进场 → 最终结算 → 再排”；异常时状态与 `[Collection]` 日志可用于定位。

接口依据为本机 Dalamud/FFXIVClientStructs 绑定与上游声明：
[ContentsFinder](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/UI/ContentsFinder.cs)、
[AtkEvent](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Component/GUI/AtkEvent.cs)、
[EventFramework](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/Event/EventFramework.cs)。

本次回归：完整解决方案 1,028 项测试通过，其中插件 476 项、此次新增 28 项。Release 构建无警告或错误。
