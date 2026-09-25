# 使用旧录包离线验证 Mortal

## 当前采集策略

无需用户为了开发反复随机匹配。先重放已有录包、分析已捕获的杠及流局、用构造事件测试状态恢复。只有确认某个字段在现有材料中完全没有独立证据时，才提出具体、有限的补采请求，并先准备好能记录该字段的工具。不能让用户靠打很多把来弥补尚未实现的解码。

真实报文的字段意义仍需真实证据。构造事件可以验证杠后逻辑、重复/跨局事件及恢复流程，不能证明某个游戏内存偏移或 opcode 正确。当前工具明确区分这两件事，输出一直是 `runtime_eligible:false`。

## 两步重放

工具离线运行，不连接 FF14、不注入输入、不点击游戏按钮，也不更改用户插件配置或候选 profile 的 verified 标志。

先导出实际生产 C# 解码器和公开局面归约器接受的事件：

```powershell
dotnet build tools/Mahjong.PacketReplay/Mahjong.PacketReplay.csproj -c Release -p:RestoreLockedMode=true

dotnet tools/Mahjong.PacketReplay/bin/Release/net8.0/Mahjong.PacketReplay.dll data/protocols/20260915-international-candidate.json tests/Mahjong.Plugin.Game.Tests/RegressionFixtures/20260924-full-match.json artifacts/offline-full-match.json
```

输入支持已封口的 debug-packets `.ndjson` 或带 variant/packets 的脱敏 `.json`。EmjL 须使用对应候选 profile。输出父目录须存在，输出文件必须不存在。原始捕获校验版本、变体、结束标记、stream_complete、丢弃/拒绝数量和包数；重复或乱序直接失败，不通过排序掩盖问题。

再在现有 Mortal Python 环境运行真实 runner。此机器实际发行版为 Ubuntu，旧 `.loacl/mortal-bridge.json` 中 Ubuntu-25.10 已过时；本次没有改动那份用户旧配置。

```powershell
wsl.exe -d Ubuntu -e python /mnt/e/SapphireServer/FFXIV-AutoMahjongSolver/tools/replay_mortal.py --mortal-dir /mnt/e/SapphireServer/FFXIV-AutoMahjongSolver/.loacl/Mortal/mortal --input /mnt/e/SapphireServer/FFXIV-AutoMahjongSolver/artifacts/offline-full-match.json --output /mnt/e/SapphireServer/FFXIV-AutoMahjongSolver/artifacts/offline-model-run
```

可重复传 `--input` 批量检查旧对局；输出目录必须不存在。其他机器替换成本机实际路径及发行版。依赖 Python 3.11+ 和原有 Mortal 环境，不下载或替换模型。

每小局独立启动 runner，输入带 session/hand/sequence，逐条等待关联确认；默认单次等待 60 秒（包括首条加载模型），超时结束该子进程并记录具体包号，不影响后续小局。此离线时限不修改插件的实战操作时限。只有已有结果边界、且无解码/归约异常的小局送入模型；缺开局的片段、尚未支持的杠和缺失结果边界会明确跳过。

模型建议只保存到本地 `reactions.ndjson`，下一条输入始终沿用历史真实动作。回放不按模型推荐重新发牌，因此不是模型对战，更不能根据建议与历史出牌是否相同评估胜率。报告记录来源、生产解码器、模型权重、libriichi、配置和 runner 哈希；对手暗手和摸入牌仍然为未知。

## 未验证范围不会被隐藏

- 开始供托按 Doman 起始十万点与四家已确认分数推导；差额必须是非负整数千点，异常分数拒绝解码。报告标明这是规则推导，不是已验证的供托偏移；历史导出中的固定 0 值不能冒充新结果。
- 未映射 opcode 只统计数量，不声称它们与麻将无关；从选定 fixture 导出也不声称包含了捕获中的所有包。
- 0x018D / 264 字节的普通流局边界已接入生产解码；其余字段和特殊流局语义保持未知。缺实际结果的手局仍明确标注并跳过模型。
- 构造的每局子进程结束仅用于释放离线模型，不写回捕获，也不会触发游戏操作。
- “回放无异常”只证明这些事件能被当前模型接收，不证明新增宝牌、模式差异、押金、完整公开历史、实时恢复或游戏执行均正确。

## 2026-09-24 首次运行结果（流局支持前）

本地完整输出在 `artifacts/mortal-replay-20260924/`（Git 忽略，原始文件不改写）。输入为 13:22 和 14:09 两场已有脱敏网络样本，以及 15:54 新场原始捕获：

| 输入 | 实际配牌 | 完成真实模型回放 | 明确跳过 |
|---|---:|---:|---|
| 13:22 | 5 | 5 | 0 |
| 14:09 | 5 | 4 | 第 2 小局，加杠候选 sequence 133 / action 0x140 |
| 15:54 | 8 | 6 | 第 6、8 小局，当前解码器缺流局结果边界 |

共 18 个小局，15 个成功、0 个模型错误、3 个跳过。真实 runner 确认 **1454 条输入**（含逐局生命周期），返回 **216 条建议/响应**。模型和配置未更换。权重 SHA-256 `0a88ddad649804d085491b5397d895f596b0e55f30632c549ea145bb44786563`；libriichi SHA-256 `f3f336e16c90de3e5acd989ef1cdc7cb06aa30c6bf2af920aab72cd86fe0bfac`。

新增回归覆盖旧完整样本、对手暗牌隔离、缺结果、未知杠后隔离至真实开局、长度错误、乱序/重复、空样本、版本变体门禁和捕获完整性；Python 覆盖建议不替换历史动作、精确错误包号、关联响应及超时清理。工具已纳入 CI。离线工具的交付不需要用户再次进游戏或热重载插件；当前实战 Mortal 门禁保持原状。

本轮完整解决方案 **973 项 .NET 测试通过**，Python **12 项通过**；独立 CLI 构建零警告、零错误。

## 接入流局后的复验

本地输出 `artifacts/mortal-trial-20260924/model-run-1/report.json`：三场共 18 小局，**17 成功、0 错误、1 跳过**。第三场改用新增的完整脱敏 fixture，8 小局全部完成；唯一跳过仍为第二场第 2 小局的 sequence 133 / 0x140 加杠。真实 runner 确认 1744 条输入，返回 262 条建议/响应。模型、libriichi、配置和 runner 哈希与首次相同。

这些结果仍为离线诊断；新的实战入口是另一个默认关闭、范围受限且有逐局保护的 [Mortal 有限试用](reviews/20260924-mortal-limited-trial.md)。离线通过本身不会改写配置或解锁完整协议。

2026-09-25 新增四个带供托开局的脱敏回放：真实模型全部完成，共 427 条关联输入、66 条响应，见 [覆盖修复报告](reviews/20260925-mortal-decision-coverage.md)。随后通过自身摸牌、立直点击与服务端弃牌回执独立核对了 `0x113`，第 3 个样本现可在有限试用中通过 sequence 866 并走到结果；新增六段完整组合立直手局也通过实际模型（689 条输入、96 条响应）。两段包含更早未验证杠的历史继续跳过。见 [组合立直证据与回放](reviews/20260925-combined-riichi-protocol.md)。离线兼容不替代字段验证或胜率评测。


## 2026-09-25 杠与新增宝牌

旧报告的“所有杠均跳过”限制已被 `doman-east-opponent-kan-v4` 的对手杠支持替代。原始 fixture 保持不变，历史 `unsupported_action` 注释是旧审计结论；当前断言要求既有加杠全场的五小局都可回放，布局切换轨迹遇对手暗杠不重启。

新增独立核对命令（输出不能覆盖已存在文件）：

```powershell
python tools/audit_kan_dora.py --archives '<插件配置目录>/match-archives' --output '<新报告路径>.json'
```

它仅读取已结束且未标记损坏的归档，以结算时公开的赢家手牌、已知副露和结算宝牌数核对起始/新增/里/赤宝牌；只处理结果包首个赢家块。没有公开手牌的结果明确列为跳过，不计为成功。不向推理过程提供结果手牌，也不修改协议准入。131 个可核对结果全部一致，另 1 个跳过；其中含杠结果 11 个，4 个有非零新增宝牌收益。

另从 15 份完整 Emj 抓包提取 16 个完整含杠手局（保留各自开局至真实结算），通过原生产回放命令和实际 Mortal：16 成功、0 失败、0 跳过，1874 条关联输入、288 条响应。覆盖 8 次暗杠、3 次明杠、6 次加杠、17 次补牌/新增宝牌，其中一次连续加杠。私有回放产物留在 `artifacts/kan-20260925/`，不提交原始个人抓包。

字段证据、推断边界、自杠限制、验证版本见 [本次验收](reviews/20260925-kan-dora-protocol.md)。这些是输入兼容与恢复检查，不是胜率或新模型验收。


## 2026-09-25 自家暗杠与补牌

当次限定准入升级为 `doman-east-self-ankan-v5`。两份新实机记录的 12 个完整手局均通过生产解码器与公开局面归约器；其中两个自家暗杠手局从开局到实际结算再经过真实 Mortal，2 成功、0 失败、0 跳过，共 279 条关联输入、42 条响应。没有改写后续真实动作，也没有更换模型权重。

私有导出与模型回放报告位于 `artifacts/self-ankan-20260925/`。字段交叉验证、红牌边界、确认去重、自动立直修复及用户操作见 [本次修复验收](reviews/20260925-self-ankan-fix.md)。


## 2026-09-25 自家加杠

当前限定范围为 `doman-east-self-kakan-v6`，新增普通摸牌后的自身 `0x140`。对应完整抓包的 6 个手局均通过生产解码器与公开局面归约器；加杠所在第 2 手局从开局到实际结算通过真实 Mortal：1 成功、0 失败、0 跳过，101 条关联输入、17 条响应。已有碰牌升级、补牌和后续实际舍牌均保持原序，不用模型建议篡改录制行为。

证据及限制见 [自家加杠修复](reviews/20260925-self-kakan-fix.md)。私有产物位于 `artifacts/self-kakan-20260925/`。自家明杠和补牌后的连续加杠仍不准入；不把兼容回放当成接管率或胜率证明。
