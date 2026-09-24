# 实战训练素材留存与导出

这一步保存可追溯的素材并提供整理工具，不启动训练、不替换 Mortal 权重，也不把模型推荐视为最优动作。

## 日常采集

设置页 **Training data archive → Keep completed matches for future training** 默认开启，旧配置保留原有其他设置。
热重载后继续使用 `/mjauto collect on` 即可，或手动进入麻将对局。设置页可复制素材目录。
素材保存在插件配置目录下的 `training-data/match-<SHA256>/`，独立于 `match-archives` 的 30 天 / 1 GiB 清理。
原始资料只在本机，不自动上传。长期目录没有自动删除策略，包含原始包中的玩家信息，应当作为私人资料保存。

每场长期副本包含：

- 已结束比赛的 `archive/`：公开局面、己方手牌、推荐、操作、操作结果、网络事件和汇总。
- `raw-capture.ndjson`：本桌第一段原始抓包。若中途停开录制，后续段全部放入 `raw-captures/segment-NNN.ndjson`；逐段等写入队列及末尾完整性记录落盘后复制，并标记中断，不能因尾段完整就认定整场完整。
- `provenance.json`、`policy-weights.json`：内置策略权重及 SHA-256、策略和规则构建标识、模式设置；已收到模型身份时记录 Mortal 权重 SHA-256、模型架构版本和 runner SHA-256。
- `manifest.json`：内容身份、逐文件 SHA-256 / 字节数、固定分组及质量标记。

日志 schema 7 新增局面自身的 `hand_id` / `revision`、观测完整性、座位与局况上下文。当前 schema 8 在游戏操作前冻结这些编号，动作携带 `dispatch_context=before-input`，防止同步回调把操作关联到操作后的局面。旧记录增加 `action_context_unverified` 质量标记，不推测补造操作前编号。
动作记录红牌身份；决策记录当时的版本信息，不用比赛结束时的配置反推整场所有决策。
手动或其他代码触发的麻将回调保留 `automated=false`，不会伪装成本插件执行。
未知自风、局数、模型身份、外部校准文件身份保持未知；外部校准文件目前不声称已经记录实际加载版本。
Mortal runner 对传给 torch 的同一份权重字节计算 SHA-256，经带进程会话与输入序号的响应返回；不把模型路径当成版本。
该读取发生在后台子进程启动时，不在游戏线程读取权重。

归档和复制都在后台。复制失败保留 `training-pending.json`，普通归档清理不会删除这些尚未成功保存的比赛。
连续采集会停止续排并显示错误，正在进行的比赛沿用原自动操作。处理存储问题后重新开启采集；失败的历史记录可显式导入。
未完成的 `.pending-*` 目录不参与导出。插件被强制结束时只保留已成功写入的文件，不声称有完整训练样本。

## 导出

在项目根目录运行（PowerShell）：

```powershell
$corpus = Join-Path $env:APPDATA 'XIVLauncher/pluginConfigs/Mahjong.Plugin.Dalamud/training-data'
python tools/training_dataset.py --corpus $corpus --output artifacts/training-export-001
```

输出目录必须不存在。工具逐场校验文件及身份，按场处理并流式写出；任何文件损坏、越界路径、分组篡改或冲突副本都令导出失败。
`report.json` 是成功完成标记，记录输出文件哈希和各场质量。失败只留下诊断及待完成目录，不发布可用分组文件。

分组由不可变比赛内容身份计算：`int(match_id[:8],16) % 100`，0–79 为 train，80–89 为 development，90–99 为 acceptance。
这是散列分配比例，不保证小样本恰好 80/10/10。整场所有动作固定到同一个集合；相同内容的副本只导出一次。
缺少服务端比赛开局事件或明确最终成绩画面的记录放入 `unassigned.jsonl`，防止中途重载产生的片段跨集合泄漏。
划分不能因为结果好坏而重新随机。最终验收集应冻结；查看并据此调参后，该数据不能再作为未见过的最终验收证据。

每条记录分别提供 `observation`、`recommendation`、`dispatched_action`、`execution_status`、`provenance`、`labels` 和 `quality_flags`。
只按同一手局和局面版本关联操作与先前状态，推荐还须匹配动作、牌种及红牌身份。
模型推荐、客户端操作返回成功、UI 状态变化、服务端确认是不同证据层级。
导出会核对封存原始包是否覆盖已有麻将网络事件；缺失时标记 `raw_archive_packets_missing`。UTC 时间统一到 100 ns，只规范序列化末尾零，不用模糊时间配对。
本轮导出还没有对每条操作做服务端逐条确认，因此 `server_confirmed_action` 保持 null，`training_eligible` 明确为 false。
最终分数来自空手牌的状态 27；不会把中途比分或推测出来的 hand-end 充当整场最终结果。
和牌、放铳及最优动作标签目前保持 null，不根据分差或 Mortal 选择编造。

导出保留己方可见状态和必要版本信息，省略原始包、玩家名字、角色网络 ID、路径和自由文本说明。
这是以后解码验证、补标签、对照评测的中间数据集，尚不是 Mortal 的完整训练牌谱，也不能直接输入现有 `calibrate.py`。
后者继续只接受独立模拟训练种子的标签；不会把 live 数据冒充为模拟训练集。

## 导入已有归档

```powershell
$archives = Join-Path $env:APPDATA 'XIVLauncher/pluginConfigs/Mahjong.Plugin.Dalamud/match-archives'
python tools/training_dataset.py --corpus $corpus --import-archives $archives
```

只导入有 `managed-complete.json` 的归档，不修改原文件。不猜测抓包与比赛的配对；知道准确对应关系时使用
`--import-archive <某场目录> --raw-capture <对应抓包>`。旧日志缺失的版本、局面关联编号及抓包标记为未知，不补造。
导入过的同一场不会覆盖已有较完整副本；文件发生冲突时停止，保留两边供检查。

2026-09-24 已把现有 9 场归档备份到长期目录，并为已通过哈希核对的一场附上对应原始抓包。
首次真实数据导出共 860 条操作记录：train 92、development 57、acceptance 0、unassigned 711。
这些是待清洗素材，0 条被标为可直接训练；旧日志缺少精确关联信息等限制可以在导出质量标记中看到。
新字段会从更新后的下一场起记录，历史文件不改写。

## 验证

本次改动通过全套 1,039 项 C# 测试及 25 项 Python 测试。覆盖原始包封口顺序、磁盘失败保护、独立留存、配置兼容、模型指纹、精确局面关联、整场分组、重复与损坏拒绝及未知标签保留。
第一场新版本实测已完成，发现并修复抓包重启遗漏及同步回调的动作关联问题，详见 [首场验收记录](reviews/20260924-training-first-match.md)。后续修复通过 489 项插件测试及 28 项 Python 测试，并重新导出这场原始记录。修复后的 schema 8 尚未做新的实机对局验证。
