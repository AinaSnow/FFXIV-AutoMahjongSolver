# 已有归档审计与回放

`tools/audit_live_archives.py` 仅读取现有文件。它不启动游戏、不改配置、不执行模型建议，也不把回放成功当作胜率证明。

## 用法

在项目根目录，先按锁定依赖构建生产解码器：

```powershell
dotnet build tools/Mahjong.PacketReplay/Mahjong.PacketReplay.csproj -c Release -p:RestoreLockedMode=true
$configDir = Join-Path $env:APPDATA 'XIVLauncher/pluginConfigs/Mahjong.Plugin.Dalamud'
python -X utf8 tools/audit_live_archives.py --archives "$configDir/match-archives" --captures "$configDir/debug-packets" --profile data/protocols/20260915-international-candidate.json --decoder tools/Mahjong.PacketReplay/bin/Release/net8.0/Mahjong.PacketReplay.dll --output artifacts/live-audit-new
```

仅审计摘要时省略 captures/profile/decoder 三个参数。输出目录必须不存在；再次运行换一个目录名，不能覆盖已有证据。

工具扫描每个 `match-*` 目录，包括没有 summary 的目录。摘要 schema 4/5 的决策、执行与异常计数会被校验、汇总并按构建 ID 分组；更早 schema 记为证据不足，不猜测缺失字段。schema 4 的终局观测为未知，不从最后分数推断排名。本工具不统计总战绩，请使用有完整比赛边界证据的战绩分析。

可选抓包回放调用生产 CLI，逐文件保留结果；不排序或补写输入包、不合成缺失的结果边界。子进程默认每份最多 120 秒，可用 `--timeout-seconds` 修改；超时/错误记录后继续处理下一份。空目录、损坏和缺摘要均不能得到全量成功。未映射 opcode 保留清单，不声称与麻将无关。

## 输出和退出码

- `report.json`：工具、摘要、抓包及解码器哈希，按构建的计数，各文件错误与每手场风/局号/本场/结果边界。
- `report.md`：汇总阅读入口。
- `replay-NNNN.json`：生产解码器输出，可以继续送入 [真实 Mortal 离线回放](offline-mortal-replay.md)。
- 退出 0：所选摘要均支持且审计/导出无文件错误。仍须查看每手 `offline_model_eligible`，导出成功并不代表所有手局完整或协议认证。
- 退出 1：完整输出已保存，但有损坏、缺失、不支持摘要或抓包导出失败。
- 退出 2：输入/输出参数无效，例如空语料、输出目录已存在。

摘要统计是已记录的计数，尚未独立核对逐动作原始日志；`state-changed` 也不等价于服务端确认。Mortal、本地兜底、UI 恢复、和牌保护分别展示。异常计数会重叠，例如同一次超时可能同时记在 timeout_fallbacks 和 decision_timeouts，不能相加。

`estimated_point_loss` 和 `estimated_rank_loss` 保持 null。未出现的稀有场景是证据不足。原始文件、模型响应和日志均留在忽略目录，不提交私人牌谱。

## 启动耗时诊断

`tools/mortal_runner.py` 在模型准备完成（或异常退出）时，向 **stderr** 写一条 `type=mortal_startup` 的 JSON：

| 字段 | 范围 |
|---|---|
| imports_ms | prelude、torch、config、model、engine 导入 |
| device_check_ms | CUDA 可用性检查、设备选择 |
| checkpoint_read_hash_load_ms | 读取、哈希和反序列化同一份权重 |
| model_construct_load_ms | 模型构造与载入 state dict |
| engine_device_setup_ms | MortalEngine 构造及其设备准备 |
| bot_setup_ms | libriichi Bot 构造 |
| total_ms | Python 进入初始化至 Bot 就绪 |

使用单调时钟。stdout 仍只输出原 session/hand/sequence 关联响应；模型、合法动作和超时默认值不变。设备阶段测量的是调用返回时间，没有强制 CUDA synchronize；异步设备工作可能体现在首次推理中。总时间不包括 WSL/解释器启动和此前的标准库导入，也不等于宿主 first_ack_ms。

离线真实回放逐手创建新进程，因此可比较首个进程与后续有缓存的启动；不能称它们为九次独立冷启动。进程若在准备期间被杀死，可能没有这条终结诊断，需结合宿主 child_start/first_write/first_ack。具体本轮数字见 [2026-10-03 验收](reviews/20261003-offline-audit.md)。
