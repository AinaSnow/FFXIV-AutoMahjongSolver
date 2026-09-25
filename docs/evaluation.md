# 完整对战评测

正式策略比较使用已安装 Mortal 的 `libriichi.arena.OneVsThree`，一名候选对固定三名对手。每个 seed 候选轮换四座，基线再运行同 seed 四座，共八场。原 C# Simulator/WeightTuner 仅保留为快速逻辑冒烟，调参命令须显式 `--logic-smoke`。

## 构建与运行

安装 `global.json` 指定 SDK 和 .NET 8 runtime，在仓库执行：

```sh
dotnet build Mahjong.Policy.Mjai -c Release -p:RestoreLockedMode=true
python3 -m unittest discover -s tools/tests -v
```

在已具备 libriichi/torch 的 Mortal Python 环境运行；Mortal 工作目录应包含有效 config 和已有模型。下例的 `REPO`、`MORTAL_DIR` 是各自 Linux 绝对路径，输出目录必须尚不存在：

```sh
cd "$MORTAL_DIR"
./venv/bin/python "$REPO/tools/evaluation/arena.py" \
  --policy-command "[\"dotnet\",\"$REPO/Mahjong.Policy.Mjai/bin/Release/net8.0/Mahjong.Policy.Mjai.dll\"]" \
  --mortal-dir "$MORTAL_DIR" --opponent mortal --match-mode libriichi-hanchan \
  --split development --groups 1 --output "$REPO/artifacts/evaluation/dev-001"
```

WSL 可把 `--policy-command` 指向 Windows `dotnet.exe` 和 Windows DLL 路径，参数必须为正确 JSON 数组。采用 subprocess 参数列表处理空格；不要拼接 shell 命令。完整正式验收改用 `--split acceptance --groups 1000`。这是 8,000 场，运行时间可能很长；本次未执行。GitHub 手动 evaluation workflow 需要带 `mortal` 标签的自托管 Linux runner，并配置 `MORTAL_DIR` 和 `MORTAL_PYTHON`。

## 种子、可见信息和结果

- 训练种子从 100000 开始，开发从 1000000 开始，最终验收从 10000000 开始；每段 100000 个，`--offset` 只能在段内取值。
- 适配器把自身旋转为 0，隐藏对手起手牌和摸牌，删除 meta；合法动作由完整引擎提供。C# 进程只收到该玩家可见事件。
- 50 ms 搜索受硬件、JIT 与系统负载影响，超时会返回基线；相同种子并不保证两台机器逐动作一致，正式比较应固定运行环境。

- 输出 `manifest.json` 记录引擎 commit、原模型 SHA256、策略程序集哈希、schema、种子、规则和模式；`results.jsonl` 记录每场座位/得分/名次/和牌/放铳，压缩 MJAI 日志可重放。
- `report.json` 汇总平均名次、一位/四位率、平均得失分、和牌/放铳率。以 seed 组为单位计算配对 bootstrap 95% 区间；一个 seed 组返回空区间，不允许晋级。
- 只有固定 Mortal 对手的 acceptance ≥1000 组才检查统计门槛：名次差 ≤ -0.03 且区间上界 <0，四位率差区间上界 ≤0.01。报告的统计通过只属于当前引擎环境；`doman_promotion_eligible=false` 阻止将其用于 FF14 默认策略晋级。还需兼容引擎及独立操作/协议回归；脚本不会修改插件默认配置。
- `--opponent tsumogiri` 只用于连接测试，不能晋级。开发/训练集不能用于最终验收结论。

## 训练校准与影子比较

`--split train` 额外导出仅训练种子的 `training-decisions.jsonl`。用以下命令生成实验校准文件：

```sh
python3 tools/evaluation/calibrate.py artifacts/evaluation/train-001/training-decisions.jsonl artifacts/evaluation/train-001/calibration.json
```

校准器拒绝开发/验收种子；输出记录数据 SHA256。每个分桶至少 50 条，且候选均有校准值才采用名次、风险、得分排序，否则回到基线/搜索。离线可传 `--calibration-path`（C# 进程可见路径）；插件 `CalibrationPath` 配置同理。小样本生成文件只是链路测试，本次不发布默认校准参数。

经验分桶存在选择偏差，不能单凭训练误差判定策略有效。应冻结训练集、校准文件与策略版本，先开发集逐项消融，再使用未见过的验收组；评测同一组时不得训练。插件增强策略默认关闭，开启后默认影子模式，`shadow` 日志记录两种选择与耗时。

## 规则差异与报告边界

libriichi 使用其标准四人半庄规则。游戏客户端比赛长度、连庄/延长局、流局条件、同分排序、鸣牌合法性与 UI 时间仍需逐项验证；当前报告记录了这些差异，不能宣称完全等价。此环境不模拟网络丢包、游戏延迟、弹窗或进程超时损失。策略对战、协议恢复回归与真实游戏统计必须分别报告。

CI 普通测试不更新 golden：缺文件或空样本目录即失败。显式 `UPDATE_REPLAY_SNAPSHOTS=1` 和 `MJ_REGEN_FIXTURES=1` 仅用于有意基线更新，更新后必须审查差异。

### 官方规则核对补充（2026-09-24）

官方已明确快速/完整比赛、同分排序及特殊结算的规则文本，见 [规则差距审查](reviews/20260924-official-rules-audit.md)。待验证的是本机当前模式识别和引擎实现差异，不需要重新用实战证明规则存在。已检查本机引擎源码，当前 `--match-mode libriichi-hanchan` 对应标准半庄且可能延长局，末局庄家结束要求达到 30000 分。能力声明统一提供 `scheduled_rounds=2`；请求 `doman-quick` 或 `doman-full` 会在创建输出和启动进程前明确报错。报告逐项列出规则差异，并固定 `doman_promotion_eligible=false`。在引擎结束条件和特殊结算适配前，不能宣称它代表 FF14 快排上分效果。


## Mortal 自对局训练数据（2026-09-25）

`tools/evaluation/self_play.py` 使用现有 Mortal 权重驱动完整 libriichi 对局，生成训练候选数据。它不更新权重，不修改在线配置，也不把模拟成绩混入真实游戏统计。运行环境与上文相同；使用已安装依赖的 Python：

```sh
python "$REPO/tools/evaluation/self_play.py" \
  --mortal-dir "$MORTAL_DIR" --split train --groups 2 --offset 100 \
  --output "$REPO/artifacts/selfplay/train-001"
```

输出目录必须不存在。训练、开发、验收使用隔离种子区间；跨次运行需自行分配未使用的 offset。训练默认 epsilon=0.02、temperature=0.05；开发与验收不启用探索。记录模型实际加载文件的 SHA256、引擎 commit、源文件校验值、种子、设备和规则，便于追溯。

每个种子在四个座位各运行一次。相同模型可能产生完全重复的对局，脚本按牌局事件去重；`generated_matches` 不能当作独立样本量。`raw/` 保留完整模拟日志，`matches.jsonl` 标注重复项，训练只使用 `unique/*.json.gz`。该去重范围是单次运行，合并多批数据仍需按 match_id 去重，并避免训练/验收交叉。

完整原始日志包含模拟器已知的全牌信息。脚本使用 `GameplayLoader(oracle=False, trust_seed=False)` 检查四名玩家视角、不产生不可见特征且动作符合合法掩码；训练时仍须保持非 oracle 配置。不能把完整日志直接拼成玩家输入。最终 manifest 仅在全部对局验证完成后标为 complete；running 状态表示未完成，不应进入训练。

这条入口仅验证牌谱能通过 GameplayLoader。完整 Mortal 训练还需要 GRP 排名预测模型、独立训练配置和奖励设计；它不是一键训练脚本。当前引擎仍是标准半庄，与 FF14 快速东风模式的结束条件不等价，不能用模拟结果宣布 FF14 上分提升。

本机冒烟验证：2 个训练种子，8 个物理输出，去重后 2 场、25 手局、1,615 条玩家决策；包含 3 次加杠、1 次暗杠和 4 次宝牌更新。耗时约 176 秒，只说明该小批次运行成功，不代表大规模吞吐。真实网络同步仍由捕获回放验证。详见 [预训练权重与训练建议](reviews/20260925-pretrained-and-training.md)。


## 两份 Mortal 权重的半庄对照

`tools/evaluation/compare_models.py` 用于比较两份兼容 Mortal checkpoint。它独立加载候选、基线及固定对手；每个种子先后执行候选对三名固定对手、基线对相同三名对手，各轮换四座，共八场。各批交替运行先后次序，同批的两方始终使用相同种子区间和批大小。模型读取只使用当前玩家可见信息，关闭探索与混合精度，不修改在线配置或模型文件。

本机 WSL 示例（输出目录必须尚不存在）：

```powershell
wsl -d Ubuntu --cd /mnt/e/SapphireServer/FFXIV-AutoMahjongSolver/.loacl/Mortal/mortal --exec python /mnt/e/SapphireServer/FFXIV-AutoMahjongSolver/tools/evaluation/compare_models.py --mortal-dir /mnt/e/SapphireServer/FFXIV-AutoMahjongSolver/.loacl/Mortal/mortal --candidate patch/mortal_298k.pth --groups 2 --offset 300 --output /mnt/e/SapphireServer/FFXIV-AutoMahjongSolver/artifacts/evaluation/models-dev-001
```

`--candidate` 必填；`--baseline` 默认读取当前 config 中的权重，`--opponent` 默认与基线相同。三项权重路径均相对于 Mortal 工作目录，也可传绝对路径。默认读取已配置设备，可用 `--device cpu` 显式覆盖；CPU 模式可能较慢。`--threads` 默认 2。两份本地文件存在只证明可以尝试加载，不证明候选来源、授权或训练质量已验证。

首次使用应先把 candidate 设为当前基线文件，执行 `--groups 1` 做同模型对照；四座的完整牌局事件、名次、得分和和牌/放铳统计必须逐一相同，否则评测失败。不能把这四座当四个独立随机种子。新模型小样本对照仍只是流程验证，不应据此替换在线权重。

每个输出目录包含：

- `manifest.json`：实际加载权重哈希、架构、引擎和运行参数、种子区间，以及 running/failed/complete 状态。只有 complete 可作为完整报告；失败时保留此前完整配对组供诊断。
- `results.jsonl`：每个种子一行，候选/基线各四座；每场保存原始牌谱哈希、完整牌局事件哈希、名次和和牌/放铳次数。只有八场均完整且座位/种子对应时才写入该组。
- `report.json` 与 `report.md`：平均名次、前二率、一位/四位率、牌桌得失分、每手和牌/放铳率，以及按种子组计算的配对区间。
- `batch-*/candidate` 和 `batch-*/baseline`：原始压缩 MJAI 牌谱，结果中的 log 为相对输出目录路径；早期单组运行使用 seed-* 目录。原始牌谱包含全局信息，只供事后校验，不进入模型不可见输入。

开发阶段使用 `--split development`。正式统计验收使用未被训练或开发选模使用的 `--split acceptance --groups 1000`，共 8,000 场；这可能需要较长时间，默认不会启动。每次使用新的 offset 和输出目录，跨批次需核对种子重复，不能把重复运行算成更多独立证据。统计门槛沿用平均名次至少改善 0.03 且 95% 区间上界小于 0、四位率差区间上界不超过 0.01。

本阶段优先半庄，因为用户两种模式都接受，且现有引擎本来支持半庄。FF14 半庄与当前标准天凤半庄仍有结束条件和特殊结算差异，报告继续明确 `doman_promotion_eligible=false`，不把牌桌得失分换算为 Rating，也不自动替换线上模型。没有改动自动排队模式；本轮交付针对离线模型评测。


### 批量开发筛选

`--batch-groups` 控制一次送入 GPU 的独立种子组数量，默认 1、上限 256；最后一批自动缩小到剩余组数。批量执行不改变“一名候选对三名固定对手”的设置，也不把整批当成一个统计样本：每个种子依然独立保存候选/基线各四座。批内每个种子的四张座位牌谱及整批引擎名次都必须完整对应；两方均完成并验证之后才记录该批结果。

本轮 100 组开发筛选使用以下参数（在上面的命令中替换）：

```text
--groups 100 --batch-groups 20 --offset 1000 --split development
--output /mnt/e/SapphireServer/FFXIV-AutoMahjongSolver/artifacts/evaluation/models-298k-dev100-20260925
```

这会使用开发种子 1001000–1001099，候选与基线各 400 场，总计 800 场；不混入此前 1000300–1000301 的小样本，也不使用最终验收种子。运行期间可查看 manifest 的 completed_groups 和 elapsed_seconds。不同批大小可能因浮点计算产生动作差异，因此正式复现须固定该参数；使用同一批大小比较两边，并先通过同模型对照。显存不足时任务失败，不能把已完成局数当作完整筛选报告。


本轮运行已完成：100 组、800 场，约 19 分 15 秒；先行与补充同模型对照均通过。平均名次差 −0.0625 的 95% 区间跨过 0，因此保留当前在线权重。见 [完整筛选报告](reviews/20260925-mortal-dev100.md)。


## FF14 东风／半庄离线实验引擎

`compare_models.py` 现在接受 `doman-quick`（东风）及 `doman-full`（半庄）。这两个模式必须显式指定受测的独立引擎；标准 `arena.py` 内置策略入口和 `self_play.py` 仍保持原来的标准半庄，不能用改标签的方式生成东风数据。

在 WSL／Linux 中构建（使用运行 Mortal 的同一个 Python，需已安装 Rust、Git 和锁定依赖）：

```sh
REPO=/mnt/e/SapphireServer/FFXIV-AutoMahjongSolver
python "$REPO/tools/evaluation/build_doman_engine.py" \
  --mortal-source "$REPO/.loacl/Mortal" \
  --output "$REPO/artifacts/doman-engine-v1"
```

输出目录必须不存在。脚本从 Mortal `0cff2b52982be5b1163aa9a62fb01f03ce91e0d2` 的 Git 对象导出源码，应用仓库内的 `tools/evaluation/patches/libriichi-doman.patch`，运行原生回归测试并编译 `libriichi_doman.so`。补丁基于 Equim-chan/Mortal，沿用上游 AGPL-3.0-or-later 授权。源码、测试日志、库文件及身份清单都留在输出目录；构建失败不会获得可用状态。原来的 `libriichi.so`、在线配置和权重不被替换。

实际东风对照示例（候选及基线各轮换四座，20 组共 160 场）：

```sh
python "$REPO/tools/evaluation/compare_models.py" \
  --mortal-dir "$REPO/.loacl/Mortal/mortal" \
  --candidate patch/mortal_298k.pth   --match-mode doman-quick   --doman-engine "$REPO/artifacts/doman-engine-v1" \
  --groups 20 --batch-groups 20 --offset 2100 \
  --output "$REPO/artifacts/evaluation/doman-quick-dev20-20260925"
```

换成 `--match-mode doman-full` 即为实验半庄。清单记录补丁、库文件和测试日志的哈希；补丁更新、库文件被改动或构建未通过测试时拒绝使用。牌谱验收额外禁止东风进入南场、半庄进入西场，允许连庄及负分提前结束。

已适配的差异：东四／南四终局、无延长赛、最后庄家首位和牌或听牌结束且不要求 30,000 分；三家荣和流局、四家立直继续、第五杠流局；天然役满与天和／地和组合及四倍封顶。保留真实场风和旧模型观察编码，终局和牌／流局规则辅助判断使用真实比赛长度。

这仍是实验规则档案，不等于完整 FF14 一致性认证。四家立直继续和东风不进入南场分别依据官方完整流局列表及短局长度推导，尚缺受控游戏验证；双荣和本场与供托分配、包牌边界、立直后杠的听牌限制仍需补实战对照。80／120 分钟游戏计时、输入延迟和 Rating 未模拟。半庄权重没有新增“东风长度”训练特征，不把东场伪装成南场来欺骗模型。结果不会自动触发线上换权重，也不能直接当作 FF14 胜率保证。

官方依据：[高级规则](https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/goldsaucer/doman-mahjong/special_rule/)、[5.4 版本加入 Quick Match](https://na.finalfantasyxiv.com/lodestone/topics/detail/230f6e0af3536f9a40654d783c8c2f1094131070)。

首批东风验证已完成：20 组、160 场模型比较，另有 160 场同模型对照。见 [结果与验收记录](reviews/20260925-doman-quick-dev20.md)。
