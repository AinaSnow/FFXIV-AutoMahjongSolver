# A3：鸣牌窗口派发停顿复核（7.58，44 场）

范围：[44 场复核](20261116-live-44-match-review.md) 中四次 Mortal 超时和三次 Pass 超时。证据是本机 `match-archives` 的只读副本（`summary.json`、`packets.ndjson`、`games/*.ndjson`），游戏版本 `2026.11.03.0000.0000`，profile [20261103-international-candidate.json](../../data/protocols/20261103-international-candidate.json)。下文的“行号”是对应手局 ndjson 的行号，`P<n>` 是该场 `packets.ndjson` 的行号。原始包和归档都没有复制进 Git，回归测试只保留牌种、状态码和动作值。

报文记法：message 638（本版 opcode `0x0211`）为摸牌/鸣牌，`@0` 绝对座位，`@4` 动作（`0x100` 摸牌、`0x130` 暗杠、`0x500` 碰、`0x600` 吃）；message 641（`0x0382`）为舍牌，`@8` 动作（`0x110/0x112` 普通/摸切、`0xA10` 鸣牌后舍牌、`0x140` 加杠）。

## 结论一览

| # | 样本 | 结论 | 处理 |
|---|---|---|---|
| 1 | `113816Z` 第 1 手，立直后暗杠 | **已证实**：立直暗杠弹窗没有动作标签，`AnKan` 走了 `SelectItem`，该路径在 state 6 无标签弹窗上从未生效；之后等待服务端杠回执的锁让同一提示 8 秒内无人处理 | 修复派发路径：state 6 的 `AnKan` 与 `Tsumo` 一样改发 `[11, 行号]`。`[11,0]` 在立直弹窗上能否提交仍待实机确认 |
| 2 | `091643Z` 第 1 手、`151730Z` 第 4 手，他家吃/碰后 | **已证实**：残留行，不是鸣牌机会；服务端没有等我们 | 他家鸣牌后、下一张他家舍牌前出现的仅含碰/吃/明杠的行，不再等待 Mortal，也不点击 |
| 3 | `111808Z` 第 1 手，自家碰后舍牌 | **已证实**：与 #2 同属残留行，触发点是自家舍牌；不是“连续第二次碰” | 与 #2 同一修复 |
| 4 | 11-14 三次 Pass `timeout` | **分类完成**：三场没有加载可用协议（纯 UI 模式），提示在他家摸牌后残留，`[11,1]` 无效 | 不改。启用网络后已有 `[8]` 过期提示取消路径 |

## 1. 立直后暗杠未执行

### 时间线（`match-20261115-113816Z`，`game-…-hand01.ndjson`）

| UTC | 行 | 事实 |
|---|---|---|
| 11:40:34.478 | P85 | 自家摸到第四张中（physical `0x84`） |
| 11:40:34.511 | 542 | state 6，14 张，`our_riichi=true`，`legal = AnKan, Pass` |
| 11:40:35.067 | 543 | Mortal `AnKan`，评估 46 ms |
| 11:40:35.072 | 544 | 动作 15 返回 `Ok`；**没有 `input-callback`** |
| 11:40:35.078 | 546 | `superseded`，路径 `call:list-select(option=0)`（只是 revision 闪了一次 legal=None） |
| 11:40:35.095 | 547 | 同一提示 `AnKan, Pass` 恢复，此后 8 秒不变；没有 `0x130` 报文 |
| 11:40:43.088 | 548 | `local-fallback` Pass（“Mortal timeout”），`tracker.melds = 1` |
| 11:40:43.090 | 549–551 | `[8]`，`call:opcode-8(riichi-kan-pass)` |
| 11:40:43.217 | P86 | 自家 `0x112` 摸切中 |

### 根因

1. **派发路径。** `InputDispatcher.UseClassicCallPath` 只有在 state 15、能在 AtkValues 中扫到动作标签，或 state 6 的 `Tsumo` 时才走 `[11, option]`；立直暗杠弹窗没有标签，`AnKan` 因此落到 `AtkComponentList.SelectItem`。在 state 6 无标签弹窗上，`SelectItem` 的所有现场记录都没有生效：
   - 2026-09-23：state 6 自摸两次 `SelectItem` 无回调、无变化；人工点击记录为 `[11,0]`（[复盘](20260923-international-match.md)）。
   - 2026-09-24：立直杠弹窗 `list-select(option=1)` 连续 5 次无回调、无变化；人工取消记录为 `[8]`（[复盘](20260924-six-match-log-review.md)）。
   - 本例：`list-select(option=0)` 同样无回调、无 `0x130`。
   
   对照：本批 3 次自家杠都在 state 6 用 `[11,0]` 提交，每次都有自动回调，服务端回执分别在 112 ms（`134529Z` 暗杠，P48）、161 ms（`123744Z` 暗杠，P111）、128 ms（`134702Z` 加杠，P302）后到达；它们都非立直，弹窗带标签。另外 46 次 state 6 立直宣言同样走 `[11,0]`。
2. **为什么没有重试。** `DispatchAnkan` 在返回 `Ok` 后立即执行 `MeldTracker.Record(AnKan)` 和 `ConfirmKanDecisionDispatched`。有限试用下后者会设置 `awaitingSelfKanConfirmation`，清空推荐并丢弃排队的反应，`TryChoose` 一直等到服务端的 `MjaiAnkan` 才会解除。点击无效，这个回执不会来。约 3 秒重试冷却后，Mortal 等待计时开始，5 秒后超时（11:40:43.088），本地兜底在立直中选 Pass。不是去重或 revision 问题：推荐是被有意清掉的，Mortal 也没有新事件可以回应。
3. **副作用。** `MeldTracker` 记下了一个不存在的暗杠（行 548、581、620 的 `tracker.melds = 1`，直到本手结束）。本手处于立直、由游戏自动摸切，最终荣和由 terminal-guard 处理，所以没有造成第二次错误；但在非立直局面这会让本地兜底的副露算术偏差。本轮没有改这一点，见“未处理”。

### 语料统计

- 44 场中立直暗杠提示 2 次：`120723Z` 第 4 手（行 405，本地策略 Pass，`[8]` 后 `state-changed`）和本例。**没有任何一次成功的立直暗杠**，因此 `[11,0]` 在立直弹窗上的效果没有直接证据。
- 自家杠回执（`0x130/0x140/0x400` 中 actor 为自己）：3 次，均非立直，均为 `[11,0]`。
- 全部归档中 `call:list-select` 只出现过本例这一次。

### 修复与风险

`UseClassicCallPath` 增加 `state == selfDeclare && action == AnKan`。只影响 state 6 上**没有**动作标签时的 `AnKan`；有标签的普通暗杠原本就走 `[11, option]`，state 6 的 Riichi/Pass、state 28 和 state 15 路由不变；立直杠 Pass 仍用 `[8]`。

- 行号仍由 `CallPromptReader` 在点击前从实时列表按标签解析，本例中 `AnKan` 解析为 0；行变化或识别不到会返回 `HookFailed`，不会盲点。
- 最坏情况一：`[11,0]` 在立直弹窗上同样无效。结果与现在一致（约 8 秒后兜底 Pass），不会更差。
- 最坏情况二：`[11,0]` 被解释成其他行。弹窗只有杠和取消两项，而人工取消发的是 `[8]` 而不是 `[11,1]`，所以 `[11,0]` 落到取消的可能性很低；即使真的落到取消，后果也只是放弃这次杠，与现在相同。和牌选项不在此弹窗中，`Tsumo` 另有路径。
- 重复开杠：成功后仍由原有 `awaitingSelfKanConfirmation` 锁住，服务端回执约 110–160 ms 到达，与普通暗杠同一机制。

### 下次实机要看

- **确认修复**：立直中出现 `AnKan, Pass`、Mortal 选 `AnKan` 后，应看到 `input-callback values=[11,0] automated=true`，`action-outcome path=call:opcode-11(option=0)`，约 0.2 秒内出现自家 `0x130` 和 `server-confirmed … server=ankan`，随后 `0x200` 岭上牌。
- **否定修复**：仍是 `path=call:opcode-11(option=0)`，但没有 `0x130`、提示保持，8 秒后 `local-fallback` + `opcode-8(riichi-kan-pass)`。此时需要让玩家手动点一次立直暗杠并抓 `input-callback`（`automated=false`）以确定真实回调。
- 如果 `[11,0]` 之后出现的是摸切（`0x112`）而不是 `0x130`，说明它被当成取消处理，应立即撤回本修改。

## 2. 他家吃/碰后的空候选“碰/过”

### 时间线

`match-20261115-091643Z` 第 1 手（自家绝对座位 3）：

| UTC | 行 | 事实 |
|---|---|---|
| 09:18:15.776 | P58 | 对家（座位 1）`0x110` 舍 5p |
| 09:18:15.826 | 335–336 | 真实碰提示 `Pon, Pass` |
| 09:18:17.315 | 337–341 | Mortal Pass，`[11,1]`，`state-changed` |
| 09:18:20.178 | P59 | 上家（座位 2）`0x600` 吃，载荷手牌 `0x38/0x3C`（6p/7p） |
| 09:18:20.198 | 348–349 | state 15，`Pon, Pass`，候选为空；`av` 为 `[15,0,1,5,1,3,255,3,76014,76015,76016,…]`，即这组吃牌 5p-6p-7p 的展示 |
| 09:18:25.804 | 350–353 | `local-fallback` Pass（“no authoritative opponent discard”），`[11,1]`，`superseded`（状态不变） |
| 09:18:26.464 | P60 | 上家 `0xA10` 舍牌，UI 进入 state 20 |

`match-20261115-151730Z` 第 4 手情况相同：P 序列中他家 `0x600` 于 15:28:34.200，提示于 +0.02 s 出现，6.27 s 后兜底 Pass 无效（`superseded`），吃牌者 7.79 s 后舍牌。

### 根因与“服务端是否在等我们”

- 吃/碰之后、吃碰者舍牌之前，任何人都不可能获得新的碰/吃/明杠机会。此时网络侧最近一条事件是他家的鸣牌，`LiveMortalBridge` 的 `lastNetworkDiscard` 已清空，于是 `TryChoose` 停在 `waiting for authoritative opponent discard`，5 秒后超时，`RecoverUnresponsiveDecision` 还会重启 Mortal 进程并重放。
- 这是 UI 在播放他家鸣牌时把我们上一个提示行重新置为 state 15（`legal` 来自我们上一个提示的类型，而不是他家的鸣牌种类：他家吃时显示 `Pon, Pass`，他家碰时也出现过 `Chi, Pass`）。
- **服务端没有等我们**（已证实）：331 次他家吃/碰/大明杠中有 36 次在 +0.02 s 出现这种行，其中 34 次我们**没有任何点击**，桌面照常继续；有残留行与没有残留行的“鸣牌→吃碰者舍牌”间隔中位数分别为 1.59 s 和 1.60 s，无残留行的尾部也有 7.4–11.7 s。两次超时只是吃碰者本身慢（6.29 s、7.79 s），超过了“0.7 s 调度 + 5 s 等待”；兜底点击本身没有让状态变化，0.7–1.5 s 后的舍牌是巧合。
- “候选为空”不是区分特征：36 次中有 12 次带碰/吃候选（例如 `pon:[29]`）。

## 3. 自家碰后舍牌紧跟的碰提示

`match-20261115-111808Z` 第 1 手（自家座位 0）：11:19:28.109 Mortal 碰并于 11:19:28.238 由服务端确认（P43，`0x500`），11:19:29.253 舍 5p，P44 为自家 `0xA10`。20 ms 后（行 301–302）state 15 `Pon, Pass`、`pon:[1]`（手中 2m 对）。最近一条网络舍牌是自己的，Mortal 无可回应；11:19:36.209 兜底 `[11,1]`，`superseded`。下家 11:19:40.294 才吃了我们这张 5p（P45 `0x600`），此前我们不可能有任何鸣牌选项，服务端也不会等待我们。

- 这与 [dispatch-protocol.md](../dispatch-protocol.md) 的“连续第二次碰”（state 6、手牌 10 张、副露未被识别）不是同一问题：本例碰和后续舍牌都正常完成。本批 25 手有两次及以上自家鸣牌，没有出现 `hand state out of sync` 或 `oos-tsumogiri`，“连续第二次碰”在 7.58 语料中没有复现。
- 语料：在 41 场有报文的归档中，“自家舍牌之后”出现的仅含碰/吃/明杠的提示共 118 次，117 次没有触发任何决策或点击便自行结束，只有本例因下家思考 10.9 s 而超时。#1 里摸切中之后的 `MinKan, Pass`、`kan:[33]`（行 557）也属于这一类：它在下一次他家摸牌后由原有的 `[8]` 过期提示取消处理（行 581–584），没有造成停顿。

## 2/3 的修复

`LiveMortalBridge` 依据服务端事件维护 `callOfferPrecludedByNetwork`：自家 `dahai`、他家 `chi/pon/daiminkan` 时为真；任何其他推进牌局的事件（他家舍牌、摸牌、暗杠、加杠、开局、结束）为假；`reach/reach_accepted/dora` 不改变它；重启、隔离和续局时清零。新增 `IsStaleCallPromptWithoutOffer`：在 Mortal 启用、未隔离、上述标志为真，且提示只包含碰/吃/明杠加 Pass、闭手张数 ≡ 1 (mod 3) 时成立（复用 `IsCancellableOpponentPrompt`）。`AutoPlayLoop` 遇到这种行时直接跳过（不点击、不计 Mortal 等待），已排队的 call 动作在执行前也会放弃。

风险分析：

- **漏掉真实机会？** 按规则，自家舍牌后或他家鸣牌后、下一张他家舍牌前，不存在碰/吃/明杠机会。语料中两类窗口共 154 次，0 次随后出现 Mortal 决策。他家舍牌一到，标志立即变为假，恢复正常流程。若他家舍牌报文丢失（本批 dropped=0），旧逻辑也只能强制 Pass；新逻辑不点击，交给游戏自己的计时处理，结果同为不鸣。
- **荣和/自摸/出牌**：只要行里有 Ron、Tsumo、Discard 或 Riichi，就不会匹配，terminal-guard 和荣和处理没有改动。加杠后的抢杠由 `lastNetworkKakan` 处理，加杠事件会把标志置为假。
- **时序**：报文比 UI 早约 20 ms，桥接在同一帧先处理报文。若某次 UI 先到，行为退回旧逻辑（等待后兜底），不会更差。
- 仅在 Mortal 网络模式下生效。纯 UI 模式（如 #4）保持原样。

### 下次实机要看

- **确认**：他家吃碰或自家舍牌后约 20 ms 出现 state 15 的 `call-prompt`，之后**没有** `decision`/`action`，findings 中出现 `hand_state_paused` 且 `reason` 为 `stale call row: no opponent discard since our discard or the last call`；summary 中 `local-fallback` 和 `decision_timeouts` 不再由这类窗口产生。
- **否定**：在上述理由出现后，同一手中服务端紧接着把该窗口当成我们的响应（例如他家长时间不舍牌、桌面停住，或出现 `[8]`/人工输入才继续）；或者随后出现一张他家舍牌，而它的真实提示没有得到决策。

## 4. 11-14 的三次 Pass 超时

`match-20261114-113224Z` 第 3 手行 283–288、`match-20261114-120723Z` 第 1 手行 692–697 与第 4 手行 435–440。这两场的 summary 都是 “No admitted protocol for 2026.11.03.0000.0000”，决策来源全为 `stable`，归档中也没有报文（7.58 profile 当时尚未就位）。三次提示都在 state 5（有人刚摸牌）时出现，`av` 形如 `[15,0,<n>,76001,0,1,…]`，与 #1 中摸牌后残留的 `MinKan, Pass` 形态相同，属于“下一家摸牌后残留的旧提示”。`[11,1]` 对它无效，5 秒后记为 `timeout`；随后在下一张舍牌时自然消失，或在 `120723Z` 第 4 手由一次非自动 `[8]`（行 443）关闭。启用网络后，同类提示由已有的 `call:opcode-8(expired-opponent-prompt)` 处理（本批共 149 次，结果均为 `superseded`，未引发超时）。本轮不改纯 UI 模式。

## 未处理与未证实

- `[11,0]` 对立直暗杠是否生效没有直接证据，只有类比证据，需按上文信号确认。
- 无效的自家杠点击仍会让 `MeldTracker` 记下虚假暗杠，并锁住 Mortal 约 8 秒。若在点击后一段时间内既没有服务端回执、提示也未变化，就撤回记录并重新决策。这能缩短停顿，但如果判断有误会造成重复开杠，必须先拿到修复后的立直暗杠实机结果再决定，本轮不做。
- 纯 UI 模式下的残留行（#4）仍会点击无效的 Pass，没有网络证据可以安全区分。
- `post-MinKan` 手牌计数错位和“连续第二次碰”在本批中都没有复现，roadmap 中保留为待观察项。

## 验证

- `LiveDispatchRegressionTests.Recorded_riichi_ankan_accept_uses_button_callback_and_its_pass_keeps_cancel`（基于 #1 的牌型与状态），并把 `Recorded_self_draw_route_does_not_change_riichi_or_other_states` 中 `(6, AnKan)` 的期望改为走按钮回调。
- `MortalLimitedTrialTests.Retained_pon_row_after_another_players_chi_is_not_a_call_window`（#2 的报文顺序）与 `Retained_pon_row_after_our_own_post_pon_discard_is_not_a_call_window`（#3 的报文顺序）。
- 临时撤回两处修复后，上述 4 个用例全部失败；恢复后整套 1,311 项通过，0 警告。
