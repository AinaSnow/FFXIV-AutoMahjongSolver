# 2026-10-03：旧归档审计、v7 回归与半庄离线验证

## 本轮改动

基于 `5795537` 实现 P0 首批工作，修订 [后续计划](../plan-20261003.md)，不修改实战权重、协议准入范围或默认超时。

- 新增只读批量审计入口，复用生产解码器，旧 schema/缺失文件/异常均显式报告；按构建区分统计，点数与名次损失保持未知。
- 为 v7 增加 8 个回归用例：四种座位分别经历东四、南一、南二及连庄；非法场风/局号不污染 decoder 生命周期；South 解码成功仍被 East-only live guard 拒绝。
- runner 增加 stderr 启动阶段诊断；修正进程接口中“回调在 framework 线程”的错误注释，保留现有同步。
- 控制台评测摘要补四位率差和区间；Markdown 原本已包含这些指标。
- SDK 从本机已不存在的 10.0.400 统一锁到已安装且本轮验证的 10.0.401，更新 CI、评测和发布流程，增加 CI 手动入口。

## 旧数据审计

输入仍止于 9 月 25 日，无需新增实机对局。产物目录：`artifacts/live-audit-20261003-final/`。

| 项目 | 结果 |
|---|---|
| 归档目录 | 76 |
| 可核对摘要 | 61（56 份 schema 4、5 份 schema 5） |
| 不支持的历史摘要 | 14（13 份 schema 1、1 份 schema 3） |
| 缺摘要目录 | 1（8 月 2 日旧目录） |
| 原始抓包 | 64 |
| 成功导出 | 59，包含 328 手 |
| 可送离线模型的完整手局 | 326 |
| 缺结果边界的手局 | 2，保留不足状态 |
| 拒绝导出的抓包 | 5：空语料 1、rejected 非零/缺失 2、版本/变体不匹配 1、无开局 1 |

报告退出码为 **1 / incomplete**，这是历史证据不全的准确结果，不能为得到绿色结果而删除失败输入。

可审计的 61 份摘要合计 6573 次决策，包含大量早期内置策略；不能用总体 Mortal 比例衡量当前覆盖率。最新构建 `108af900-f27d-4ac8-a857-f83291c4f230` 的五份归档单列：

- Mortal 402/440 = 91.36%；本地兜底 4/440 = 0.91%；UI 恢复 27，和牌保护 7。
- 决策超时 4、失败动作 0、缺执行结果 0、损坏行/IO 失败 0，均为摘要报告值。
- 不把 UI 恢复/和牌保护当作丢失决策，也不把多项重叠异常相加。
- 这些是历史数据，本轮没有修复后新实机样本，不能声称已验证热重载后的游戏执行。

## 真实半庄回放

输入为 9 月 25 日 21:39–22:15（UTC+8）的完整半庄。原始文件 SHA-256：

`f65ea0fc49cb9b54c41fffbb77ad5e0b1ee4fa7e3c9fede24e29617a9669dfcc`

v7 生产解码器恢复顺序：东一、东一一本场、东二二本场、东三、东四、南一、南二、南二一本场、南二二本场。5 手东场、4 手南场，9 手都有真实结果边界。

真实 Mortal 回放：**9 成功、0 失败、0 跳过**，确认 976 条输入（含逐手生命周期），153 条响应。下一条输入始终沿历史实战动作，未以模型建议改写历史。

模型输出在 `artifacts/half-replay-20261003/`；使用批量初次导出的 `artifacts/live-audit-20261003/replay-0060.json`。最终审计同名导出的来源哈希相同。

| 依赖 | SHA-256 |
|---|---|
| 权重（现用 v4） | `0a88ddad649804d085491b5397d895f596b0e55f30632c549ea145bb44786563` |
| libriichi | `f3f336e16c90de3e5acd989ef1cdc7cb06aa30c6bf2af920aab72cd86fe0bfac` |
| runner | `4c7b8476d70bb974a7d01f8f6f9ca4478f145e284ff28c06e20a4ba753c65440` |

这是场风与历史输入兼容性验证。未映射包仍有清单；并未认证完整协议、计分规则、实时恢复或胜率。**半庄实战门禁继续关闭**，还需要模式/长度、终局与恢复验证。

## 首摸超时定位

逐手隔离进程测得：

| 阶段 | 首个进程 | 后续八个进程范围 |
|---|---:|---:|
| 依赖导入 | 3751 ms | 1459–1570 ms |
| 权重读入/哈希/加载 | 776 ms | 718–786 ms |
| 引擎设备准备 | 396 ms | 327–377 ms |
| Python 初始化总计 | 5328 ms | 2882–3047 ms |

首个进程准备时间已经超过当前 4 秒舍牌等待默认值，支持“启动准备是首摸超时来源之一”。该次并非游戏运行中，也未重现历史 first_ack 11.8–13 秒的全部延迟；不能宣称旧故障完全修好。WSL/解释器启动、系统负载和首次推理应继续单独测量。

下一步以这些证据设计入桌前预热：预热只准备进程，不发送未确认手局；ready 前不放行动作；配置变更/离桌/会话代次/队列满/旧回调都有取消测试。默认时限不因冷启动而直接放宽。当前仅落地诊断，没有提前启动进程或绕过门禁。

## 检查与复现

- 完整解决方案：**1251 个 .NET 用例通过，0 失败，0 跳过**。含 Game 241、Dalamud 618；结果按实际执行统计。
- Python：**60 个测试通过**，包括摘要兼容、空输入、缺文件、计数矛盾、超时继续及源文件在审计期间变化。
- Node：包解析、抓包审计、debug capture 三个脚本通过。
- 最终 Release 构建：0 警告、0 错误；原开发输出 `Mahjong.Plugin.Dalamud/bin/Release/` 已更新。
- 全量 .NET 检查后仅增加 runner 诊断/线程注释，随后通过 Python 全套、真实模型回放和最终插件构建。
- 远端可查到的成功 CI 仍仅覆盖 `1585632`；本轮当前提交尚未完成远端 job，不能与本地检查混称。

```powershell
dotnet test Mahjong.Plugin.Dalamud.sln -c Release -p:RestoreLockedMode=true --nologo --verbosity minimal
python -X utf8 -m unittest discover -s tools/tests -v
node tools/test-parse-mahjong-packets.mjs
node tools/test-audit-mahjong-capture.mjs
node tools/test-audit-debug-packets.mjs
dotnet build Mahjong.Plugin.Dalamud/Mahjong.Plugin.Dalamud.csproj -c Release --no-restore --nologo
```

归档命令和字段语义见 [操作说明](../live-archive-audit.md)。真实模型命令（输出目录不可已存在）：

```powershell
wsl.exe -d Ubuntu -e python /mnt/e/SapphireServer/FFXIV-AutoMahjongSolver/tools/replay_mortal.py --mortal-dir /mnt/e/SapphireServer/FFXIV-AutoMahjongSolver/.loacl/Mortal/mortal --input /mnt/e/SapphireServer/FFXIV-AutoMahjongSolver/artifacts/live-audit-20261003/replay-0060.json --output /mnt/e/SapphireServer/FFXIV-AutoMahjongSolver/artifacts/half-replay-new
```

## 尚待落实

P0 还未全部关闭：角色/动作级异常关联、役满审计修复、到期鸣牌弹窗回归和安全预热仍在计划中。P1 调度重构、半庄线上准入、规则补齐、东风终局特征实验与新权重训练均未在本轮完成。不根据这次输入回放宣称棋力提高。


## 同日续批更正与进展

实际传输实现已经把后台反应放入通知队列，在 framework 的 Poll 中分发；本报告早先关于“回调在后台”的注释修改判断不正确，续批已更正。空进程预热和离桌/配置切换隔离已完成，测试及实机限制见 [预热验收](20261003-mortal-prewarm.md)。上文“仅诊断、预热待做”保留为首批交付时点描述。
