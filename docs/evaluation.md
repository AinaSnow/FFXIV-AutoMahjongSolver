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
