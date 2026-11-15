# 后续开发规划与技术路线（2026-11-15）

基线提交：`04d8c43`（插件版本 `0.1.2.3`，Dalamud API `15`）。本文档在 [10 月 3 日计划](plan-20261003.md) 与 [10 月 11 日决策提升复盘](plan-20261011-decision-uplift.md) 的基础上，结合截至 2026-11-15 的已落地成果，制定下一阶段按优先级分轨推进的开发规划与验收标准。

---

## 1. 当前基线与已解决项（截至 2026-11-15）

10 月至 11 月的迭代已经解决了一批 P0 稳定性与补丁适配问题：

- **预热与自动离场修复（2026-10-03 ~ 10-05）**：
  - 将 `wsl.exe` + Python + PyTorch 初始化的约 3.7–6.9 秒冷启动移至桌外空进程预热（[预热验收](reviews/20261003-mortal-prewarm.md)），并在退桌时清理 pre-roll 与消费者队列，消除首摸超时与跨场污染。
  - 修复 `DutyCompleted` 后结算画面短暂消失导致连续采集卡在局内的问题（[离场修复](reviews/20261005-collection-exit-fix.md)）。
- **东风进度特征通过大规模统计验收（2026-10-12，`52c0bbb`）**：
  - [east-progress-v1](reviews/20261012-east-progress-acceptance.md) 将东风场观测特征 row 27 的进度重映射为 `(kyoku + 3) / 7`，在 1,500 组（12,000 场）`doman-quick` 对照中相对 baseline `v4` 取得显著收益：平均名次 **`-0.032`（95% CI `[-0.051, -0.012]`）**、四位率 **`-2.1pp`**、前二率 **`+1.0pp`**，已作为 `MortalEastProgress` 选项接入实机。
  - 同期多轮离线模型实验（A/B/C/D、局部解冻、`finetune_full.py` 全网 CQL 微调、4 局面反事实搜索、对手信念粒子滤波）均未显著超越 `v4`，明确了“继续对对称强模型小规模微调”性价比极低（[复盘](plan-20261011-decision-uplift.md)）。
- **7.58 适配与运行时 Opcode 自动学习（2026-11-15，`8060d81`、`4ff7625`）**：
  - 升级并固定 Deucalion `1.5.1`（SHA-256 校验），重映射国际服 7.58 `Emj` 7 个 opcode（[7.58 复盘](reviews/20261114-758-protocol-remap.md)，1,170 包 4 手零错，但该局未出现杠；后续 42 份录包的杠/流局核验见 [复盘](reviews/20261116-758-kan-and-0x211.md)）。
  - 新增 [`ProtocolDiscovery`](../Mahjong.Plugin.Game/Mjai/ProtocolDiscovery.cs) 与 [`EmjTableReader`](../Mahjong.Plugin.Dalamud/GameState/EmjTableReader.cs)：无内置 profile 时在桌内缓冲同长度报文（含 10 秒 pre-roll），退桌后结合客户端 `EmjTable` 点数真值与生产解码器穷举候选组合（上限 4096 组），自动保存至 `learned-protocols/`。
- **全插件中文 UI 本地化（2026-11-15，`04d8c43`）**：
  - 新增 [`Loc.cs`](../Mahjong.Plugin.Dalamud/UI/Loc.cs) 随 Dalamud 界面语言自动切换英文/中文，保持状态常量与日志底层仍为英文以便检索与回归对比。

---

## 2. 当前瓶颈与待解缺口诊断

虽然东风快排与本地兜底链路已稳定可用，但代码与实机证据显示存在五个明确瓶颈：

| 类别 | 当前表现与代码位置 | 核心影响 |
|---|---|---|
| **协议与模式受限** | 所有协议 profile 仍为 `"verified": false`，受 [`DomanMortalTrialGuard`](../Mahjong.Plugin.Game/Mjai/DomanMortalTrialGuard.cs) 锁定在 `"doman-east-self-kakan-v7"`；[`MahjongPacketMjaiDecoder`](../Mahjong.Plugin.Game/Mjai/MahjongPacketMjaiDecoder.cs) 仍拒绝自家大明杠 `0x400`、连续加杠 `0x240` 与补牌后立直摸切 `0x213`（7.58 的 42 份录包中均无样本）；7.58 杠/宝牌/流局已核验（26 次杠、25 次流局，217/217 手可回放，见 [7.58 杠与 0x211 复盘](reviews/20261116-758-kan-and-0x211.md)）。 | 南场（半庄）即使离线 9/9 手回放通过仍无法线上启用 Mortal；实机遇到特定自家杠会触发隔离降级。 |
| **Mortal 部署门槛高** | [`MortalProcessClient`](../Mahjong.Plugin.Dalamud/Mortal/MortalProcessClient.cs) 硬编码启动 `wsl.exe` 执行 `mortal_runner.py`（依赖 Ubuntu + Python + PyTorch + Rust 编译的 `libriichi`）。 | 普通 Windows Dalamud 用户几乎无法开箱使用最强策略，插件传播与实测反馈范围受限。 |
| **多变体与中文环境未打通** | `2026.11.03` 仅有 `Emj` profile，缺少 `EmjL`；`jp.json` 未实机验证，尚无国服/韩服布局；[`ContinuousCollectionLoop`](../Mahjong.Plugin.Dalamud/Actions/ContinuousCollectionLoop.cs) 的英文名称与 `variant == "Emj"` 硬编码已在 B2 中移除（见下），但非英客户端尚无实机验证。 | 虽然 UI 已中文化，但非英客玩家或北美/日服用户无法直接享受自动排本与完整协议/布局支持。 |
| **实机数据与训练闭环断裂** | [`TrainingCorpusWriter`](../Mahjong.Plugin.Dalamud/Logging/TrainingCorpusWriter.cs) 固定写入 `training_ready = false`；[`training_dataset.py`](../tools/training_dataset.py) 固定输出 `server_action_not_verified` 与 `training_eligible = False`；[`ServerCallOutcomeTracker`](../Mahjong.Plugin.Dalamud/Actions/ServerCallOutcomeTracker.cs) 仅校验鸣牌确认，未覆盖舍牌/立直/和牌；实机对手放铳率（~15.2%）显著高于噪声池（12.8%）。 | 无法利用真实 FF14 对局数据做可靠行为分析、对手画像标定与高保真训练/评测。 |
| **UI 兜底** | [`BaseEmjVariant`](../Mahjong.Plugin.Dalamud/GameState/Variants/BaseEmjVariant.cs) 在无网络时仅提供自身手牌/舍牌与点数，不读取对手河牌、副露、立直状态与风位（`#48`）；[分发协议](dispatch-protocol.md) 中 `post-MinKan` 与连续第二次碰停顿未闭环。 | 断包/降级或新补丁未适配期间保底策略缺乏防守依据。 |

---

## 3. 分轨后续发展规划

后续按 **4 条并行轨道（Track A–D）** 推进，不以未验证的“换权重”作为前置阻塞条件。

### Track A（P0）：协议完整化、半庄准入与动作状态机收口

**目标**：让线上 Mortal 覆盖普通玩家最常用的**半庄段位赛**与完整杠/流局场景，消灭已知动作停顿。

1. **A1 · 补齐 7.58 杠/流局样本并验证自家 `0x400` / `0x240`**
   - ~~利用自动录包或既有脱敏归档，确认 7.58 下杠（暗杠/明杠/加杠）与流局字段边界，核验 [20261103-international-candidate.json](../data/protocols/20261103-international-candidate.json) 沿用 09.15 的 kan opcode。~~ **已完成（2026-11-16）**：42 份 7.58 录包含暗杠 14 次、加杠 7 次、大明杠 5 次（自家依次为 2、1、0 次），每次都接同一玩家的 `0x200` 补牌和新宝牌；25 次 `0x03E1` 流局（09.15 为 `0x018D`）的点数与下一局开局一致；`audit_kan_dora.py` 核对 189/189 个结算一致。唯一的失败手局是自家暗杠后岭上手切立直 `0x211`，已按 UI、立直棒扣分和后续摸切核对后接入，回放由 216/217 手变为 217/217 手。详见 [复盘](reviews/20261116-758-kan-and-0x211.md)。
   - 拿到自家大明杠（`0x400`）、补牌后连续加杠（`0x240`）、补牌后立直摸切（`0x213`）的独立 UI/服务端对照样本后，再解除 `MahjongPacketMjaiDecoder` 对它们的硬拒绝，并新增对应的解码与 MJAI 归约回归测试。截至 2026-11-16，三者都没有 7.58 样本。
2. **A2 · 开放半庄（South）线上准入**
   - 2026-10-03 已完成完整半庄（9 手含连庄）离线回放验证，下一步聚焦**线上模式识别与边界隔离**：
     - 优先研究通过 `FFXIVClientStructs`（`AgentEmj` / `EmjTable`，复用 `EmjTableReader` 路径）读取真实规则与局制（东风 vs 半庄、是否允许食断 `AllowsKuitan`、初始庄家）。
     - 在 `DomanMortalTrialGuard` 中从“一律拒绝 `roundWindIndex != 0`”升级为按实机已验证模式放行（例如区分东场/半庄进度映射，避免对半庄误开启 `east-progress-v1`）。
     - 补齐半庄终局边界、连庄/西入否定行为及跨场隔离测试后再默认开放。
3. **A3 · 清理已知动作分发卡顿**
   - 按 [dispatch-protocol.md](dispatch-protocol.md) 捕获或复现 `post-MinKan`（暗/明杠摸岭上瞬间手牌计数错位）与“连续第二次碰”状态停顿，修复 [`AutoPlayLoop`](../Mahjong.Plugin.Dalamud/Actions/AutoPlayLoop.cs) / `MeldTracker` 的等待条件或重试去重逻辑。

**验收门槛**：半庄完整对局在线下脱敏回放与实机观察均 0 解码失败、0 隔离；`MortalEastProgress` 仅在确认东场模式时激活；已知卡顿场景不再出现无限重试或永久阻塞。

---

### Track B（P1）：去 WSL 原生推理与多区/中文生态落地

**目标**：显著降低 Mortal 的安装与配置门槛，并将“自动学习 + 中文界面”的能力延伸到非英客及多区域客户端。

1. **B1 · Mortal 推理架构演进（解耦 WSL 强依赖）**
   - **近期（低成本）**：保留 `MortalProcessClient` 既有基于标准输入输出的 MJAI JSONL 协议契约，增加“配置模式”区分：支持直接连接本机或局域网独立启动的 bridge 服务（延续 [`mortal-bridge.mjs`](../tools/mortal-bridge.mjs) 思路）以及一键环境自检提示（WSL 发行版、Python 路径、`libriichi` / 权重可读性、启动阶段明确报错）。
   - **中期（核心体验跃迁）**：探索**纯 Windows 原生推理运行时**：
     - 将 `Mortal` PyTorch checkpoint 导出为 ONNX / TorchScript，由插件或轻量原生 sidecar 通过 `Microsoft.ML.OnnxRuntime`（CPU / DirectML）直接加载；
     - 特征工程层复用纯 C# 或独立编译的 Windows 原生 `libriichi` FFI DLL 生成输入张量，消除对 WSL2、Ubuntu 及数 GB Python/PyTorch 环境的强依赖，并将启动准备耗时从数秒压缩到亚秒级。
     - 迁移前必须在固定种子集上验证“原生运行时输出动作 vs `mortal_runner.py` 参考输出”100% 一致（允许浮点容差内的决策校验）。
2. **B2 · 连续采集去英文硬编码与多客户端变体扩展**
   - 修改 [`ContinuousCollectionLoop`](../Mahjong.Plugin.Dalamud/Actions/ContinuousCollectionLoop.cs)：`DutyId = 766`、`TerritoryId = 831`、`ContentId = 61005` 本身已具备跨语言唯一性，移除对 `ClientLanguage.English` 与英文文本 `"Novice Mahjong (Quick Ranked Match)"` 的强校验（或仅将其作为日志辅助信息），并将允许采集的变体从硬编码 `Emj` 改为基于“当前激活变体存在匹配布局 + 有效或可学习协议 profile”。
     - **已完成（2026-11-16，未实机）**：任务行改为按客户端语言表的数字列（行 766 → TerritoryType 831、Content 61005、ContentType 19）校验，名称只写日志；最终结算/离场门槛改为 [`CollectionGate`](../Mahjong.Plugin.Dalamud/Actions/CollectionGate.cs)（窗口变体 = 已解析布局的 `addonName`，且当前版本有该变体的已验证或试用范围 profile）。“可学习”仍走原有的手动对局学习流程，采集本身不学习。尚无人在国服/日服/北美等非英客户端实机跑过，详见[持续采集说明](continuous-collection.md)。
   - 依托 `/mjauto variant dump` 补全 `EmjL`（NA 7.58）与 `jp.json` 实机验证；若国服/繁中客户端需要接入，通过新增 `data/layouts/*.json` 完成零代码注册（[布局说明](../data/layouts/README.md)）。
3. **B3 · 增强补丁自愈（`ProtocolDiscovery` 二期）**
   - **跨场聚合**：当单场对局缺少稀有包（例如 0 杠、流局（7.58 为 `0x03E1`）未发生）时，保留部分映射状态或支持合并同一版本/变体的多次桌面样本，减少单个对局样本维度不足导致的歧义。
   - **一键导出脱敏 Profile**：在设置界面为 `learned-protocols/` 增加导出脱敏候选 JSON 按钮，方便用户直接提交 Issue/PR 供上游收录与验证。

**验收门槛**：非英文客户端能正常启动并完成连续采集；原生推理原型在离线回放上与 Python `mortal_runner.py` 决策一致率达标且冷启动 < 1 秒；多场聚合学习单元测试覆盖缺失杠/流局场景。

---

### Track C（P2）：实机数据闭环与 FF14 生态定向策略提升

**目标**：吸取 10 月实验经验，停止无方向的小样本微调；先建立高质量 FF14 实机数据管道与拟真对手模型，再推进真正有效的策略收益点。

1. **C1 · 打通实机动作闭环与训练语料可用性（解除 `training_eligible = False`）**
   - 扩展 [`ServerCallOutcomeTracker`](../Mahjong.Plugin.Dalamud/Actions/ServerCallOutcomeTracker.cs)：除鸣牌外，补齐对 `Discard`（对应 `MjaiDahai { Actor: 0 }`）、`Riichi`（对应 `MjaiReach { Actor: 0 }`）、`Hora` 与超时/主动 `Pass` 的服务端确认闭环，并将确认事件写入游戏日志与归档。
   - 完善 `0x018D` 等流局与小局结果归因，消除实机复盘中仍大量出现的 `unknown` 和牌/放铳标签。
   - 在满足“开局完整、终局观测、服务端动作核对一致、无丢包/无隔离、协议对应动作已验证”的前提下，使 [`TrainingCorpusWriter`](../Mahjong.Plugin.Dalamud/Logging/TrainingCorpusWriter.cs) 与 [`training_dataset.py`](../tools/training_dataset.py) 能真正产出 `training_eligible = true` 样本（严格维持现有 PII 脱敏白名单）。
2. **C2 · 构建“FF14 实机标定对手池”**
   - 针对 [10 月 11 日复盘](plan-20261011-decision-uplift.md) 暴露的核心偏差（真实 FF14 玩家放铳率约 15.2%，而当前噪声池仅 12.8%）：从脱敏实机语料统计 FF14 菜鸟/段位桌真实分布（各巡目立直率、副露率、放铳率、和了巡目与打点分布），用校准后的异质参数池替代对称 `v4` 克隆用于离线评测。
3. **C3 · 延续低风险“观测对齐与顺位目标适配”路线**
   - 在“高保真 `doman-quick` / FF14 拟真对手池”上优先验证无需海量重训的改进：
     - **规则对齐**：FF14 无西入、同分按初始庄家顺位、东场/半庄末局差异等特征编码核查（延续 `east-progress-v1` 的思路）；
     - **顺位/段位激励重加权**：针对 FF14 段位分/R 值奖励结构（保前二、强避四位）探索推理后价值头或末局风险系数轻量校准；
     - 若需重新训练，仅在标定好的非对称 FF14 对手池中、采用充足样本预算与 1,000+ 组正式验收门禁（平均名次差 ≤ -0.03 且 95% CI 上界 < 0、四位率差上界 ≤ +0.01）推进。

**验收门槛**：实机闭合局在 `training_dataset.py` 导出时 `server_confirmed_action` 与胜负/放铳标签非空且通过完整性校验；任何新策略默认生效前均须通过 `docs/evaluation.md` 规定的独立验收种子组。

---

### Track D（P3）：UI 直读桌面兜底与仓库工程卫生

**目标**：让本地兜底与交互校验不再“对对手两眼一抹黑”，同时清理文档与仓库历史遗留。

1. **D1 · 补全 UI 对手公开信息直读（原 Roadmap `#48`）**
   - 基于 `FFXIVClientStructs`（延续 `EmjTableReader` 的路线，读取 `AgentEmj` / `EmjTable` 公开内存），为 `BaseEmjVariant` 补全四家河牌、副露、立直状态、自风/场风与手切/摸切标记。
   - 收益一：无 Deucalion/未适配新补丁期间，`EfficiencyPolicy` / `EnhancedSearch` 与提示界面也能正确避险立直者现物/筋牌；收益二：对 Mortal 抓包流提供更精细的局内实时交叉校验，及时发现并纠偏潜在丢包。
2. **D2 · 其余配置与模块卫生**
   - 后续升至配置 Schema v4 时顺手清理已仅作历史反序列化兼容的 `Configuration.InstallId` 与 `ConfigMigratorV1ToV2`。
   - 为 `Mahjong.Plugin.Dalamud` 评估启用与纯逻辑模块同等级的警告门禁（`TreatWarningsAsErrors`），并明确 `Mahjong.Tuner` 在实机语料闭环后的用途定位。

---

## 4. 建议里程碑节奏

| 里程碑 | 包含工作项 | 主要目标 | 非目标（本阶段不做） |
|---|---|---|---|
| **M1（近期可用性收口）** | Track A1 + A3 + Track B2（去英文硬编码） + Track D2（配置/警告卫生） | 修复动作卡顿，让 7.58 补齐杠样本并打通非英客连续采集 | 不重启大规模模型权重训练 |
| **M2（全模式实机开放）** | Track A2 + Track B3（跨场学习与一键导出） | 安全放开半庄线上 Mortal 准入，完善补丁后自动学习生态 | 不强制重写 Mortal 推理后端 |
| **M3（免 WSL 与直读底座）** | Track B1（原生运行时/ONNX 方案验证） + Track D1 | 降低普通玩家部署门槛，补齐 UI 对手公开桌面直读 | 不将未经一致性验证的原生运行时设成默认 |
| **M4（数据闭环与棋力再突破）** | Track C1 → C2 → C3 | 完成实机标注闭环，构建 FF14 拟真对手池并做顺位/特征专项优化与千组验收 | 不凭少数实机对局胜负调参或绕过千组门禁 |

---

## 5. 常规验证基准

任何改动合并前继续执行现有分层验证基线（具体环境要求见 [CI 工作流](../.github/workflows/ci.yml)）：

```powershell
# 纯逻辑与 PacketReplay + 插件全套测试（Windows 环境，含 pinned Dalamud/Deucalion）
dotnet test Mahjong.Plugin.Dalamud.sln -c Release -p:RestoreLockedMode=true --nologo --verbosity minimal

# Python 工具与 runner 单测
python -X utf8 -m unittest discover -s tools/tests -v

# Node 包解析与审计测试
node tools/test-parse-mahjong-packets.mjs
node tools/test-audit-mahjong-capture.mjs
node tools/test-audit-debug-packets.mjs
```
